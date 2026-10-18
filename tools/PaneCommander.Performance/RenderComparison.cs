using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Performance;

// A/B benchmark only. No forced GC, no changes to production configuration.
internal static class RenderComparison
{
    public static int Run(string scenario, string fixture, string output, string mode)
    {
        if (mode is not ("A" or "B") || scenario is not ("T1" or "T2" or "T3" or "T4" or "T5" or "T6" or "T7" or "T8" or "T9" or "R1" or "R2" or "R3" or "GUI")) return 2;
        RenderOptions.ProcessRenderMode = mode == "A" ? RenderMode.Default : RenderMode.SoftwareOnly;
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        MainViewModel? model = null;
        if (scenario != "T1")
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/PaneCommander.App;component/Themes/Controls.xaml") });
            model = new(new FileSystemService(), new ErrorLogger(Path.Combine(output, "errors")));
        }
        using var ownedModel = model;
        Window window = model == null ? new Window { Width = 1280, Height = 800, Title = "PaneCommander comparison empty", UseLayoutRounding = true }
            : new MainWindow { DataContext = model };
        window.ShowActivated = false;
        window.ShowInTaskbar = true;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 60; window.Top = 60;
        var clock = Stopwatch.StartNew();
        var measurements = new List<object>();
        var checkpoints = new List<object>();
        var uiDelays = new List<double>();
        var phases = new List<object>();
        var exit = 0;
        window.Loaded += async (_, _) =>
        {
            try
            {
                if (model != null)
                {
                    await Wait();
                    foreach (var pane in Panes()) await pane.SelectedTab.NavigateAsync(Path.Combine(fixture, "normal"));
                    if (scenario is "T3" or "T6" or "GUI")
                        foreach (var pane in Panes()) for (var i = 0; i < 2; i++) { pane.AddTab(); await Wait(); }
                    if (scenario is "T4" or "T9") await model.Workspace.Left.SelectedTab.NavigateAsync(Path.Combine(fixture, "large"));
                    if (Panes().Any(p => p.SelectedTab.Entries.Count != (p.Side == PaneSide.Left && scenario is "T4" or "T9" ? 10000 : 256)))
                        throw new InvalidOperationException("Fixture count mismatch");
                }
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(10000);
                var dpi = VisualTreeHelper.GetDpi(window);
                Phase(scenario == "T5" ? "idle" : "baseline");
                Write("ready.json", new
                {
                    scenario,
                    mode,
                    pid = Environment.ProcessId,
                    renderPreference = RenderOptions.ProcessRenderMode.ToString(),
                    renderCapabilityTier = RenderCapability.Tier >> 16,
                    runtime = RuntimeInformation.FrameworkDescription,
                    os = RuntimeInformation.OSDescription,
                    dpiX = dpi.DpiScaleX,
                    dpiY = dpi.DpiScaleY,
                    window.Width,
                    window.Height,
                    screenWidthDip = SystemParameters.PrimaryScreenWidth,
                    screenHeightDip = SystemParameters.PrimaryScreenHeight,
                    power = Power(),
                    warmupSeconds = 10,
                    offscreen = false,
                    interactiveDriver = false,
                    window.IsActive,
                    settings = model?.Settings,
                    modules = Modules()
                });
                Checkpoint("baseline");
                if (scenario == "GUI")
                {
                    Phase("gui-manual");
                    while (!File.Exists(Path.Combine(output, "finish.txt")))
                    { if (clock.Elapsed.TotalMinutes > 30) throw new TimeoutException("GUI run timeout"); await Task.Delay(250); }
                }
                else if (scenario == "T5") await Task.Delay(300000);
                else if (scenario is "T1" or "T2" or "T3" or "T4") await Task.Delay(10000);
                else
                {
                    await Task.Delay(5000);
                    Phase("operations");
                    Checkpoint("before-operations");
                    using var cancel = new CancellationTokenSource();
                    var probe = Task.Run(async () =>
                    {
                        try
                        {
                            while (true)
                            {
                                await Task.Delay(100, cancel.Token);
                                var queued = Stopwatch.GetTimestamp();
                                await window.Dispatcher.InvokeAsync(() => uiDelays.Add(Stopwatch.GetElapsedTime(queued).TotalMilliseconds), DispatcherPriority.Input, cancel.Token);
                            }
                        }
                        catch (OperationCanceledException) { }
                    });
                    for (var i = 0; i < 100; i++)
                    {
                        var operation = scenario;
                        var watch = Stopwatch.StartNew();
                        if (scenario == "T6")
                        {
                            model!.Workspace.Toggle();
                            var pane = model.Workspace.ActivePane;
                            pane.SelectedTab = pane.Tabs[(i / 2 + 1) % 3];
                        }
                        else if (scenario == "T7")
                            await model!.Workspace.Left.SelectedTab.NavigateAsync(Path.Combine(fixture, i % 2 == 0 ? "normal/child" : "normal"));
                        else if (scenario == "T8")
                        {
                            var pane = i % 2 == 0 ? model!.Workspace.Left : model!.Workspace.Right;
                            var added = pane.AddTab(); await Wait();
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            pane.CloseTab(added);
                        }
                        else if (scenario == "R1")
                            await model!.ApplySettingsAsync(ColorSchemes.Apply(model.Settings, ColorSchemes.Presets[i % 3]));
                        else if (scenario == "R2")
                            await model!.ApplySettingsAsync(model.Settings with { FontFamily = new[] { "Segoe UI", "Yu Gothic UI", "Meiryo" }[i % 3] });
                        else if (scenario == "R3")
                        {
                            // Component driver, not mouse/keyboard GUI acceptance.
                            var dialog = new SettingsDialog(model!) { Owner = window, ShowActivated = false };
                            dialog.Show();
                            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                            dialog.Close();
                        }
                        else
                        {
                            if (i < 50)
                            {
                                operation = "scroll";
                                var list = Walk(window).OfType<ListView>().First(v => v.DataContext == model!.Workspace.Left.SelectedTab);
                                var scroll = Walk(list).OfType<ScrollViewer>().First();
                                scroll.ScrollToVerticalOffset(i % 2 == 0 ? (i + 1) * 120 : 0);
                            }
                            else
                            {
                                operation = "resize";
                                window.Width = i % 2 == 0 ? 1000 : 1280;
                                window.Height = i % 2 == 0 ? 650 : 800;
                            }
                        }
                        await Wait();
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        var layoutMs = watch.Elapsed.TotalMilliseconds;
                        var frame = await NextRendering();
                        measurements.Add(new
                        {
                            index = i,
                            operation,
                            layoutReadyMs = layoutMs,
                            renderingEventMs = frame ? watch.Elapsed.TotalMilliseconds : (double?)null,
                            frameTimeout = !frame
                        });
                    }
                    cancel.Cancel(); await probe;
                    Checkpoint("after-operations");
                    if (scenario == "T9") { window.Width = 1280; window.Height = 800; }
                    Phase("recovery");
                    await Task.Delay(10000);
                }
                Checkpoint("final");
                Phase("end");
                Write("completed.json", new
                {
                    scenario,
                    mode,
                    measurements,
                    uiThreadQueueDelayMs = uiDelays,
                    checkpoints,
                    phases,
                    power = Power(),
                    modules = Modules(),
                    operationCompletion = "NavigateAsync/TabState change followed by Dispatcher.ApplicationIdle (layout/bindings); renderingEvent is CompositionTarget.Rendering notification, NOT display present completion",
                    forcedGc = false
                });
                await Task.Delay(1000);
            }
            catch (Exception ex) { Write("failure.json", new { error = ex.ToString() }); exit = 1; }
            finally { window.Close(); app.Shutdown(); }
        };
        app.Run(window);
        return exit;

        IEnumerable<PaneState> Panes() => model == null ? [] : [model.Workspace.Left, model.Workspace.Right];
        async Task Wait()
        {
            var watch = Stopwatch.StartNew();
            while (Panes().Any(p => p.Tabs.Any(t => t.IsLoading)))
            { if (watch.Elapsed.TotalSeconds > 60) throw new TimeoutException("Folder load"); await Task.Delay(10); }
        }
        void Checkpoint(string stage)
        {
            var gc = GC.GetGCMemoryInfo();
            checkpoints.Add(new
            {
                stage,
                elapsedSeconds = clock.Elapsed.TotalSeconds,
                gcHeapSizeBytesAtLastGc = gc.HeapSizeBytes,
                gcCommittedBytesAtLastGc = gc.TotalCommittedBytes,
                managedBytesWithoutGc = GC.GetTotalMemory(false),
                gcIndex = gc.Index,
                gcCollections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray(),
                tabs = Panes().Sum(p => p.Tabs.Count),
                entries = Panes().Sum(p => p.Tabs.Sum(t => t.Entries.Count)),
                realizedRows = Walk(window).OfType<ListViewItem>().Count()
            });
        }
        void Phase(string phase)
        {
            var value = new { phase, elapsedSeconds = clock.Elapsed.TotalSeconds };
            phases.Add(value);
            Write("phase.json", value);
        }
        void Write(string name, object value)
        {
            var path = Path.Combine(output, name);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
    }
    private static object Power()
    {
        var available = GetSystemPowerStatus(out var value);
        return new { available, acLineStatus = value.AcLineStatus, batteryPercent = value.BatteryPercent, batteryFlag = value.BatteryFlag };
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLineStatus, BatteryFlag, BatteryPercent, Reserved; public uint LifeTime, FullLifeTime; }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
    private static object[] Modules()
    {
        using var process = Process.GetCurrentProcess();
        return process.Modules.Cast<ProcessModule>().Select(m => (object)new { m.ModuleName }).ToArray();
    }
    private static async Task<bool> NextRendering()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => complete.TrySetResult();
        CompositionTarget.Rendering += handler;
        try { return await Task.WhenAny(complete.Task, Task.Delay(1000)) == complete.Task; }
        finally { CompositionTarget.Rendering -= handler; }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var item in Walk(child)) yield return item; }
    }
}

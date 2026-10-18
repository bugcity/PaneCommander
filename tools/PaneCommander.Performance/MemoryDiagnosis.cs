using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Performance;

// Diagnostic-only forced collections separate retained objects from pending garbage.
// These results must never replace the no-forced-GC acceptance measurements.
internal static class MemoryDiagnosis
{
    public static int Run(string fixture, string output, string mode)
    {
        if (mode == "lifecycle") return VisualLifecycle.Run(fixture, output);
        if (mode == "runtime") return RunRuntime(output);
        if (mode is not ("empty" or "styled" or "model2" or "model6" or "ui2" or "ui6" or "stages")) return 2;
        Directory.CreateDirectory(output);
        var software = Environment.GetEnvironmentVariable("PANECOMMANDER_DIAGNOSTIC_SOFTWARE") == "1";
        if (software) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (mode != "empty") app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/PaneCommander.App;component/Themes/Controls.xaml") });
        MainViewModel? model = mode is "empty" or "styled" ? null
            : new(new FileSystemService(), new ErrorLogger(Path.Combine(output, "errors")));
        var full = mode is "ui2" or "ui6" or "stages";
        Window window = full ? new MainWindow { DataContext = model }
            : new Window { Width = 1280, Height = 800, UseLayoutRounding = true };
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -20000;
        window.ShowActivated = window.ShowInTaskbar = false;
        var samples = new List<object>();
        var result = 0;
        window.Loaded += async (_, _) =>
        {
            try
            {
                if (model != null)
                {
                    if (!full) await model.Workspace.InitializeAsync();
                    await Wait(model);
                    foreach (var pane in Panes(model)) await pane.SelectedTab.NavigateAsync(Path.Combine(fixture, "normal"));
                    if (mode is "model6" or "ui6") await AddFour(model);
                }
                await Sample("initial");
                if (mode == "stages" && model != null)
                {
                    await AddFour(model);
                    await Sample("six-added");
                    for (var i = 0; i < 50; i++)
                    {
                        model.Workspace.Toggle();
                        var pane = model.Workspace.ActivePane;
                        pane.SelectedTab = pane.Tabs[i % pane.Tabs.Count];
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    }
                    await Sample("six-switched-50");
                    foreach (var pane in Panes(model))
                        while (pane.Tabs.Count > 1) pane.CloseTab(pane.Tabs[^1]);
                    await Wait(model);
                    await Sample("two-after-close");
                }
                File.WriteAllText(Path.Combine(output, "memory.json"), JsonSerializer.Serialize(new
                { mode, software, pid = Environment.ProcessId, runtime = Environment.Version.ToString(), samples }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); result = 1; }
            finally { window.Close(); model?.Dispose(); app.Shutdown(); }
        };
        app.Run(window);
        return result;

        async Task Sample(string stage)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(10000);
            samples.Add(Capture(stage, false));
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(1000);
            samples.Add(Capture(stage, true));
        }
        object Capture(string stage, bool forcedGc)
        {
            // Obtain related counters in one snapshot; GC info refers to the most recent GC.
            var managed = GC.GetTotalMemory(false);
            var gc = GC.GetGCMemoryInfo();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var privateBytes = process.PrivateMemorySize64;
            var workingSet = process.WorkingSet64;
            var visual = Walk(window).ToArray();
            var modules = process.Modules.Cast<ProcessModule>().Select(m => new { m.ModuleName, m.ModuleMemorySize }).ToArray();
            var dateCells = (window as MainWindow)?.GetType().GetProperty("DateCells", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window);
            var dateCount = dateCells?.GetType().GetProperty("Count")?.GetValue(dateCells);
            return new
            {
                stage, forcedGc, utc = DateTime.UtcNow, privateBytes, workingSet,
                managedBytes = managed, gcCommittedBytes = gc.TotalCommittedBytes,
                gcHeapSizeBytes = gc.HeapSizeBytes, gcFragmentedBytes = gc.FragmentedBytes,
                allocatedBytes = GC.GetTotalAllocatedBytes(),
                collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray(),
                handles = process.HandleCount, threads = process.Threads.Count,
                tabs = model == null ? 0 : Panes(model).Sum(p => p.Tabs.Count),
                entries = model == null ? 0 : Panes(model).Sum(p => p.Tabs.Sum(t => t.Entries.Count)),
                visualCount = visual.Length, folderViews = visual.OfType<FolderView>().Count(),
                realizedRows = visual.OfType<ListViewItem>().Count(), dateCount, modules
            };
        }
    }
    private static int RunRuntime(string output)
    {
        Directory.CreateDirectory(output);
        var samples = new List<object>();
        Thread.Sleep(10000);
        samples.Add(Capture(false));
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        Thread.Sleep(1000);
        samples.Add(Capture(true));
        File.WriteAllText(Path.Combine(output, "memory.json"), JsonSerializer.Serialize(new
        { mode = "runtime", pid = Environment.ProcessId, runtime = Environment.Version.ToString(), samples }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
        static object Capture(bool forcedGc)
        {
            var managed = GC.GetTotalMemory(false);
            var gc = GC.GetGCMemoryInfo();
            using var process = Process.GetCurrentProcess();
            return new { stage = "initial", forcedGc, privateBytes = process.PrivateMemorySize64,
                workingSet = process.WorkingSet64, managedBytes = managed, gcCommittedBytes = gc.TotalCommittedBytes };
        }
    }
    private static IEnumerable<PaneState> Panes(MainViewModel model) => [model.Workspace.Left, model.Workspace.Right];
    private static async Task AddFour(MainViewModel model)
    {
        foreach (var pane in Panes(model))
            for (var i = 0; i < 2; i++) { pane.AddTab(); await Wait(model); }
    }
    private static async Task Wait(MainViewModel model)
    {
        var watch = Stopwatch.StartNew();
        while (Panes(model).Any(p => p.Tabs.Any(t => t.IsLoading)))
        {
            if (watch.Elapsed.TotalSeconds > 60) throw new TimeoutException();
            await Task.Delay(10);
        }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var item in Walk(child)) yield return item;
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Performance;

/// <summary>アプリ本体に監視タイマーを組み込まない、別実行ファイルの再現用ドライバー。</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 8 && args[0] == "shell-phases") return ShellPhaseMeasurement.Run(args[1],args[2],int.Parse(args[3]),int.Parse(args[4]),int.Parse(args[5]),args[6],args[7]);
        if (args.Length == 3 && args[0] == "background-hosted") return HostedBackgroundProbe.Run(args[1],args[2]);
        if (args.Length == 3 && args[0] == "clipboard-sync-probe") return ClipboardAsyncProbe.Run(args[1],args[2]);
        if (args.Length == 2 && args[0] == "clipboard-isolation") return ClipboardIsolation.Run(args[1]);
        if (args.Length == 2 && args[0] == "background-readonly") return ClipboardIsolation.ReadOnlyBackground(args[1]);
        if (args.Length == 3 && args[0] == "capture-probe") return CaptureProbe.Run(args[1],args[2]);
        if (args.Length == 2 && args[0] == "desktop-environment") return DesktopEnvironment.Run(args[1]);
        if (args.Length == 3 && args[0] == "shell-lifetime") return ShellLifetimeDiagnosis.Run(args[2],args[1]);
        if (args.Length == 2 && args[0] == "shell-menu") return ShellMenuMeasurement.Run(args[1]);
        if (args.Length == 3 && args[0] == "shell-menu-type") return ShellMenuMeasurement.Run(args[2],args[1]);
        if (args.Length == 4 && args[0] == "shell-menu-type-count") return ShellMenuMeasurement.Run(args[2],args[1],int.Parse(args[3]));
        if (args.Length == 5 && args[0] == "render" && args[1] == "FILTER") return FilterComparison.Run(args[3],args[4]);
        if (args.Length == 5 && args[0] == "render") return RenderComparison.Run(args[1], args[2], args[3], args[4]);
        if (args.Length == 4 && args[0] == "memory") return MemoryDiagnosis.Run(args[1], args[2], args[3]);
        if (args.Length != 4) { Console.Error.WriteLine("scenario fixture-directory output-directory observation-seconds"); return 2; }
        var scenario = args[0];
        if (scenario is not ("six" or "large" or "repeat" or "idle" or "soak")) return 2;
        var fixture = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        var seconds = int.Parse(args[3]);
        Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PaneCommander.App;component/Themes/Controls.xaml")
        });
        var service = new CountingFileSystem();
        using var model = new MainViewModel(service, new ErrorLogger(Path.Combine(output, "errors")));
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false };
        // 画面外で描画。通常表示の入力/モニター特性は手動試験として別途必要。
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        var result = 0;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await WaitForLoads(model);
                var normal = Path.Combine(fixture, "normal");
                await model.Workspace.Left.SelectedTab.NavigateAsync(normal);
                await model.Workspace.Right.SelectedTab.NavigateAsync(normal);
                if (scenario == "six")
                    foreach (var pane in new[] { model.Workspace.Left, model.Workspace.Right })
                        for (var i = 0; i < 2; i++) { pane.AddTab(); await WaitForLoads(model); }
                if (scenario == "large")
                    await model.Workspace.Left.SelectedTab.NavigateAsync(Path.Combine(fixture, "large"));
                if (model.Workspace.Left.SelectedTab.Entries.Count != (scenario == "large" ? 10_000 : 256)
                    || model.Workspace.Right.SelectedTab.Entries.Count != 256)
                    throw new InvalidOperationException("Fixture listing did not complete as expected.");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(TimeSpan.FromSeconds(10));
                var dpi = VisualTreeHelper.GetDpi(window);
                Write(output, "ready.json", new
                {
                    scenario, pid = Environment.ProcessId, framework = RuntimeInformation.FrameworkDescription,
                    os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    dpiX = dpi.DpiScaleX, dpiY = dpi.DpiScaleY, configuration = "Release",
                    folders = model.Workspace.Left.Tabs.Concat(model.Workspace.Right.Tabs).Select(t => new { count = t.Entries.Count }).ToArray(),
                    uiControls = Descendants<ListViewItem>(window).Count(), enumerationCount = service.ReadCount,
                    offscreen = true, warmupSeconds = 10,
                    windowIsActive = window.IsActive, windowState = window.WindowState.ToString(),
                    dateTimerCount = typeof(MainWindow).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Count(f => typeof(DispatcherTimer).IsAssignableFrom(f.FieldType)),
                    managedBytesWithoutForcedGc = GC.GetTotalMemory(false), gcCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes
                });
                var before = service.ReadCount;
                var rounds = 0;
                var watch = Stopwatch.StartNew();
                if (scenario is "repeat" or "soak")
                {
                    while (watch.Elapsed.TotalSeconds < seconds + 5)
                    {
                        var pane = model.Workspace.ActivePane;
                        var tab = pane.AddTab();
                        await WaitForLoads(model);
                        await tab.NavigateAsync(Path.Combine(normal, "child"));
                        await tab.UpAsync();
                        pane.CloseTab(tab);
                        model.Workspace.Toggle();
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        rounds++;
                        await Task.Delay(scenario == "soak" ? 1000 : 50);
                    }
                }
                else await Task.Delay(TimeSpan.FromSeconds(seconds + 5));
                var idleEnumerationCount = service.ReadCount - before;
                // アイドル後、実際のWPFレイアウト完了までの遅延を観測（実入力遅延ではない）。
                var feedback = new List<double>();
                for (var i = 0; i < 50; i++)
                {
                    watch.Restart();
                    model.Workspace.Toggle();
                    var pane = model.Workspace.ActivePane;
                    pane.SelectedTab = pane.Tabs[i % pane.Tabs.Count];
                    var list = Descendants<ListView>(window).FirstOrDefault(v => v.DataContext == pane.SelectedTab);
                    if (list is not null && list.Items.Count > 0) list.SelectedIndex = i % list.Items.Count;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    feedback.Add(watch.Elapsed.TotalMilliseconds);
                }
                Write(output, "completed.json", new
                {
                    rounds, observationSeconds = seconds, idleEnumerationCount,
                    feedbackMedianMs = feedback.Order().ElementAt(feedback.Count / 2), feedbackMaxMs = feedback.Max(),
                    managedBytesWithoutForcedGc = GC.GetTotalMemory(false), gcCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
                    inactiveCachedEntries = model.Workspace.Left.Tabs.Where(t => t != model.Workspace.Left.SelectedTab).Sum(t => t.Entries.Count)
                        + model.Workspace.Right.Tabs.Where(t => t != model.Workspace.Right.SelectedTab).Sum(t => t.Entries.Count)
                });
            }
            catch (Exception ex) { Write(output, "failure.json", new { error = ex.ToString() }); result = 1; }
            finally { window.Close(); application.Shutdown(); }
        };
        application.Run(window);
        return result;
    }

    private static async Task WaitForLoads(MainViewModel model)
    {
        var timeout = Stopwatch.StartNew();
        while (model.Workspace.Left.Tabs.Concat(model.Workspace.Right.Tabs).Any(t => t.IsLoading))
        {
            if (timeout.Elapsed.TotalSeconds > 60) throw new TimeoutException("Folder loading timeout");
            await Task.Delay(10);
        }
    }
    private static void Write(string output, string name, object value) =>
        File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private sealed class CountingFileSystem : IFileSystemService
    {
        private readonly FileSystemService inner = new();
        public int ReadCount { get; private set; }
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken cancellationToken)
        {
            ReadCount++;
            return inner.ReadFolderAsync(path, cancellationToken);
        }
        public Task OpenFileAsync(FileEntry entry, CancellationToken cancellationToken) => inner.OpenFileAsync(entry, cancellationToken);
    }
}

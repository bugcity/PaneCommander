using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
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

/// <summary>Weak-only tracking: the diagnostic must not keep retired drawing objects alive.</summary>
internal static class VisualLifecycle
{
    public static int Run(string fixture, string output)
    {
        Directory.CreateDirectory(output);
        var software = Environment.GetEnvironmentVariable("PANECOMMANDER_DIAGNOSTIC_SOFTWARE") == "1";
        if (software) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/PaneCommander.App;component/Themes/Controls.xaml") });
        using var model = new MainViewModel(new FileSystemService(), new ErrorLogger(Path.Combine(output, "errors")));
        MainWindow? window = new() { DataContext = model, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        var tracker = new Tracker();
        var samples = new List<object>();
        var result = 0;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Wait();
                foreach (var pane in Panes()) await pane.SelectedTab.NavigateAsync(Path.Combine(fixture, "normal"));
                await Settle();
                await Sample("two-initial");
                foreach (var pane in Panes())
                    for (var i = 0; i < 2; i++) { pane.AddTab(); await Wait(); await Settle(); }
                await Sample("six-added");
                await Switch(18);
                await Sample("six-switched-18");
                await Switch(120);
                await Sample("six-switched-138");
                // Keep all six TabState/data objects while releasing the entire native window.
                window!.Close();
                window = null;
                app.MainWindow = null;
                await Sample("window-closed-six-states-kept");
                File.WriteAllText(Path.Combine(output, "lifecycle.json"), JsonSerializer.Serialize(new
                { software, pid = Environment.ProcessId, runtime = Environment.Version.ToString(), samples,
                    objects = tracker.Records.Select(r => new { r.Id, r.Type, r.BornStage, r.LoadedEvents, r.UnloadedEvents, r.TabIds }) },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); result = 1; }
            finally { window?.Close(); app.Shutdown(); }
        };
        window.Show();
        app.Run();
        return result;

        IEnumerable<PaneState> Panes() => [model.Workspace.Left, model.Workspace.Right];
        async Task Wait()
        {
            var watch = Stopwatch.StartNew();
            while (Panes().Any(p => p.Tabs.Any(t => t.IsLoading)))
            {
                if (watch.Elapsed.TotalSeconds > 60) throw new TimeoutException();
                await Task.Delay(10);
            }
        }
        async Task Settle()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            tracker.Observe(window, "transition");
        }
        async Task Switch(int count)
        {
            for (var i = 0; i < count; i++)
            {
                model.Workspace.Toggle();
                var pane = model.Workspace.ActivePane;
                pane.SelectedTab = pane.Tabs[(i / 2) % pane.Tabs.Count];
                await Wait();
                await Settle();
            }
        }
        async Task Sample(string stage)
        {
            await Task.Delay(3000);
            tracker.Observe(window, stage);
            samples.Add(Capture(stage, false));
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(1000);
            samples.Add(Capture(stage, true));
            var pauseStages = Environment.GetEnvironmentVariable("PANECOMMANDER_DIAGNOSTIC_PAUSE_STAGE")?.Split(',') ?? [];
            if (pauseStages.Contains(stage))
            {
                File.WriteAllText(Path.Combine(output, $"ready-{stage}.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId, stage }));
                var watch = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(output, $"continue-{stage}.txt")))
                {
                    if (watch.Elapsed.TotalSeconds > 180) throw new TimeoutException("Heap capture pause timed out");
                    await Task.Delay(100);
                }
            }
        }
        object Capture(string stage, bool forcedGc)
        {
            var managed = GC.GetTotalMemory(false);
            var gc = GC.GetGCMemoryInfo();
            using var process = Process.GetCurrentProcess();
            var privateBytes = process.PrivateMemorySize64;
            return new { stage, forcedGc, privateBytes, managedBytes = managed, gcCommittedBytes = gc.TotalCommittedBytes,
                tabs = Panes().Sum(p => p.Tabs.Count), entries = Panes().Sum(p => p.Tabs.Sum(t => t.Entries.Count)),
                tabStates = Panes().SelectMany(p => p.Tabs).Select(t => new { id = tracker.TabId(t), t.CurrentPath,
                    entries = t.Entries.Count, selected = Panes().Any(p => p.SelectedTab == t) }).ToArray(),
                tracking = tracker.Snapshot(window, Panes().Select(p => p.SelectedTab).ToArray(), Panes().SelectMany(p => p.Tabs).ToArray()),
                dateRegistry = window == null ? 0 : (int?)typeof(MainWindow).GetProperty("DateCells", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(window)?.GetType().GetProperty("Count")?.GetValue(typeof(MainWindow).GetProperty("DateCells", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window)) };
        }
    }

    private sealed class Record(int id, object value, string stage)
    {
        public int Id { get; } = id;
        public string Type { get; } = value.GetType().Name;
        public string BornStage { get; } = stage;
        public WeakReference<object> Reference { get; } = new(value);
        public int LoadedEvents;
        public int UnloadedEvents;
        public HashSet<int> TabIds { get; } = [];
    }
    private sealed class Tracker
    {
        private readonly ConditionalWeakTable<object, Record> identities = new();
        private readonly ConditionalWeakTable<TabState, TabIdentity> tabs = new();
        public List<Record> Records { get; } = [];
        private int nextTab;
        private sealed record TabIdentity(int Id);
        public int TabId(TabState tab) => tabs.GetValue(tab, _ => new(++nextTab)).Id;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Observe(Window? window, string stage)
        {
            if (window == null) return;
            Track(window, stage);
            foreach (var element in Walk(window))
            {
                if (element is not (FolderView or ListView or ListViewItem or DateCell or GridViewRowPresenter)) continue;
                var record = Track(element, stage);
                if (element is FrameworkElement { DataContext: TabState tab }) record.TabIds.Add(TabId(tab));
                if (element is DateCell cell) { Track(cell.Background, stage); Track(cell.Foreground, stage); }
            }
        }
        private Record Track(object? value, string stage)
        {
            if (value == null) throw new InvalidOperationException("Unexpected missing brush");
            return identities.GetValue(value, key =>
            {
                var record = new Record(Records.Count + 1, key, stage);
                Records.Add(record);
                if (key is FrameworkElement element)
                {
                    element.Loaded += (_, _) => record.LoadedEvents++;
                    element.Unloaded += (_, _) => record.UnloadedEvents++;
                }
                return record;
            });
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public object Snapshot(Window? window, TabState[] selected, TabState[] allTabs)
        {
            var attached = window == null ? new HashSet<DependencyObject>() : Walk(window).Append(window).ToHashSet();
            var entryOwners = new Dictionary<FileEntry, bool>(ReferenceEqualityComparer.Instance);
            foreach (var tab in allTabs) foreach (var entry in tab.Entries) entryOwners[entry] = !selected.Contains(tab);
            return Records.GroupBy(r => r.Type).Select(group =>
            {
                int alive = 0, inTree = 0, visible = 0, loaded = 0, inactiveBinding = 0, detachedInactiveBinding = 0;
                var aliveIds = new List<int>();
                foreach (var record in group)
                    if (record.Reference.TryGetTarget(out var target))
                    {
                        alive++;
                        aliveIds.Add(record.Id);
                        var isAttached = target is DependencyObject dependency && attached.Contains(dependency);
                        if (isAttached) inTree++;
                        if (target is FrameworkElement element)
                        {
                            if (element.IsVisible) visible++;
                            if (element.IsLoaded) loaded++;
                            var inactive = element.DataContext is TabState tab && !selected.Contains(tab);
                            var entry = element is DateCell cell ? cell.Entry : element.DataContext as FileEntry;
                            if (entry != null && entryOwners.TryGetValue(entry, out var inactiveEntry)) inactive |= inactiveEntry;
                            if (inactive)
                            { inactiveBinding++; if (!isAttached) detachedInactiveBinding++; }
                        }
                    }
                return new { type = group.Key, everObserved = group.Count(), alive, collected = group.Count() - alive,
                    inTree, detached = alive - inTree, visible, loaded, inactiveBinding, detachedInactiveBinding, aliveIds };
            }).ToArray();
        }
        private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var descendant in Walk(child)) yield return descendant;
            }
        }
    }
}

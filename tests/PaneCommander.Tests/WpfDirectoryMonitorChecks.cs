using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyDirectoryMonitoring()
    {
        const string first = @"C:\watch\first", second = @"C:\watch\second", next = @"C:\watch\next";
        var files = new WatchFiles(); var watches = new WatchSource();
        using var model = new MainViewModel(files, new FakeLogger());
        model.Workspace.Left.Restore(new PaneSnapshot { Tabs = [new(first), new(second)] });
        model.Workspace.Right.Restore(new PaneSnapshot { Tabs = [new(first)] });
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        WorkspaceDirectoryMonitor? monitor = null;
        try
        {
            window.Show(); Pump();
            monitor = new WorkspaceDirectoryMonitor(window, model, watches, new FakeLogger());
            void Wait(Func<bool> condition)
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!condition() && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(5); }
                Assert.IsTrue(condition(), "変更通知の反映待ち"); Pump();
            }
            Wait(() => watches.Items.Count == 2 && files.Reads.GetValueOrDefault(first) >= 4);
            CompleteWithDispatcher(Task.Delay(350)); Pump();
            var pane = model.Workspace.Left;
            var tab = pane.SelectedTab;
            var left = (PaneView)window.FindName("LeftPane");
            var list = left.CurrentFileList!;
            list.SelectedItems.Add(list.Items[30]); list.SelectedItems.Add(list.Items[31]);
            var selected = list.SelectedItems.Cast<FileEntry>().Select(e => e.FullPath).ToArray();
            left.FocusList(); Pump();
            var scroll = Descendants<ScrollViewer>(list).First(); scroll.ScrollToVerticalOffset(30); Pump();
            var anchorPath = ((FileEntry)list.Items[(int)scroll.VerticalOffset]).FullPath;
            var focused = (Keyboard.FocusedElement as FrameworkElement)?.DataContext as FileEntry;
            var reads = files.Reads[first];
            files.Extra[first] = "download.zip";
            CompleteWithDispatcher(Task.Run(() => { for (var i = 0; i < 100; i++) watches.Items[first].Changed(); }));
            Wait(() => tab.Entries.Any(e => e.Name == "download.zip") && model.Workspace.Right.SelectedTab.Entries.Any(e => e.Name == "download.zip"));
            CollectionAssert.AreEquivalent(selected, list.SelectedItems.Cast<FileEntry>().Select(e => e.FullPath).ToArray());
            Assert.IsTrue(list.IsKeyboardFocusWithin);
            Assert.AreEqual(focused?.FullPath, ((FrameworkElement)Keyboard.FocusedElement).DataContext is FileEntry focus ? focus.FullPath : null);
            Assert.AreEqual(reads + 2, files.Reads[first], "100通知を左右1回ずつへ集約する");
            Assert.AreEqual(anchorPath, ((FileEntry)list.Items[(int)scroll.VerticalOffset]).FullPath, "表示先頭の項目を保持する");
            CompleteWithDispatcher(Task.Delay(350)); Assert.AreEqual(reads + 2, files.Reads[first], "無変更時は再列挙しない");

            var heldRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            files.HoldNext = heldRead;
            files.Extra[first] = "during-read.zip"; watches.Items[first].Changed();
            Wait(() => tab.IsLoading);
            list.SelectedItems.Clear(); list.SelectedItems.Add(list.Items[1]);
            var latestSelection = ((FileEntry)list.Items[1]).FullPath;
            heldRead.SetResult();
            Wait(() => !tab.IsLoading && tab.Entries.Any(e => e.Name == "during-read.zip"));
            CollectionAssert.AreEqual(new[] { latestSelection }, list.SelectedItems.Cast<FileEntry>().Select(e => e.FullPath).ToArray(), "読込中のユーザーの選択変更を優先する");

            pane.SelectedTab = pane.Tabs[1]; Pump();
            files.Extra[first] = "renamed.zip";
            var before = files.Reads[first]; watches.Items[first].Changed();
            Wait(() => model.Workspace.Right.SelectedTab.Entries.Any(e => e.Name == "renamed.zip"));
            Assert.AreEqual(before + 1, files.Reads[first], "背景タブは列挙せず、表示中の右側だけ更新");
            pane.SelectedTab = tab; Pump();
            Wait(() => tab.Entries.Any(e => e.Name == "renamed.zip"));
            Assert.AreEqual(before + 2, files.Reads[first]);

            var toolbar = Descendants<Button>(window).Single(b => Equals(b.Content, "更新"));
            toolbar.Focus(); Pump();
            files.Extra[first] = "later.zip"; watches.Items[first].Changed();
            Wait(() => tab.Entries.Any(e => e.Name == "later.zip"));
            Assert.IsTrue(toolbar.IsKeyboardFocused, "自動更新でペインへフォーカスを奪わない");

            CompleteWithDispatcher(tab.NavigateAsync(next));
            Wait(() => watches.Items.ContainsKey(next));
            pane.CloseTab(pane.Tabs[1]); Pump();
            Assert.IsTrue(watches.Items[second].Disposed);
            Assert.IsFalse(watches.Items[first].Disposed, "右側が同じフォルダーを使う間は監視を維持");
            watches.Items[next].Failed(new IOException("simulated watch failure"));
            Wait(() => tab.WatchWarning.Length > 0);
            Assert.IsTrue(watches.Items[next].Disposed);
            StringAssert.Contains(tab.Status, "自動更新停止");
            var failedHandle = watches.Items[next];
            CompleteWithDispatcher(tab.RefreshAsync());
            Wait(() => !ReferenceEquals(failedHandle, watches.Items[next]) && tab.WatchWarning.Length == 0);
            monitor.Dispose();
            Assert.IsTrue(watches.Items.Values.All(w => w.Disposed));
        }
        finally { monitor?.Dispose(); window.Close(); Pump(); }
    }

    private sealed class WatchFiles : IFileSystemService
    {
        public Dictionary<string, int> Reads { get; } = [];
        public Dictionary<string, string> Extra { get; } = [];
        public TaskCompletionSource? HoldNext;
        public async Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token)
        {
            Reads[path] = Reads.GetValueOrDefault(path) + 1;
            var held = HoldNext; HoldNext = null;
            if (held is not null) await held.Task.WaitAsync(token);
            var names = new List<string> { "keep-a.txt", "keep-b.txt" };
            names.AddRange(Enumerable.Range(0, 80).Select(i => $"z-item-{i:D3}.txt"));
            if (Extra.TryGetValue(path, out var name)) names.Add(name);
            return new FolderSnapshot(path, names.Select(n => new FileEntry(Path.Combine(path, n), n, false, 1, null)).ToArray());
        }
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class WatchSource : IFolderWatchService
    {
        public ConcurrentDictionary<string, WatchHandle> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IDisposable Watch(string path, Action changed, Action<Exception> failed)
        {
            var handle = new WatchHandle(changed, failed);
            Items.AddOrUpdate(path, handle, (_, old) => { Assert.IsTrue(old.Disposed); return handle; });
            return handle;
        }
    }
    private sealed class WatchHandle(Action changed, Action<Exception> failed) : IDisposable
    {
        public Action Changed { get; } = changed;
        public Action<Exception> Failed { get; } = failed;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}

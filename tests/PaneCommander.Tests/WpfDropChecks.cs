using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyDropTargets()
    {
        var operations = new DropRecordingOperations();
        using var main = new MainViewModel(new DropFiles(), new FakeLogger(), fileOperations: operations);
        var window = new MainWindow { DataContext = main, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            CompleteWithDispatcher(main.Workspace.Left.SelectedTab.NavigateAsync(@"C:\drop\left"));
            CompleteWithDispatcher(main.Workspace.Right.SelectedTab.NavigateAsync(@"C:\drop\right")); Pump();
            var panes = Descendants<PaneCommander.App.Views.PaneView>(window).ToArray();
            var left = panes.Single(p => ((PaneViewModel)p.DataContext).State.Side == PaneSide.Left);
            var right = panes.Single(p => p != left);
            var sourceList = left.CurrentFileList!;
            sourceList.SelectedItems.Clear(); sourceList.SelectedItems.Add(sourceList.Items[0]); sourceList.SelectedItems.Add(sourceList.Items[1]);
            var selectedSource = (FileEntry)sourceList.Items[0];
            var captured = PaneCommander.App.Views.FileDragBehavior.CaptureSources(sourceList, selectedSource);
            Assert.AreEqual(2, captured.Length, "選択済み行の押下では複数選択を保持");
            var unselectedSource = (FileEntry)sourceList.Items[2];
            CollectionAssert.AreEqual(new[] { unselectedSource.FullPath }, PaneCommander.App.Views.FileDragBehavior.CaptureSources(sourceList, unselectedSource));
            sourceList.SelectedItems.Clear(); sourceList.SelectedItem = sourceList.Items[4];
            Assert.AreEqual(selectedSource.FullPath, captured[0], "カーソル移動後の行選択は保持したドラッグ元を変えない");
            foreach (var receiver in new[] { right, left })
            foreach (var external in new[] { false, true })
            foreach (var copy in new[] { false, true })
            foreach (var rowName in new[] { "", "normal.txt", "probe.exe", "probe.cmd", "probe.lnk", "subdirectory" })
            {
                var receiverState = ((PaneViewModel)receiver.DataContext).State;
                var from = receiver == right ? @"C:\drop\left\payload.txt" : @"C:\drop\right\payload.txt";
                var list = receiver.CurrentFileList!; list.UpdateLayout();
                var entry = list.Items.OfType<FileEntry>().FirstOrDefault(e => e.Name == rowName);
                var target = entry is null ? (UIElement)list : (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(entry);
                var data = new DataObject();
                if (external) data.SetFileDropList(new System.Collections.Specialized.StringCollection { from });
                else data.SetData(PaneCommander.App.Views.FileDragBehavior.InternalFilesFormat, new[] { from });
                var keys = copy ? DragDropKeyStates.ControlKey : DragDropKeyStates.ShiftKey;
                var enter = DropEvent(data, keys, target, DragDrop.PreviewDragEnterEvent); target.RaiseEvent(enter);
                Assert.AreEqual(copy ? DragDropEffects.Copy : DragDropEffects.Move, enter.Effects);
                var drop = DropEvent(data, keys, target, DragDrop.PreviewDropEvent); target.RaiseEvent(drop); Pump();
                Assert.IsTrue(drop.Handled);
                Assert.AreEqual(receiverState.SelectedTab.CurrentPath, operations.Last!.Destination, $"{rowName}, external={external}, copy={copy}");
                Assert.AreEqual(copy ? FileOperationKind.Copy : FileOperationKind.Move, operations.Last.Kind);
                CollectionAssert.AreEqual(new[] { from }, operations.Last.Sources);
                Assert.AreEqual(copy ? DragDropEffects.Copy : DragDropEffects.Move, drop.Effects);
            }
            var before = operations.Calls;
            var sameData = new DataObject(); sameData.SetData(PaneCommander.App.Views.FileDragBehavior.InternalFilesFormat, new[] { @"C:\drop\right\normal.txt" });
            foreach (var keys in new[] { DragDropKeyStates.None, DragDropKeyStates.ControlKey, DragDropKeyStates.ShiftKey })
            {
                var args = DropEvent(sameData, keys, right.CurrentFileList!, DragDrop.PreviewDropEvent); right.CurrentFileList!.RaiseEvent(args);
                Assert.AreEqual(DragDropEffects.None, args.Effects); Assert.AreEqual(before, operations.Calls);
            }
            var oldList = right.CurrentFileList!;
            var freshData = new DataObject(); freshData.SetFileDropList(new System.Collections.Specialized.StringCollection { @"C:\drop\left\payload.txt" });
            var over = DropEvent(freshData, DragDropKeyStates.ControlKey, oldList, DragDrop.PreviewDragOverEvent); oldList.RaiseEvent(over);
            main.Workspace.Right.AddTab(); Pump(); CompleteWithDispatcher(main.Workspace.Right.SelectedTab.NavigateAsync(@"C:\drop\new-tab")); Pump();
            var switchedList = right.CurrentFileList!;
            var switched = DropEvent(freshData, DragDropKeyStates.ControlKey, switchedList, DragDrop.PreviewDropEvent); switchedList.RaiseEvent(switched); Pump();
            Assert.AreEqual(@"C:\drop\new-tab", operations.Last!.Destination, "DragOver以前のタブを宛先として保持しない");
            operations.Abort = true;
            var aborted = DropEvent(freshData, DragDropKeyStates.ControlKey, right.CurrentFileList!, DragDrop.PreviewDropEvent); right.CurrentFileList!.RaiseEvent(aborted); Pump();
            Assert.AreEqual(DragDropEffects.None, aborted.Effects, "中止結果で成功を返さない");
            Assert.AreEqual(0, PaneCommander.App.Views.FileDragBehavior.ReadEffect(freshData, "Logical Performed DropEffect"));
        }
        finally { window.Close(); }
    }
    private static DragEventArgs DropEvent(IDataObject data, DragDropKeyStates keys, UIElement target, RoutedEvent routedEvent)
    {
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [data, keys, DragDropEffects.Copy | DragDropEffects.Move, target, new Point(4, 4)], null)!;
        args.RoutedEvent = routedEvent; return args;
    }
    private sealed class DropRecordingOperations : IFileOperationService
    {
        public FileOperationRequest? Last; public int Calls; public bool Abort;
        public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, IntPtr owner, CancellationToken token = default)
        { Last = request; Calls++; return Task.FromResult(new FileOperationResult(Abort)); }
    }
    private sealed class DropFiles : IFileSystemService
    {
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,
            new[] { "normal.txt", "probe.exe", "probe.cmd", "probe.lnk", "subdirectory" }.Select(name => new FileEntry(Path.Combine(path, name), name, name == "subdirectory", 1, DateTime.Now)).ToArray()));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => throw new AssertFailedException("DnD must not open any row or launch an application");
    }
}

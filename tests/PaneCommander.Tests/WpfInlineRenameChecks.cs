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
    private static void VerifyInlineRename()
    {
        var files = new InlineRenameFiles();
        using var model = new MainViewModel(files, new FakeLogger(), fileOperations: files);
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); window.Activate(); Pump(); model.Workspace.Left.SelectedTab.ShowDotFiles = true; Pump();
            var pane = (PaneView)window.FindName("LeftPane"); var folder = Descendants<FolderView>(pane).Single(); var list = folder.FileList;
            HighlightedFileName Label(string name) => Descendants<HighlightedFileName>(list).Single(l => l.FileName == name);
            void Select(string name) { list.SelectedItems.Clear(); list.SelectedItem = list.Items.Cast<FileEntry>().Single(e => e.Name == name); pane.FocusList(); Pump(); }
            TextBox Begin(string name)
            {
                Select(name); Assert.IsTrue(folder.PrepareRename(Label(name), new Point(), 1, ModifierKeys.None));
                folder.ReleaseRename(new Point()); Assert.IsNull(folder.RenameEditor, "ダブルクリックを判定するまで待機");
                Assert.IsTrue(folder.BeginPendingRename()); Pump(); return folder.RenameEditor!;
            }
            Select("folder.with.dot");
            Assert.IsFalse(folder.PrepareRename(Label("report.v1.txt"), new Point(), 1, ModifierKeys.None), "最初のクリックは選択のみ");
            Assert.IsFalse(folder.PrepareRename(Label("folder.with.dot"), new Point(), 1, ModifierKeys.Control));
            Assert.IsFalse(folder.PrepareRename(Label("folder.with.dot"), new Point(), 1, ModifierKeys.Shift));
            Assert.IsFalse(folder.PrepareRename(Label("folder.with.dot"), new Point(), 2, ModifierKeys.None), "ダブルクリックは既存のOpenへ");
            Assert.IsFalse(folder.BeginPendingRename());
            Assert.IsTrue(folder.PrepareRename(Label("folder.with.dot"), new Point(), 1, ModifierKeys.None));
            folder.CancelRenameDrag(new Point(100, 100)); folder.ReleaseRename(new Point(100, 100)); Assert.IsFalse(folder.BeginPendingRename(), "DnDでは編集しない");
            list.SelectedItems.Add(list.Items.Cast<FileEntry>().Single(e => e.Name == "report.v1.txt"));
            Assert.IsFalse(folder.PrepareRename(Label("folder.with.dot"), new Point(), 1, ModifierKeys.None), "複数選択を崩さない");

            var editor = Begin("report.v1.txt"); Assert.AreEqual("report.v1", editor.SelectedText); Assert.IsTrue(editor.IsKeyboardFocused);
            Assert.IsFalse(FileMarqueeSelection.Get(list).Begin(new Point(10, 30), ModifierKeys.None, editor), "入力欄では文字範囲を選択");
            editor.SelectedText = "変更"; Capture(window, "inline-rename.png");
            RaiseKey(editor, PresentationSource.FromVisual(window)!, Key.Enter); Pump();
            Assert.IsNull(folder.RenameEditor); Assert.AreEqual("変更.txt", files.Last!.NewName); Assert.AreEqual(FileOperationKind.Move, files.Last.Kind);
            Assert.AreEqual(model.Workspace.Left.SelectedTab.CurrentPath, files.Last.Destination);
            Assert.AreEqual("変更.txt", ((FileEntry)folder.FileList.SelectedItem).Name); Assert.IsTrue(folder.FileList.IsKeyboardFocusWithin);
            editor = Begin("folder.with.dot"); Assert.AreEqual("folder.with.dot", editor.SelectedText);
            editor.Text = "cancelled"; RaiseKey(editor, PresentationSource.FromVisual(window)!, Key.Escape); Pump(); Assert.AreEqual(1, files.Moves); Assert.IsNull(folder.RenameEditor);
            editor = Begin(".gitignore"); Assert.AreEqual(".gitignore", editor.SelectedText); RaiseKey(editor, PresentationSource.FromVisual(window)!, Key.Escape); Pump();
            editor = Begin("変更.txt"); editor.Text = "../bad"; CompleteWithDispatcher(folder.CommitRenameAsync(true));
            Assert.AreSame(editor, folder.RenameEditor); Assert.IsNotNull(editor.ToolTip); Assert.AreEqual(1, files.Moves);
            editor.Text = "変更.txt"; CompleteWithDispatcher(folder.CommitRenameAsync(true)); Assert.IsNull(folder.RenameEditor); Assert.AreEqual(1, files.Moves, "同じ名前は操作しない");
            editor = Begin("変更.txt"); editor.Text = "focus-loss.txt"; ((Button)pane.FindName("DisplayOptionsButton")).Focus(); Pump();
            Assert.AreEqual(2, files.Moves); Assert.AreEqual("focus-loss.txt", files.Last.NewName);
            Select("focus-loss.txt"); Assert.IsTrue(folder.PrepareRename(Label("focus-loss.txt"), new Point(), 1, ModifierKeys.None)); folder.ReleaseRename(new Point());
            CompleteWithDispatcher(model.Workspace.Left.SelectedTab.NavigateAsync(@"C:\inline-test\other")); Assert.IsFalse(folder.BeginPendingRename(), "移動・一覧更新で待機を解除");
            editor = Begin("focus-loss.txt"); model.Left.AddTab.Execute(null); Pump(); Assert.IsNull(folder.RenameEditor, "タブ切替で編集を取り消す"); Assert.AreEqual(2, files.Moves);
        }
        finally { window.Close(); Pump(); }
    }
    private sealed class InlineRenameFiles : IFileSystemService, IFileOperationService
    {
        private readonly List<(string Name, bool Directory)> entries = [("report.v1.txt", false), ("folder.with.dot", true), (".gitignore", false)];
        internal FileOperationRequest? Last; internal int Moves;
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path, entries.Select(e => new FileEntry(System.IO.Path.Combine(path, e.Name), e.Name, e.Directory, 1, null)).ToArray()));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
        public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, IntPtr owner, CancellationToken cancellationToken = default)
        {
            Last = request; Moves++; var index = entries.FindIndex(e => e.Name == System.IO.Path.GetFileName(request.Sources.Single()));
            entries[index] = (request.NewName!, entries[index].Directory); return Task.FromResult(new FileOperationResult(false));
        }
    }
}

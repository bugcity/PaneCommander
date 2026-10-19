using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyDeleteFocus()
    {
        var files = new DeleteFocusFiles();
        using var model = new MainViewModel(files, new FakeLogger(), fileOperations: files);
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            var left = (PaneView)window.FindName("LeftPane"); var list = left.CurrentFileList!;
            var source = PresentationSource.FromVisual(list)!;
            Descendants<GridViewColumnHeader>(left).Single(h => Equals(h.Content, "名前")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Pump();
            void Select(int index) { list.SelectedIndex = index; left.FocusList(); Pump(); }
            void Delete()
            {
                RaiseKey(list, source, Key.Delete); Pump();
                Assert.AreEqual(FileOperationKind.Delete, files.Last!.Kind);
                Assert.IsTrue(files.Last.Recycle);
                Assert.IsFalse(model.IsFileOperationRunning);
            }
            void Focused(string name)
            {
                Assert.AreEqual(name, ((FileEntry?)list.SelectedItem)?.Name);
                Assert.AreEqual(name, model.Workspace.Left.SelectedTab.SelectedEntry?.Name);
                Assert.IsTrue(Keyboard.FocusedElement is ListViewItem { DataContext: FileEntry entry } && entry.Name == name,
                    "削除後の選択行へ実際のキーボードフォーカスを戻す");
            }
            Select(2); Assert.AreEqual("c.txt", ((FileEntry)list.SelectedItem).Name);
            Delete(); Focused("b.txt"); // Display order is descending, independent of enumeration order.
            Select(list.Items.Count - 1); Delete(); Focused("b.txt"); // Last row falls back to previous row.

            list.SelectAll(); left.FocusList(); Pump(); files.Cancel = true;
            var selected = list.SelectedItems.Count; Delete(); Assert.AreEqual(selected, list.SelectedItems.Count, "中止時は複数選択を維持");
            Assert.IsTrue(list.IsKeyboardFocusWithin);
            files.Cancel = false; Delete(); Assert.AreEqual(0, list.Items.Count);
            Assert.IsNull(list.SelectedItem); Assert.AreSame(list, Keyboard.FocusedElement, "空になったら一覧へフォーカス");
        }
        finally { window.Close(); Pump(); }
    }

    private sealed class DeleteFocusFiles : IFileSystemService, IFileOperationService
    {
        private readonly HashSet<string> remaining = ["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"];
        public bool Cancel;
        public FileOperationRequest? Last;
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,
            remaining.Select(name => new FileEntry(Path.Combine(path, name), name, false, 1, null)).ToArray()));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
        public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, IntPtr owner, CancellationToken token = default)
        {
            Last = request;
            if (!Cancel) foreach (var path in request.Sources) remaining.Remove(Path.GetFileName(path));
            return Task.FromResult(new FileOperationResult(Cancel));
        }
    }
}

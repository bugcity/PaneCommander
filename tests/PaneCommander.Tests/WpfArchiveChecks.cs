using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyArchives()
    {
        var archives = new UiArchives();
        using var model = new MainViewModel(new ArchiveUiFiles(), new FakeLogger(), fileOperations: new ReviewUiOperations(), archives: archives);
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump(); var pane = (PaneView)window.FindName("LeftPane"); var list = pane.CurrentFileList!; var tab = model.Workspace.Left.SelectedTab;
            list.SelectedItem = list.Items.Cast<FileEntry>().Single(e => e.Name == "sample.zip"); Pump();
            Assert.IsTrue(model.CanExtractArchives); Assert.IsTrue(model.CanCompressArchives);
            var functions = Descendants<Button>(window).Where(b => b.DataContext is FunctionAction).ToArray();
            Assert.AreEqual("F2 展開", functions[1].Content); Assert.IsTrue(functions[1].IsEnabled);
            bool? requested = null; model.RequestArchive = compress => { requested = compress; return Task.CompletedTask; };
            RaiseKey(list, PresentationSource.FromVisual(window)!, Key.F2); Assert.AreEqual(false, requested);
            model.SetFunctionModifiers(ModifierKeys.Shift); functions[1].Command.Execute(null); Assert.AreEqual(true, requested); Assert.AreEqual("⇧F2 圧縮", functions[1].Content);
            model.SetFunctionModifiers(ModifierKeys.None);

            var dialog = new ArchiveCompressionDialog("日本語") { Owner = window };
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                Assert.AreEqual("日本語", Descendants<TextBox>(dialog).Single(t => t.Text == "日本語").SelectedText);
                var combos = Descendants<ComboBox>(dialog).ToArray(); Assert.AreEqual(ArchiveLevel.Normal, dialog.Level);
                combos[0].SelectedIndex = 1; Assert.IsFalse(((ComboBoxItem)combos[1].Items[0]).IsEnabled, "7z無圧縮は選べない");
                combos[0].SelectedIndex = 2; Assert.IsFalse(combos[1].IsEnabled, "TARのレベルは対象外");
                combos[0].SelectedIndex = 0; Capture(dialog, "archive-compression.png");
                dialog.DialogResult = false;
            }); dialog.ShowDialog(); foreach (var encoding in new[] { ZipNameEncoding.Utf8, ZipNameEncoding.Cp932 })
            {
                var choice = new ArchiveEncodingDialog(new("sample.zip", Enumerable.Range(0, 10000).Select(n => new ArchiveNamePair($"日本語/{n}.txt", $"比較/{n}.txt")).ToArray())) { Owner = window };
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var names = Descendants<ListView>(choice).Single(); Assert.IsTrue(VirtualizingPanel.GetIsVirtualizing(names));
                    Assert.IsTrue(Descendants<ListViewItem>(names).Count() < 100, "大量の名前を一括描画しない");
                    if (encoding == ZipNameEncoding.Utf8) Capture(choice, "archive-encoding.png");
                    Descendants<Button>(choice).Single(b => Equals(b.Content, encoding == ZipNameEncoding.Utf8 ? "UTF-8で展開" : "CP932で展開")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }); choice.ShowDialog(); Assert.AreEqual(encoding, choice.SelectedEncoding);
            }
            var conflict = new ArchiveConflictDialog(new("sample.zip", @"C:\archive-test\sample\a.txt", false, false)) { Owner = window };
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                Descendants<CheckBox>(conflict).Single().IsChecked = true;
                Descendants<Button>(conflict).Single(b => Equals(b.Content, "名前変更（連番）")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }); conflict.ShowDialog(); Assert.AreEqual(new ArchiveConflictChoice(ArchiveConflictAction.Rename, true), conflict.Choice);

            var pending = new TaskCompletionSource<ArchiveBatchResult>();
            var running = model.ExecuteArchiveAsync(tab, (_, progress, token) =>
            {
                progress.Report(new("展開", "sample/a.txt", 50, 100, 1, 2));
                token.Register(() => pending.TrySetResult(new(0, 0, 0, true, [], [], []))); return pending.Task;
            }); Pump(); Assert.IsFalse(running.IsFaulted, running.Exception?.ToString()); Assert.IsTrue(model.IsFileOperationRunning); Assert.IsTrue(model.IsArchiveOperationRunning);
            foreach (var n in new[] { 2, 5, 6, 7, 8 }) Assert.IsFalse(model.Functions[n - 1].Command.CanExecute(null));
            Assert.IsFalse(model.CanCompressArchives); Assert.AreEqual(50d, Descendants<ProgressBar>(pane).Single().Value);
            Capture(window, "archive-progress.png");
            model.Left.AddTab.Execute(null); Pump(); Assert.AreNotSame(tab, model.Workspace.Left.SelectedTab);
            CompleteWithDispatcher(model.Workspace.Left.SelectedTab.NavigateAsync(@"C:\archive-test\other"));
            model.Workspace.Left.SelectedTab = tab; Pump();
            Descendants<Button>(pane).Single(b => Equals(b.Content, "キャンセル") && b.IsVisible).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CompleteWithDispatcher(running); Assert.IsTrue(running.Result.Cancelled); Assert.IsNull(tab.ArchiveProgress); Assert.IsFalse(model.IsFileOperationRunning);
            var paths = tab.Entries.Take(2).Select(e => e.FullPath).ToArray(); tab.RequestSelection(paths); Pump();
            Assert.AreEqual(2, pane.CurrentFileList!.SelectedItems.Count);
            model.Left.AddTab.Execute(null); Pump(); tab.ReleaseSnapshot(); tab.RequestSelection(paths);
            model.Workspace.Left.SelectedTab = tab; CompleteWithDispatcher(tab.EnsureLoadedAsync()); Pump();
            Assert.AreEqual(2, pane.CurrentFileList!.SelectedItems.Count, "背景タブの再読込後に生成物を複数選択");
        }
        finally { window.Close(); Pump(); }
    }
    private sealed class ArchiveUiFiles : IFileSystemService
    {
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,
            [new(Path.Combine(path, "sample.zip"), "sample.zip", false, 100, DateTime.Now), new(Path.Combine(path, "data.txt"), "data.txt", false, 10, DateTime.Now), new(Path.Combine(path, "folder"), "folder", true, null, DateTime.Now)]));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class UiArchives : IArchiveService
    {
        public Task<ArchiveBatchResult> CompressAsync(ArchiveCompressionRequest request, IArchiveInteraction interaction, IProgress<ArchiveProgress> progress, CancellationToken token) => throw new NotSupportedException();
        public Task<ArchiveBatchResult> ExtractAsync(string[] paths, string directory, long reserve, IArchiveInteraction interaction, IProgress<ArchiveProgress> progress, CancellationToken token) => throw new NotSupportedException();
    }
}

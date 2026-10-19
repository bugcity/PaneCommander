using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;

namespace PaneCommander.Tests;

[TestClass]
public sealed class FunctionSelectionTests
{
    [DataTestMethod]
    [DataRow(false, false, false, false, true, true)]
    [DataRow(true, false, false, false, false, true)]
    [DataRow(false, true, false, false, true, false)]
    [DataRow(false, false, true, false, true, false)]
    [DataRow(false, false, false, true, false, true)]
    public async Task KnownUnsupportedSelectionsDisablePreviewOrRecycle(bool directory, bool link, bool unc, bool large, bool preview, bool recycle)
    {
        var root = unc ? @"\\server\share" : @"C:\fixture";
        var entry = new FileEntry(Path.Combine(root, "item"), "item", directory, large ? 2 * 1024 * 1024 : 10, null, link);
        using var model = new MainViewModel(new SelectionFiles(entry), new FakeLogger(), fileOperations: new SelectionOperations());
        await model.Workspace.Left.SelectedTab.NavigateAsync(root);
        model.Workspace.Left.SelectedTab.SelectedEntry = entry;
        model.GetSelectedPaths = () => [entry.FullPath];
        model.RequestPreview = _ => Task.CompletedTask;
        model.RequestFileOperation = _ => Task.CompletedTask;
        Assert.AreEqual(preview, model.Functions[2].Command.CanExecute(null));
        Assert.AreEqual(recycle, model.Functions[7].Command.CanExecute(null));
        Assert.IsTrue(model.Functions[4].Command.CanExecute(null), "コピーはリンク・UNCでも従来の操作経路を使用可能");
    }
    private sealed class SelectionFiles(FileEntry entry) : IFileSystemService
    {
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path, [entry]));
        public Task OpenFileAsync(FileEntry value, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class SelectionOperations : IFileOperationService
    {
        public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, IntPtr owner, CancellationToken token = default) => Task.FromResult(new FileOperationResult(false));
    }
}

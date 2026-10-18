using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

[TestClass]
public sealed class FileIconTests
{
    [TestMethod]
    public async Task ShellIconsUseFileTypesWithoutAccessingTheEntryPath()
    {
        var file = new FileEntry(@"\\unavailable.invalid\share\sample.txt", "sample.txt", false, 0, null);
        var first = FileIconProvider.GetAsync(file);
        var sameType = FileIconProvider.GetAsync(file with { Name = "other.TXT", FullPath = @"C:\missing\other.TXT" });
        Assert.AreSame(first, sameType, "同じ種類はアイコンと要求を共有する");
        var document = await first;
        var folder = await FileIconProvider.GetAsync(file with { IsDirectory = true });
        Assert.IsInstanceOfType<BitmapSource>(document, "Shellから実アイコンを取得する");
        Assert.IsInstanceOfType<BitmapSource>(folder);
        Assert.IsTrue(document.IsFrozen);
        Assert.IsTrue(folder.IsFrozen);
        Assert.AreNotSame(document, folder);
        Assert.AreSame(document, await FileIconProvider.GetAsync(file with { IsReparsePoint = true }), "リンク先を参照してアイコンを取得しない");
    }
}

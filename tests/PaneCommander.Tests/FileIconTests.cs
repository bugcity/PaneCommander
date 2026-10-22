using System.Windows.Media.Imaging;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

[TestClass]
public sealed class FileIconTests
{
    [TestMethod]
    public async Task ExecutablesUseEmbeddedIconsAndDoNotShareAnExtensionCache()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "PaneCommander.App.exe");
        Assert.IsTrue(File.Exists(path));
        var info = new FileInfo(path); var entry = new FileEntry(path, info.Name, false, info.Length, info.LastWriteTime);
        var first = FileIconProvider.GetAsync(entry);
        Assert.AreSame(first, FileIconProvider.GetAsync(entry), "同じファイルと更新情報は共有");
        var actual = (BitmapSource)await first;
        var generic = (BitmapSource)await FileIconProvider.GetAsync(entry with { IsReparsePoint = true });
        Assert.IsTrue(actual.IsFrozen); Assert.AreEqual(generic.PixelWidth, actual.PixelWidth, "DPIに応じた小アイコンを取得");
        CollectionAssert.AreNotEqual(Pixels(generic), Pixels(actual), "PaneCommanderの埋め込みアイコンを種類共通アイコンと区別");
        var missing = FileIconProvider.GetAsync(entry with { FullPath = Path.Combine(AppContext.BaseDirectory, "missing-icon-test.exe") });
        Assert.AreNotSame(first, missing, "別のEXEに同じアイコンを流用しない");
        CollectionAssert.AreEqual(Pixels(generic), Pixels((BitmapSource)await missing), "取得不能時も種類アイコンを表示");
        var changed = FileIconProvider.GetAsync(entry with { LastWriteTime = info.LastWriteTime.AddSeconds(1) });
        Assert.AreNotSame(first, changed, "一覧更新で新しい更新日時のアイコンを再取得");
        CollectionAssert.AreEqual(Pixels(actual), Pixels((BitmapSource)await changed));
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4]; converted.CopyPixels(bytes, converted.PixelWidth * 4, 0); return bytes;
    }

    [TestMethod]
    public async Task RenamedJapaneseExecutableAndIcoDisplayTheSameEmbeddedProductIcon()
    {
        var parent = Path.Combine(Path.GetTempPath(), "PaneCommanderIconTests"); var root = Directory.CreateDirectory(Path.Combine(parent, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var exe = Path.Combine(root, "日本語 PaneCommander.exe"); var ico = Path.Combine(root, "日本語.ico");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "PaneCommander.App.exe"), exe);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "IconFixtures/PaneCommander.ico"), ico);
            static FileEntry Entry(string path) { var info = new FileInfo(path); return new(path, info.Name, false, info.Length, info.LastWriteTime); }
            var executableIcon = (BitmapSource)await FileIconProvider.GetAsync(Entry(exe));
            var icoIcon = (BitmapSource)await FileIconProvider.GetAsync(Entry(ico));
            CollectionAssert.AreEqual(Pixels(icoIcon), Pixels(executableIcon), "名前変更したEXEも元のICOと同じ製品アイコンを表示");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(executableIcon));
            using var image = File.Create(Path.Combine(AppContext.BaseDirectory, "executable-icon.png")); encoder.Save(image);
        }
        finally
        {
            Assert.AreEqual(parent, Path.GetDirectoryName(root)); Assert.IsTrue(Guid.TryParse(Path.GetFileName(root), out _)); Directory.Delete(root, true);
        }
    }

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

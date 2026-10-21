using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class ArchiveServiceTests
{
    [TestMethod]
    [DataRow(ArchiveFormat.Zip)] [DataRow(ArchiveFormat.SevenZip)] [DataRow(ArchiveFormat.Tar)] [DataRow(ArchiveFormat.TarGZip)]
    public async Task FormatsAndLevelsRoundTripJapaneseHierarchyEmptyFoldersAndAttributes(ArchiveFormat format)
    {
        using var fixture = new ArchiveFixture();
        var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "日本語")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "empty"));
        var file = Path.Combine(folder, "隠し.txt"); await File.WriteAllTextAsync(file, "日本語データ");
        File.SetAttributes(file, FileAttributes.Hidden | FileAttributes.System);
        var modified = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc); File.SetLastWriteTimeUtc(file, modified);
        Directory.SetLastWriteTimeUtc(folder, modified);
        foreach (var level in Enum.GetValues<ArchiveLevel>().Where(l => ArchiveFormats.SupportsLevel(format, l)))
        {
            var service = new ArchiveService(); var interaction = new ArchiveChoices(); var progress = new ArchiveTestProgress();
            var result = await service.CompressAsync(new([folder], fixture.Root, "日本語-" + level, format, level, 0), interaction, progress, default);
            Assert.AreEqual(1, result.Succeeded, string.Join("\n", result.Errors.Concat(result.Warnings)));
            Assert.AreEqual(0, result.Failed); Assert.IsTrue(progress.Values.Count > 0);
            var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "out-" + level)).FullName;
            result = await service.ExtractAsync([result.Outputs.Single()], target, 0, interaction, progress, default);
            Assert.AreEqual(1, result.Succeeded, string.Join("\n", result.Errors.Concat(result.Warnings)));
            var extracted = Directory.GetFiles(target, "隠し.txt", SearchOption.AllDirectories).Single();
            Assert.AreEqual("日本語データ", await File.ReadAllTextAsync(extracted));
            Assert.AreEqual(modified, File.GetLastWriteTimeUtc(extracted));
            Assert.IsTrue(Directory.Exists(Path.Combine(Path.GetDirectoryName(extracted)!, "empty")));
            if (format != ArchiveFormat.SevenZip) Assert.IsTrue(File.GetAttributes(extracted).HasFlag(FileAttributes.Hidden | FileAttributes.System));
        }
        Assert.AreEqual(0, Directory.GetFileSystemEntries(fixture.Root, ".panecommander-*").Length);
    }

    [TestMethod]
    [DataRow("sample/a.txt", true)] [DataRow("other/a.txt", false)] [DataRow("a.txt", false)]
    public async Task ExtractionRootRule(string entryName, bool direct)
    {
        using var fixture = new ArchiveFixture(); var zip = Path.Combine(fixture.Root, "sample.zip");
        MakeZip(zip, [(entryName, "data")]); var output = Directory.CreateDirectory(Path.Combine(fixture.Root, "out")).FullName;
        var result = await new ArchiveService().ExtractAsync([zip], output, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(1, result.Succeeded, string.Join("\n", result.Errors));
        Assert.IsTrue(File.Exists(Path.Combine(output, direct ? entryName : "sample/" + entryName)));
        Assert.IsFalse(Directory.Exists(Path.Combine(output, "sample/sample")));
    }

    [TestMethod]
    public async Task ConflictsAreResolvedBeforeWritingAndCanOverwriteSkipOrRenameAcrossArchives()
    {
        using var fixture = new ArchiveFixture(); var first = Path.Combine(fixture.Root, "one.zip"); var second = Path.Combine(fixture.Root, "two.zip");
        MakeZip(first, [("a.txt", "first")]); MakeZip(second, [("a.txt", "second")]);
        var output = Directory.CreateDirectory(Path.Combine(fixture.Root, "out")).FullName;
        Directory.CreateDirectory(Path.Combine(output, "one")); var original = Path.Combine(output, "one/a.txt"); await File.WriteAllTextAsync(original, "original");
        var choices = new ArchiveChoices { Conflict = conflict => { Assert.AreEqual("original", File.ReadAllText(original)); return new(ArchiveConflictAction.Overwrite, true); } };
        var result = await new ArchiveService().ExtractAsync([first, second], output, 0, choices, new ArchiveTestProgress(), default);
        Assert.AreEqual(2, result.Succeeded, string.Join("\n", result.Errors)); Assert.AreEqual("first", File.ReadAllText(original));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(output, ".panecommander-*").Length);
        choices.Conflict = _ => new(ArchiveConflictAction.Rename, true);
        result = await new ArchiveService().ExtractAsync([first], output, 0, choices, new ArchiveTestProgress(), default);
        Assert.AreEqual(1, result.Succeeded, string.Join("\n", result.Errors)); Assert.AreEqual("first", File.ReadAllText(Path.Combine(output, "one/a (2).txt")));
        choices.Conflict = _ => new(ArchiveConflictAction.Skip, true);
        result = await new ArchiveService().ExtractAsync([first], output, 0, choices, new ArchiveTestProgress(), default);
        Assert.AreEqual(1, result.Skipped);
    }

    [TestMethod]
    [DataRow("../escape.txt")] [DataRow("/absolute.txt")] [DataRow("C:/absolute.txt")] [DataRow("folder/../../escape.txt")] [DataRow("file:stream")] [DataRow("NUL.txt")]
    public async Task UnsafePathsFailEntireArchiveBeforeOutput(string entry)
    {
        using var fixture = new ArchiveFixture(); var zip = Path.Combine(fixture.Root, "unsafe.zip"); MakeZip(zip, [("ok.txt", "safe"), (entry, "unsafe")]);
        var output = Directory.CreateDirectory(Path.Combine(fixture.Root, "out")).FullName;
        var result = await new ArchiveService().ExtractAsync([zip], output, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(1, result.Failed); Assert.AreEqual(0, Directory.GetFileSystemEntries(output).Length);
    }

    [TestMethod]
    public async Task CapacityAndExternalChangesDoNotOverwriteExistingFiles()
    {
        using var fixture = new ArchiveFixture(); var zip = Path.Combine(fixture.Root, "sample.zip"); MakeZip(zip, [("a.txt", "new")]);
        var output = Directory.CreateDirectory(Path.Combine(fixture.Root, "out")).FullName;
        var result = await new ArchiveService { AvailableBytes = _ => 10 }.ExtractAsync([zip], output, 10, new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(1, result.Failed); Assert.AreEqual(0, Directory.GetFileSystemEntries(output).Length);
        Directory.CreateDirectory(Path.Combine(output, "sample")); var original = Path.Combine(output, "sample/a.txt"); File.WriteAllText(original, "original");
        result = await new ArchiveService { BeforeCommit = _ => File.WriteAllText(original, "external change") }.ExtractAsync([zip], output, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(1, result.Failed); Assert.AreEqual("external change", File.ReadAllText(original));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(output, ".panecommander-*").Length);
    }

    internal static void MakeZip(string path, (string Name, string Data)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create, System.Text.Encoding.UTF8);
        foreach (var (name, data) in entries) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(data); }
    }
    internal sealed class ArchiveFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "PaneCommanderArchiveTests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose()
        {
            Assert.AreEqual(Path.Combine(Path.GetTempPath(), "PaneCommanderArchiveTests"), Path.GetDirectoryName(Root)); Assert.IsTrue(Guid.TryParse(Path.GetFileName(Root), out _));
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
    internal sealed class ArchiveChoices : IArchiveInteraction
    {
        internal Func<ArchiveConflict, ArchiveConflictChoice> Conflict = _ => new(ArchiveConflictAction.Overwrite, true);
        internal ZipNameEncoding? Encoding = ZipNameEncoding.Utf8;
        internal int EncodingCalls;
        public Task<ZipNameEncoding?> ChooseEncodingAsync(ArchiveNamePreview preview, CancellationToken token) { EncodingCalls++; return Task.FromResult(Encoding); }
        public Task<ArchiveConflictChoice> ResolveConflictAsync(ArchiveConflict conflict, CancellationToken token) => Task.FromResult(Conflict(conflict));
    }
    internal sealed class ArchiveTestProgress : IProgress<ArchiveProgress> { internal readonly List<ArchiveProgress> Values = []; public void Report(ArchiveProgress value) => Values.Add(value); }
}

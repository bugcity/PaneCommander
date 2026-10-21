using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;
using SharpCompress.Common;
using SharpCompress.Providers;
using static PaneCommander.Tests.ArchiveServiceTests;

namespace PaneCommander.Tests;

[TestClass]
public sealed class ArchiveSafetyTests
{
    [TestMethod]
    public async Task TarXzAndTarZstdExtractAndRejectCorruptChecksums()
    {
        using var f = new ArchiveFixture();
        // Own fixture: Python stdlib tarfile/PAX + lzma FORMAT_XZ, two entries, no external data.
        var xz = Convert.FromBase64String("/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4Cf/ALNdABcL3ASAUDIm7h19G/PyyzPs7Pg38OrWe2QHFu01U3Eqz5rlukaJ3K7e5W2hQSiMGkR6NRvcASZ4IsIpi8Tv5CvxJw1bgZTsFAGyi94heugBS7/IZ5yTC93LDcFpcxPXttiPWYvf/1EyItz+IcOnRR+S6nSrr5Sor4oY8mDxBgVX5qfBnvPg4uRs1eIl/ghEuJKkYgjnj8SLZMGljwnw0iJPdVki/vZGM98fIXcXC/YDf2uJAAAms7nRaYaEeAABzwGAUAAAh8lun7HEZ/sCAAAAAARZWg==");
        var tar = Path.Combine(f.Root, "input.tar");
        using (var writer = new TarWriter(File.Create(tar)))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "日本語/a.txt") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("data")) }; writer.WriteEntry(entry);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "empty"));
        }
        var zstPath = Path.Combine(f.Root, "sample.tar.zst");
        using (var output = File.Create(zstPath))
        using (var wrapper = new SharpCompress.Compressors.ZStandard.CompressionStream(output, 3))
        using (var input = File.OpenRead(tar))
        {
            wrapper.SetParameter(SharpCompress.Compressors.ZStandard.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            input.CopyTo(wrapper);
        }
        foreach (var extension in new[] { ".tar.xz", ".tar.zst" })
        {
            var archive = Path.Combine(f.Root, "sample" + extension);
            if (extension == ".tar.xz") File.WriteAllBytes(archive, xz);
            var output = Directory.CreateDirectory(Path.Combine(f.Root, extension[1..])).FullName;
            var result = await new ArchiveService().ExtractAsync([archive], output, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
            Assert.AreEqual(1, result.Succeeded, string.Join("\n", result.Errors));
            Assert.AreEqual("data", File.ReadAllText(Path.Combine(output, "sample/日本語/a.txt")));
            Assert.IsTrue(Directory.Exists(Path.Combine(output, "sample/empty")));
            var bytes = File.ReadAllBytes(archive); bytes[extension == ".tar.zst" ? bytes.Length - 1 : bytes.Length / 2] ^= 0x40; File.WriteAllBytes(archive, bytes);
            var badOutput = Directory.CreateDirectory(output + "-bad").FullName;
            result = await new ArchiveService().ExtractAsync([archive], badOutput, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
            Assert.AreEqual(1, result.Failed, extension + ": " + string.Join("\n", result.Errors)); Assert.AreEqual(0, Directory.GetFileSystemEntries(badOutput).Length);
        }
    }

    [TestMethod]
    [DataRow(true)] [DataRow(false)]
    public async Task FileDirectoryReplacementProtectsContentsAndRollsBack(bool newDirectory)
    {
        using var f = new ArchiveFixture(); var zip = Path.Combine(f.Root, "sample.zip");
        MakeZip(zip, [(newDirectory ? "sample/item/new.txt" : "sample/item", "new")]);
        var output = Directory.CreateDirectory(Path.Combine(f.Root, "out")).FullName;
        Directory.CreateDirectory(Path.Combine(output, "sample")); var destination = Path.Combine(output, "sample/item");
        if (newDirectory) File.WriteAllText(destination, "old"); else { Directory.CreateDirectory(destination); File.WriteAllText(Path.Combine(destination, "old.txt"), "old"); }
        var result = await new ArchiveService { AfterPublish = _ => throw new IOException("Injected failure") }.ExtractAsync([zip], output, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(1, result.Failed, string.Join("\n", result.Errors));
        Assert.AreEqual("old", File.ReadAllText(newDirectory ? destination : Path.Combine(destination, "old.txt")));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(output, ".panecommander-*").Length, string.Join("\n", result.Errors));
        result = await new ArchiveService().ExtractAsync([zip], output, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(1, result.Succeeded, string.Join("\n", result.Errors));
        Assert.AreEqual("new", File.ReadAllText(newDirectory ? Path.Combine(destination, "new.txt") : destination));
    }

    [TestMethod]
    public async Task CompressionCancellationCapacityAndReadFailuresProtectExistingArchive()
    {
        using var f = new ArchiveFixture(); var input = Path.Combine(f.Root, "input.bin"); File.WriteAllBytes(input, new byte[1024 * 1024]);
        var destination = Path.Combine(f.Root, "result.zip"); File.WriteAllText(destination, "original");
        var request = new ArchiveCompressionRequest([input], f.Root, "result", ArchiveFormat.Zip, ArchiveLevel.None, 0);
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress<ArchiveProgress>(_ => cancel.Cancel());
        var result = await new ArchiveService().CompressAsync(request, new ArchiveChoices(), progress, cancel.Token);
        Assert.IsTrue(result.Cancelled); Assert.AreEqual("original", File.ReadAllText(destination));
        var checks = 0;
        result = await new ArchiveService { AvailableBytes = _ => ++checks > 2 ? 0 : long.MaxValue }.CompressAsync(request, new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(1, result.Failed); Assert.AreEqual("original", File.ReadAllText(destination));
        using (var locked = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await new ArchiveService().CompressAsync(request, new ArchiveChoices(), new ArchiveTestProgress(), default);
            Assert.AreEqual(1, result.Failed); Assert.AreEqual("original", File.ReadAllText(destination));
        }
        Assert.AreEqual(0, Directory.GetFileSystemEntries(f.Root, ".panecommander-*").Length);
    }

    [TestMethod]
    public async Task TarLinksAreRejected()
    {
        using var f = new ArchiveFixture(); var tar = Path.Combine(f.Root, "links.tar");
        using (var writer = new TarWriter(File.Create(tar))) writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "../outside" });
        var output = Directory.CreateDirectory(Path.Combine(f.Root, "out")).FullName;
        var result = await new ArchiveService().ExtractAsync([tar], output, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(1, result.Failed); Assert.AreEqual(0, Directory.GetFileSystemEntries(output).Length);
    }

    [TestMethod]
    public async Task JunctionsAreExcludedFromCompressionAndCannotRedirectExtraction()
    {
        using var f = new ArchiveFixture(); var source = Directory.CreateDirectory(Path.Combine(f.Root, "source")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(f.Root, "target")).FullName; File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        var link = Path.Combine(source, "link");
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(info)!) { Assert.IsTrue(process.WaitForExit(5000)); Assert.AreEqual(0, process.ExitCode, process.StandardError.ReadToEnd()); }
        try
        {
            File.WriteAllText(Path.Combine(source, "ordinary.txt"), "ordinary");
            var result = await new ArchiveService().CompressAsync(new([source], f.Root, "compressed", ArchiveFormat.Zip, ArchiveLevel.Normal, 0), new ArchiveChoices(), new ArchiveTestProgress(), default);
            Assert.AreEqual(1, result.Succeeded, string.Join("\n", result.Errors)); Assert.IsTrue(result.Warnings.Any(w => w.Contains(link)));
            using (var zip = ZipFile.OpenRead(result.Outputs.Single())) Assert.IsFalse(zip.Entries.Any(e => e.FullName.Contains("link")));
            var attack = Path.Combine(f.Root, "attack.zip"); MakeZip(attack, [("source/link/escape.txt", "unsafe")]);
            result = await new ArchiveService().ExtractAsync([attack], f.Root, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
            // The stem wraps this archive, so use a matching root to exercise the actual junction.
            File.Move(attack, Path.Combine(f.Root, "source.zip"));
            result = await new ArchiveService().ExtractAsync([Path.Combine(f.Root, "source.zip")], f.Root, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
            Assert.AreEqual(1, result.Failed); Assert.IsFalse(File.Exists(Path.Combine(target, "escape.txt"))); Assert.AreEqual("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
            result = await new ArchiveService().ExtractAsync([Path.Combine(f.Root, "source.zip")], link, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
            Assert.AreEqual(1, result.Failed); Assert.IsFalse(File.Exists(Path.Combine(target, "escape.txt")));
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public async Task ChangedInputAndBatchCapacityFailBeforePublishing()
    {
        using var f = new ArchiveFixture(); var input = Path.Combine(f.Root, "source.txt"); File.WriteAllText(input, "source");
        var modified = File.GetLastWriteTimeUtc(input);
        var result = await new ArchiveService().CompressAsync(new([input], f.Root, "result", ArchiveFormat.Zip, ArchiveLevel.Normal, 0), new ArchiveChoices(),
            new InlineProgress<ArchiveProgress>(p => { if (p.CompletedFiles == 1) File.SetLastWriteTimeUtc(input, modified.AddHours(1)); }), default);
        Assert.AreEqual(1, result.Failed); Assert.IsFalse(File.Exists(Path.Combine(f.Root, "result.zip"))); Assert.AreEqual(0, Directory.GetFileSystemEntries(f.Root, ".panecommander-*").Length);
        var first = Path.Combine(f.Root, "one.zip"); var second = Path.Combine(f.Root, "two.zip"); MakeZip(first, [("a", "0123456789")]); MakeZip(second, [("b", "0123456789")]);
        var output = Directory.CreateDirectory(Path.Combine(f.Root, "out")).FullName;
        result = await new ArchiveService { AvailableBytes = _ => 15 }.ExtractAsync([first, second], output, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(0, result.Succeeded); Assert.AreEqual(1, result.Failed); Assert.AreEqual(0, Directory.GetFileSystemEntries(output).Length);
    }

    [TestMethod]
    [DataRow(true)] [DataRow(false)]
    public async Task CrossArchiveFileDirectoryReplacementIsResolvedBeforeAnyPublish(bool firstDirectory)
    {
        using var f = new ArchiveFixture(); var first = Path.Combine(f.Root, "same.zip"); var second = Path.Combine(f.Root, "same.tar");
        MakeZip(first, [(firstDirectory ? "same/item/a" : "same/item", "first")]);
        using (var writer = new TarWriter(File.Create(second)))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, firstDirectory ? "same/item" : "same/item/b") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("second")) });
        var output = Directory.CreateDirectory(Path.Combine(f.Root, "out")).FullName; var choices = new ArchiveChoices(); var prompts = 0;
        choices.Conflict = conflict => { prompts++; Assert.IsTrue(conflict.TypeMismatch); Assert.AreEqual(0, Directory.GetFileSystemEntries(output).Length); return new(ArchiveConflictAction.Overwrite, true); };
        var result = await new ArchiveService().ExtractAsync([first, second], output, 0, choices, new ArchiveTestProgress(), default);
        Assert.AreEqual(2, result.Succeeded, string.Join("\n", result.Errors)); Assert.AreEqual(1, prompts);
        Assert.AreEqual("second", File.ReadAllText(Path.Combine(output, firstDirectory ? "same/item" : "same/item/b")));
    }

    [TestMethod]
    public void LogRetentionAndWriteFailureAreNonfatalAndOnlyRegisteredTemporaryPathsAreHidden()
    {
        using var f = new ArchiveFixture(); var logs = Directory.CreateDirectory(Path.Combine(f.Root, "logs")).FullName;
        var old = Path.Combine(logs, DateTime.Now.AddDays(-31).ToString("yyyy-MM-dd") + ".archive.log"); File.WriteAllText(old, "old");
        var recent = Path.Combine(logs, DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd") + ".archive.log"); File.WriteAllText(recent, "recent");
        var unrelated = Path.Combine(logs, "other.log"); File.WriteAllText(unrelated, "keep");
        var logger = new ErrorLogger(logs); logger.Archive("test", ["own-fixture"], "done", null);
        Assert.IsFalse(File.Exists(old)); Assert.IsTrue(File.Exists(recent)); Assert.IsTrue(File.Exists(unrelated));
        var blocked = Path.Combine(f.Root, "not-directory"); File.WriteAllText(blocked, "x"); new ErrorLogger(blocked).Archive("test", [], "ignored", new IOException("failure"));
        var own = Path.Combine(f.Root, ".panecommander-" + Guid.NewGuid().ToString("N") + ".tmp"); File.WriteAllText(own, "user data");
        Assert.IsFalse(ArchiveTemporaryPaths.Contains(own)); ArchiveTemporaryPaths.Add(own); Assert.IsTrue(ArchiveTemporaryPaths.Contains(own)); ArchiveTemporaryPaths.Remove(own);
        Assert.AreEqual("user data", File.ReadAllText(own));
    }
}

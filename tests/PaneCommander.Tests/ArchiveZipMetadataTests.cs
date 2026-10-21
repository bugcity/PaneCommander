using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;
using static PaneCommander.Tests.ArchiveServiceTests;

namespace PaneCommander.Tests;

[TestClass]
public sealed class ArchiveZipMetadataTests
{
    [TestMethod]
    public void WholeArchiveDecisionUnicodeExtraAndStrictDecoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp = Encoding.GetEncoding(932);
        using var fixture = new ArchiveFixture(); var path = Path.Combine(fixture.Root, "names.zip");
        RawZip(path, [("表.txt"u8.ToArray(), 0, null), ("ASCII.txt"u8.ToArray(), 0, null)]);
        using (var stream = File.OpenRead(path)) Assert.IsTrue(ZipNameMetadata.Read(stream).NeedsChoice, "妥当なUTF-8でもCP932を排除しない");
        File.Delete(path);
        RawZip(path, [("表.txt"u8.ToArray(), 0, null), ("あ.txt"u8.ToArray(), 0, null)]);
        using (var stream = File.OpenRead(path)) Assert.AreEqual(ZipNameEncoding.Utf8, ZipNameMetadata.Read(stream).Encoding, "全エントリで判定し、CP932として復号不能な項目を考慮");
        File.Delete(path);
        RawZip(path, [(cp.GetBytes("日本語.txt"), 0, null), ("ascii.txt"u8.ToArray(), 0, "明示Unicode.txt")]);
        using (var stream = File.OpenRead(path))
        {
            var metadata = ZipNameMetadata.Read(stream); Assert.AreEqual(ZipNameEncoding.Cp932, metadata.Encoding);
            CollectionAssert.AreEqual(new[] { "日本語.txt", "明示Unicode.txt" }, metadata.Names(ZipNameEncoding.Cp932));
        }
        File.Delete(path);
        RawZip(path, [(new byte[] { 0x81 }, 0, null)]);
        using (var stream = File.OpenRead(path)) Assert.ThrowsExactly<InvalidDataException>(() => ZipNameMetadata.Read(stream));
    }
    [TestMethod]
    public async Task AmbiguousChoiceAppliesOnlyToUnknownEntriesAndCancellationSkipsOnlyThatArchive()
    {
        using var fixture = new ArchiveFixture(); var first = Path.Combine(fixture.Root, "first.zip"); var second = Path.Combine(fixture.Root, "second.zip");
        RawZip(first, [("表.txt"u8.ToArray(), 0, null), ("raw.txt"u8.ToArray(), 0, "明示.txt")]);
        MakeZip(second, [("ok.txt", "ok")]); var output = Directory.CreateDirectory(Path.Combine(fixture.Root, "out")).FullName;
        foreach (var encoding in new[] { ZipNameEncoding.Utf8, ZipNameEncoding.Cp932 })
        {
            var target = Directory.CreateDirectory(Path.Combine(output, encoding.ToString())).FullName;
            var choices = new ArchiveChoices { Encoding = encoding };
            var result = await new ArchiveService().ExtractAsync([first], target, 0, choices, new ArchiveTestProgress(), default);
            Assert.AreEqual(1, result.Succeeded, string.Join("\n", result.Errors)); Assert.AreEqual(1, choices.EncodingCalls);
            Assert.IsTrue(File.Exists(Path.Combine(target, "first", "明示.txt")));
            Assert.IsTrue(File.Exists(Path.Combine(target, "first", encoding == ZipNameEncoding.Utf8 ? "表.txt" : Encoding.GetEncoding(932).GetString("表.txt"u8.ToArray()))));
        }
        var skip = new ArchiveChoices { Encoding = null };
        var batch = await new ArchiveService().ExtractAsync([first, second], output, 0, skip, new ArchiveTestProgress(), default);
        Assert.AreEqual(1, batch.Skipped); Assert.AreEqual(1, batch.Succeeded); Assert.IsFalse(Directory.Exists(Path.Combine(output, "first")));
    }
    [TestMethod]
    public async Task BadCrcEncryptedAndUnixSymlinksFailWithoutOutputs()
    {
        using var fixture = new ArchiveFixture(); var output = Directory.CreateDirectory(Path.Combine(fixture.Root, "out")).FullName;
        foreach (var mode in new[] { "crc", "encrypted", "link" })
        {
            var zip = Path.Combine(fixture.Root, mode + ".zip");
            RawZip(zip, [("file.txt"u8.ToArray(), mode == "encrypted" ? (ushort)1 : (ushort)0, null)], mode == "crc", mode == "link" ? 0xa1ff0000u : 0);
            var result = await new ArchiveService().ExtractAsync([zip], output, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
            Assert.AreEqual(1, result.Failed, mode); Assert.AreEqual(0, Directory.GetFileSystemEntries(output).Length, mode);
        }
    }
    [TestMethod]
    public void Zip64CentralDirectoryAndUtf8FlagsAreReadWithoutLoadingPayload()
    {
        using var fixture = new ArchiveFixture(); var path = Path.Combine(fixture.Root, "zip64.zip");
        using (var output = ZipFile.Open(path, ZipArchiveMode.Create, Encoding.UTF8)) for (var i = 0; i < 65536; i++) output.CreateEntry($"file{i:00000}.txt");
        using var input = File.OpenRead(path); var metadata = ZipNameMetadata.Read(input);
        Assert.AreEqual(65536, metadata.Entries.Count); Assert.IsTrue(metadata.Entries.All(e => e.UnicodeName is not null), "ASCIIもUTF-8フラグを持つ");
    }
    internal static void RawZip(string path, (byte[] Name, ushort Flags, string? Unicode)[] entries, bool badCrc = false, uint attributes = 0)
    {
        using var output = new BinaryWriter(File.Create(path)); var records = new List<(uint Offset, byte[] Extra)>();
        foreach (var (name, flags, unicode) in entries)
        {
            byte[] extra = [];
            if (unicode is not null)
            {
                using var memory = new MemoryStream(); using var field = new BinaryWriter(memory); var bytes = Encoding.UTF8.GetBytes(unicode);
                field.Write((ushort)0x7075); field.Write((ushort)(5 + bytes.Length)); field.Write((byte)1); field.Write(Crc32.Compute(name)); field.Write(bytes); extra = memory.ToArray();
            }
            records.Add((checked((uint)output.BaseStream.Position), extra));
            output.Write(0x04034b50u); output.Write((ushort)20); output.Write(flags); output.Write((ushort)0); output.Write((ushort)0); output.Write((ushort)0x2821);
            output.Write(badCrc ? 123u : 0u); output.Write(0u); output.Write(0u); output.Write((ushort)name.Length); output.Write((ushort)extra.Length); output.Write(name); output.Write(extra);
        }
        var central = checked((uint)output.BaseStream.Position);
        for (var i = 0; i < entries.Length; i++)
        {
            var (name, flags, _) = entries[i]; var (offset, extra) = records[i];
            output.Write(0x02014b50u); output.Write((ushort)0x0314); output.Write((ushort)20); output.Write(flags); output.Write((ushort)0); output.Write((ushort)0); output.Write((ushort)0x2821);
            output.Write(badCrc ? 123u : 0u); output.Write(0u); output.Write(0u); output.Write((ushort)name.Length); output.Write((ushort)extra.Length); output.Write((ushort)0); output.Write((ushort)0); output.Write((ushort)0); output.Write(attributes); output.Write(offset); output.Write(name); output.Write(extra);
        }
        var size = checked((uint)output.BaseStream.Position) - central;
        output.Write(0x06054b50u); output.Write((ushort)0); output.Write((ushort)0); output.Write((ushort)entries.Length); output.Write((ushort)entries.Length); output.Write(size); output.Write(central); output.Write((ushort)0);
    }
}

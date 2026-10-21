using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;
using static PaneCommander.Tests.ArchiveServiceTests;

namespace PaneCommander.Tests;

[TestClass]
public sealed class ArchiveRollbackTests
{
    [TestMethod]
    public async Task CancellationAfterFirstPublishedFileRestoresOverwrittenDataAndDirectoryTime()
    {
        using var fixture = new ArchiveFixture(); var archive = Path.Combine(fixture.Root, "sample.zip");
        MakeZip(archive, [("sample/a.txt", "new a"), ("sample/b.txt", "new b")]);
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "out")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(target, "sample")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "original a"); File.WriteAllText(Path.Combine(folder, "b.txt"), "original b");
        var date = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc); Directory.SetLastWriteTimeUtc(folder, date);
        using var cancel = new CancellationTokenSource();
        var service = new ArchiveService { AfterPublish = _ => cancel.Cancel() };
        var result = await service.ExtractAsync([archive], target, 0, new ArchiveChoices(), new ArchiveTestProgress(), cancel.Token);
        Assert.IsTrue(result.Cancelled, string.Join("\n", result.Errors));
        Assert.AreEqual("original a", File.ReadAllText(Path.Combine(folder, "a.txt"))); Assert.AreEqual("original b", File.ReadAllText(Path.Combine(folder, "b.txt")));
        Assert.AreEqual(date, Directory.GetLastWriteTimeUtc(folder)); Assert.AreEqual(0, Directory.GetFileSystemEntries(target, ".panecommander-*").Length);
    }
    [TestMethod]
    public async Task RestorationBlockedByExternalFilePreservesBothExternalDataAndBackup()
    {
        using var fixture = new ArchiveFixture(); var archive = Path.Combine(fixture.Root, "sample.zip"); MakeZip(archive, [("a.txt", "new")]);
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "out")).FullName; Directory.CreateDirectory(Path.Combine(target, "sample"));
        var original = Path.Combine(target, "sample/a.txt"); File.WriteAllText(original, "original");
        var service = new ArchiveService { AfterPublish = _ => throw new IOException("injected failure"), BeforeRestore = path => File.WriteAllText(path, "external") };
        var result = await service.ExtractAsync([archive], target, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(1, result.Failed); Assert.AreEqual("external", File.ReadAllText(original));
        var retained = Directory.GetDirectories(target, ".panecommander-*").Single();
        Assert.IsTrue(Directory.GetFiles(retained, "backup-*").Any(path => File.ReadAllText(path) == "original"));
        StringAssert.Contains(result.Errors.Single(), retained);
    }
    [TestMethod]
    public async Task OrdinaryFailureRollsBackOnlyCurrentArchiveAndContinuesFollowingArchives()
    {
        using var fixture = new ArchiveFixture(); var archives = new[] { "one", "two", "three" }.Select(name => Path.Combine(fixture.Root, name + ".zip")).ToArray();
        foreach (var archive in archives) MakeZip(archive, [("file.txt", Path.GetFileName(archive))]);
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "out")).FullName;
        var service = new ArchiveService { AfterPublish = path => { if (path.Contains("two")) throw new IOException("second archive failure"); } };
        var result = await service.ExtractAsync(archives, target, 0, new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(2, result.Succeeded, string.Join("\n", result.Errors)); Assert.AreEqual(1, result.Failed);
        Assert.AreEqual("one.zip", File.ReadAllText(Path.Combine(target, "one/file.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(target, "two"))); Assert.AreEqual("three.zip", File.ReadAllText(Path.Combine(target, "three/file.txt")));
    }
    [TestMethod]
    public async Task CrossArchiveFileDirectoryConflictIsPreflightedAndOverwriteIsProtected()
    {
        using var fixture = new ArchiveFixture(); var one = Path.Combine(fixture.Root, "same.zip"); var two = Path.Combine(fixture.Root, "same.tar");
        MakeZip(one, [("a.txt", "first")]);
        var input = Path.Combine(fixture.Root, "a.txt"); File.WriteAllText(input, "second");
        var creation = await new ArchiveService().CompressAsync(new([input], fixture.Root, "same", ArchiveFormat.Tar, ArchiveLevel.Normal, 0), new ArchiveChoices(), new ArchiveTestProgress(), default);
        Assert.AreEqual(1, creation.Succeeded, string.Join("\n", creation.Errors));
        var target = Directory.CreateDirectory(Path.Combine(fixture.Root, "out")).FullName; var decisions = 0;
        var choices = new ArchiveChoices { Conflict = _ => { decisions++; Assert.AreEqual(0, Directory.GetFileSystemEntries(target).Length); return new(ArchiveConflictAction.Overwrite, true); } };
        var result = await new ArchiveService().ExtractAsync([one, two], target, 0, choices, new ArchiveTestProgress(), default);
        Assert.AreEqual(1, decisions); Assert.AreEqual(2, result.Succeeded, string.Join("\n", result.Errors)); Assert.AreEqual("second", File.ReadAllText(Path.Combine(target, "same/a.txt")));
    }
}

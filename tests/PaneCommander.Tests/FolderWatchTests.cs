using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class FolderWatchTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RealDirectoryNotificationsCoverDownloadLifecycleAndStopAfterDisposal(bool network)
    {
        var shared = Environment.GetEnvironmentVariable("PANECOMMANDER_UNC_TEST_ROOT");
        if (network && string.IsNullOrWhiteSpace(shared)) Assert.Inconclusive("UNCテスト共有の指定がありません。");
        var parent = network ? Path.Combine(shared!, "PaneCommanderReviewTests") : Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var root = Path.Combine(parent, "PaneCommander-watch-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var signals = Channel.CreateUnbounded<bool>();
        Exception? failure = null;
        var watcher = new FolderWatchService().Watch(root, () => signals.Writer.TryWrite(true), error => failure = error);
        try
        {
            async Task Changed()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await signals.Reader.ReadAsync(timeout.Token);
                while (signals.Reader.TryRead(out _)) { }
                Assert.IsNull(failure);
            }
            var partial = Path.Combine(root, "download.crdownload");
            await File.WriteAllTextAsync(partial, "partial"); await Changed();
            await File.AppendAllTextAsync(partial, " more bytes"); await Changed();
            var completed = Path.Combine(root, "download.zip");
            File.Move(partial, completed); await Changed();
            File.Delete(completed); await Changed();
            watcher.Dispose();
            while (signals.Reader.TryRead(out _)) { }
            await File.WriteAllTextAsync(Path.Combine(root, "after-dispose.txt"), "done");
            await Task.Delay(150);
            Assert.IsFalse(signals.Reader.TryRead(out _));
        }
        finally
        {
            watcher.Dispose();
            Assert.AreEqual(parent, Path.GetDirectoryName(root));
            Assert.IsTrue(Path.GetFileName(root).StartsWith("PaneCommander-watch-", StringComparison.Ordinal));
            Directory.Delete(root, true);
        }
    }
}

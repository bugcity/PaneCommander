using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PaneCommander.Infrastructure;

internal sealed class ArchiveOutput(ArchiveItem item, string destination)
{
    internal ArchiveItem Item { get; } = item;
    internal string Destination { get; set; } = destination;
    internal bool Skip { get; set; }
}
internal sealed class ArchiveWorkspace : IDisposable
{
    internal string Root { get; }
    internal bool Retain { get; set; }
    private readonly ArchiveLease lease;
    internal ArchiveWorkspace(string directory)
    {
        Root = Path.Combine(directory, $".panecommander-{Guid.NewGuid():N}.tmp");
        if (!CreateDirectory(Root, IntPtr.Zero)) throw new Win32Exception();
        ArchiveTemporaryPaths.Add(Root);
        try { lease = new ArchiveLease(Root, metadata: true); }
        catch { ArchiveTemporaryPaths.Remove(Root); throw; }
    }
    public void Dispose()
    {
        try
        {
            if (!Retain)
            {
                PurgeChildren(Root); var original = lease.Stamp; lease.Dispose();
                using var deletion = new ArchiveLease(Root, delete: true);
                if (deletion.Stamp.Volume != original.Volume || deletion.Stamp.Id != original.Id) throw new IOException("一時領域が置換されたため削除しません。");
                deletion.Delete();
            }
        }
        finally { lease.Dispose(); if (!Retain) ArchiveTemporaryPaths.Remove(Root); }
    }
    private static void PurgeChildren(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            using var child = new ArchiveLease(path, delete: true, metadata: true);
            if (child.Stamp.Attributes.HasFlag(FileAttributes.Directory)) PurgeChildren(path);
            child.Metadata(null, child.Stamp.Attributes & ~FileAttributes.ReadOnly);
            child.Delete();
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateDirectory(string path, IntPtr security);
    internal static void CreateNewDirectory(string path) { if (!CreateDirectory(path, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
}
internal static class ArchiveSnapshots
{
    internal static void Capture(string path, Dictionary<string, ArchiveStamp?> expected, bool deep = false)
    {
        if (expected.ContainsKey(path))
        {
            if (deep && expected[path]?.Attributes.HasFlag(FileAttributes.Directory) == true)
                foreach (var child in Directory.EnumerateFileSystemEntries(path)) Capture(child, expected, true);
            return;
        }
        var attr = ArchivePaths.Attributes(path);
        if (attr is null) { expected[path] = null; return; }
        using var lease = new ArchiveLease(path);
        expected[path] = lease.Stamp;
        if (deep && attr.Value.HasFlag(FileAttributes.Directory)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) Capture(child, expected, true);
    }
    internal static bool Matches(ArchiveStamp a, ArchiveStamp b, bool directory) => a.Volume == b.Volume && a.Id == b.Id && StableAttributes(a.Attributes) == StableAttributes(b.Attributes) && (directory || a.Length == b.Length && a.Modified == b.Modified);
    // Windows can set Archive after the publishing handle closes. This is not a content change.
    private static FileAttributes StableAttributes(FileAttributes attributes) => attributes & ~(FileAttributes.Archive | FileAttributes.Normal);
}
internal sealed class ArchiveTransaction(ArchiveDirectoryGuard guard, ArchiveWorkspace workspace, Dictionary<string, ArchiveStamp?> expected, Action<string>? afterPublish = null, Action<string>? beforeRestore = null, Action<string>? rollbackReported = null)
{
    private readonly List<(ArchiveLease Root, List<ArchiveLease> Children, string Original)> backups = [];
    private readonly List<ArchiveLease> published = [];
    private readonly List<(string Path, ArchiveStamp Stamp)> directories = [];
    private readonly List<(ArchiveLease Lease, ArchiveStamp Stamp)> changedDirectories = [];
    internal List<string> Commit(IReadOnlyList<ArchiveOutput> outputs, string stage, CancellationToken token)
    {
        var warnings = new List<string>();
        try
        {
            foreach (var output in outputs.Where(o => !o.Skip).OrderBy(o => Depth(o.Destination)).ThenBy(o => !o.Item.Directory))
            {
                token.ThrowIfCancellationRequested();
                var destination = output.Destination;
                guard.Hold(Path.GetDirectoryName(destination)!);
                var attributes = ArchivePaths.Attributes(destination);
                var directory = attributes?.HasFlag(FileAttributes.Directory) == true;
                if (attributes is not null)
                {
                    if (!expected.TryGetValue(destination, out var snapshot) || snapshot is null) throw new IOException($"事前解析後に出力先が作成されました: {destination}");
                    if (output.Item.Directory && directory)
                    {
                        var existing = guard.Hold(destination);
                        if (!ArchiveSnapshots.Matches(snapshot, existing.Stamp, true)) throw new IOException($"出力フォルダーが変更されました: {destination}");
                        changedDirectories.Add((existing, existing.Stamp));
                        continue;
                    }
                    Backup(destination, snapshot);
                }
                else if (expected.TryGetValue(destination, out var previous) && previous is not null) throw new IOException($"事前解析後に出力先が削除されました: {destination}");
                if (output.Item.Directory)
                {
                    ArchiveWorkspace.CreateNewDirectory(destination);
                    var created = guard.Hold(destination); directories.Add((destination, created.Stamp));
                }
                else
                {
                    var source = ArchivePaths.Under(stage, output.Item.Name);
                    var file = new ArchiveLease(source, delete: true, metadata: true);
                    try
                    {
                        file.Metadata(output.Item.Modified, output.Item.Attributes);
                        file.Rename(destination); published.Add(file); afterPublish?.Invoke(destination);
                    }
                    catch { if (!published.Contains(file)) file.Dispose(); throw; }
                }
            }
            foreach (var output in outputs.Where(o => !o.Skip && o.Item.Directory && o.Item.Recorded).OrderByDescending(o => Depth(o.Destination)))
                guard.Hold(output.Destination).Metadata(output.Item.Modified, output.Item.Attributes | FileAttributes.Directory);
            token.ThrowIfCancellationRequested();
            foreach (var backup in backups)
            {
                foreach (var path in expected.Keys.Where(path => path.Equals(backup.Original, StringComparison.OrdinalIgnoreCase) || path.StartsWith(backup.Original + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray()) expected.Remove(path);
            }
            foreach (var output in outputs.Where(o => !o.Skip))
            {
                var lease = output.Item.Directory ? guard.Hold(output.Destination) : published.Single(file => file.PathName == output.Destination);
                expected[output.Destination] = lease.Stamp;
            }
        }
        catch (Exception original)
        {
            var failures = Rollback();
            if (failures.Count != 0)
            {
                workspace.Retain = true;
                rollbackReported?.Invoke($"incomplete; retained={workspace.Root}; errors=" + string.Join("; ", failures));
                throw new IOException($"反映を中止しましたが復元が完了していません。バックアップを保持しました: {workspace.Root}\n" + string.Join("\n", failures), original);
            }
            rollbackReported?.Invoke("completed; previous files restored; published files removed");
            throw;
        }
        finally { foreach (var file in published) file.Dispose(); }
        foreach (var backup in backups)
        {
            try { foreach (var child in backup.Children.AsEnumerable().Reverse()) { child.Delete(); child.Dispose(); } backup.Root.Delete(); }
            catch (Exception ex) { workspace.Retain = true; warnings.Add($"不要になったバックアップの削除に失敗しました: {workspace.Root}: {ex.Message}"); }
            finally { foreach (var child in backup.Children) child.Dispose(); backup.Root.Dispose(); }
        }
        return warnings;
    }
    private void Backup(string path, ArchiveStamp stamp)
    {
        guard.ReleaseTree(path);
        var children = new List<ArchiveLease>(); var root = new ArchiveLease(path, delete: true);
        try
        {
            if (!ArchiveSnapshots.Matches(stamp, root.Stamp, stamp.Attributes.HasFlag(FileAttributes.Directory))) throw new IOException($"上書き対象が変更されました: {path}");
            if (stamp.Attributes.HasFlag(FileAttributes.Directory)) LockTree(path, children);
            var actual = new HashSet<string>(children.Select(child => child.PathName), StringComparer.OrdinalIgnoreCase);
            if (expected.Where(pair => pair.Value is not null && pair.Key.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).Any(pair => !actual.Contains(pair.Key))) throw new IOException($"上書きフォルダーの内容が削除されています: {path}");
            // A Windows directory cannot reliably be renamed while descendants are open.
            // Reopen and verify every identity immediately after moving, before publishing.
            foreach (var child in children) child.Dispose(); children.Clear();
            root.Rename(Path.Combine(workspace.Root, $"backup-{Guid.NewGuid():N}"));
            backups.Add((root, children, path));
            if (stamp.Attributes.HasFlag(FileAttributes.Directory))
            {
                LockMovedTree(root.PathName);
                if (children.Count != actual.Count) throw new IOException("バックアップ中にフォルダーの内容が変更されました。");
            }
            void LockMovedTree(string folder)
            {
                foreach (var movedPath in Directory.EnumerateFileSystemEntries(folder))
                {
                    var child = new ArchiveLease(movedPath, delete: true); children.Add(child);
                    var original = Path.Combine(path, Path.GetRelativePath(root.PathName, movedPath));
                    if (!expected.TryGetValue(original, out var saved) || saved is null || !ArchiveSnapshots.Matches(saved, child.Stamp, child.Stamp.Attributes.HasFlag(FileAttributes.Directory))) throw new IOException("バックアップ中に項目が変更されました。");
                    if (child.Stamp.Attributes.HasFlag(FileAttributes.Directory)) LockMovedTree(movedPath);
                }
            }
        }
        catch { if (!backups.Any(b => ReferenceEquals(b.Root, root))) { foreach (var child in children) child.Dispose(); root.Dispose(); } throw; }
    }
    private void LockTree(string path, List<ArchiveLease> leases)
    {
        foreach (var childPath in Directory.EnumerateFileSystemEntries(path))
        {
            var child = new ArchiveLease(childPath, delete: true); leases.Add(child);
            if (!expected.TryGetValue(childPath, out var snapshot) || snapshot is null || !ArchiveSnapshots.Matches(snapshot, child.Stamp, child.Stamp.Attributes.HasFlag(FileAttributes.Directory))) throw new IOException($"上書きフォルダーの内容が変更されました: {childPath}");
            if (child.Stamp.Attributes.HasFlag(FileAttributes.Directory)) LockTree(childPath, leases);
        }
    }
    private List<string> Rollback()
    {
        var failures = new List<string>();
        foreach (var file in published.AsEnumerable().Reverse())
        {
            try { file.Metadata(null, file.Stamp.Attributes & ~FileAttributes.ReadOnly); file.Delete(); file.Dispose(); }
            catch (Exception ex) { failures.Add(ex.Message); }
        }
        foreach (var (path, stamp) in directories.AsEnumerable().Reverse())
        {
            try
            {
                guard.Release(path);
                using var lease = new ArchiveLease(path, delete: true);
                if (lease.Stamp.Id != stamp.Id || lease.Stamp.Volume != stamp.Volume) throw new IOException($"作成フォルダーが置換されています: {path}");
                lease.Delete();
            }
            catch (Exception ex) { failures.Add(ex.Message); }
        }
        foreach (var backup in backups.AsEnumerable().Reverse())
        {
            try { foreach (var child in backup.Children) child.Dispose(); beforeRestore?.Invoke(backup.Original); backup.Root.Rename(backup.Original); }
            catch (Exception ex) { failures.Add(ex.Message); }
            finally { foreach (var child in backup.Children) child.Dispose(); backup.Root.Dispose(); }
        }
        foreach (var (lease, stamp) in changedDirectories.AsEnumerable().Reverse())
        {
            try { lease.Metadata(DateTime.FromFileTimeUtc(stamp.Modified), stamp.Attributes); }
            catch (Exception ex) { failures.Add(ex.Message); }
        }
        return failures;
    }
    private static int Depth(string path) => path.Count(c => c == Path.DirectorySeparatorChar);
}

using System.Diagnostics;
using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

public sealed class ArchiveService(ErrorLogger? logger = null) : IArchiveService
{
    internal Func<string, long>? AvailableBytes { get; init; }
    internal Action<string>? BeforeCommit { get; init; }
    internal Action<string>? AfterPublish { get; init; }
    internal Action<string>? BeforeRestore { get; init; }
    private sealed class Plan(string source, ArchiveFormat format, ArchiveLease lease) : IDisposable
    {
        internal string Source = source;
        internal ArchiveFormat Format = format;
        internal readonly ArchiveLease Lease = lease;
        internal ZipNameMetadata? Zip;
        internal string[]? Names;
        internal List<ArchiveItem> Entries = [];
        internal List<ArchiveOutput> Outputs = [];
        public void Dispose() => Lease.Dispose();
    }
    public async Task<ArchiveBatchResult> ExtractAsync(string[] archives, string directory, long reserveBytes, IArchiveInteraction interaction, IProgress<ArchiveProgress> progress, CancellationToken token)
    {
        var started = Stopwatch.StartNew(); var result = new Result(); var plans = new List<Plan>();
        var expected = new Dictionary<string, ArchiveStamp?>(StringComparer.OrdinalIgnoreCase);
        var occupied = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        ArchiveConflictChoice? all = null;
        try
        {
            using var guard = await Task.Run(() => new ArchiveDirectoryGuard(directory), token);
            logger?.Archive("extract-start", archives, null, null);
            foreach (var path in archives)
            {
                token.ThrowIfCancellationRequested();
                if (ArchiveFormats.Detect(path) is not { } format || Directory.Exists(path)) { result.Skipped++; continue; }
                Plan? plan = null;
                try
                {
                    plan = await Task.Run(() =>
                    {
                        ArchivePaths.NoLinks(path); var p = new Plan(path, format, new ArchiveLease(path, data: true));
                        try { if (format == ArchiveFormat.Zip) { using var stream = File.OpenRead(path); p.Zip = ZipNameMetadata.Read(stream, token); } return p; }
                        catch { p.Dispose(); throw; }
                    }, token);
                    if (plan.Zip is { } zip)
                    {
                        var chosen = zip.Encoding;
                        if (zip.NeedsChoice) chosen = await interaction.ChooseEncodingAsync(zip.Preview(path), token);
                        logger?.Archive("zip-encoding", [path], $"encoding={chosen}; user={zip.NeedsChoice}", null);
                        if (chosen is null) { result.Skipped++; plan.Dispose(); continue; }
                        plan.Names = zip.Names(chosen.Value);
                    }
                    plan.Entries = await Task.Run(() => ArchiveCodec.Inspect(path, format, plan.Zip, plan.Names, token), token);
                    var stem = ArchivePaths.Relative(ArchiveFormats.Stem(Path.GetFileName(path)));
                    var direct = plan.Entries.Count > 0 && plan.Entries.All(e => e.Name.StartsWith(stem + "/", StringComparison.OrdinalIgnoreCase) || e.Directory && e.Name.Equals(stem, StringComparison.OrdinalIgnoreCase));
                    plan.Outputs = Outputs(plan.Entries, directory, direct ? "" : stem);
                    ValidateOutputTree(plan.Outputs);
                    // Include full existing subtrees only when a file would replace a directory.
                    await Task.Run(() => { foreach (var output in plan.Outputs) ArchiveSnapshots.Capture(output.Destination, expected, !output.Item.Directory); }, token);
                    plans.Add(plan); plan = null;
                }
                catch (OperationCanceledException) { plan?.Dispose(); throw; }
                catch (Exception ex) { plan?.Dispose(); result.Failed++; result.Errors.Add($"{path}: {ex.Message}"); logger?.Archive("extract-analysis-failed", [path], null, ex); }
            }
            // Resolve the entire batch before writing any extracted data.
            foreach (var plan in plans)
            {
                foreach (var output in plan.Outputs.OrderBy(o => o.Destination.Length))
                {
                    token.ThrowIfCancellationRequested();
                    if (output.Skip) continue;
                    var path = output.Destination;
                    var exists = expected.TryGetValue(path, out var snapshot) && snapshot is not null;
                    var planned = occupied.TryGetValue(path, out var plannedDirectory);
                    var oldDirectory = planned ? plannedDirectory : snapshot?.Attributes.HasFlag(FileAttributes.Directory) == true;
                    if ((exists || planned) && !(output.Item.Directory && oldDirectory))
                    {
                        var choice = all ?? await interaction.ResolveConflictAsync(new(plan.Source, path, output.Item.Directory, output.Item.Directory != oldDirectory), token);
                        if (choice.ApplyToAll) all = choice;
                        if (choice.Action == ArchiveConflictAction.Cancel) throw new OperationCanceledException(token);
                        if (choice.Action == ArchiveConflictAction.Skip) { RewriteSubtree(plan.Outputs, output, null); continue; }
                        if (choice.Action == ArchiveConflictAction.Rename)
                        {
                            var replacement = Unique(path, output.Item.Directory, occupied);
                            RewriteSubtree(plan.Outputs, output, replacement);
                            foreach (var rewritten in plan.Outputs.Where(o => !o.Skip)) if (!expected.ContainsKey(rewritten.Destination)) await Task.Run(() => ArchiveSnapshots.Capture(rewritten.Destination, expected, !rewritten.Item.Directory), token);
                            path = output.Destination;
                        }
                    }
                    occupied[path] = output.Item.Directory;
                }
            }
            var activePlans = plans.Where(p => p.Outputs.Any(o => o.Item.Recorded && !o.Skip)).ToArray();
            if (activePlans.Length > 0)
            {
                try { await Task.Run(() => ArchiveSpace.Require(directory, reserveBytes, activePlans.Sum(p => p.Entries.Sum(e => e.Size)), AvailableBytes), token); }
                catch (ArchiveSpaceException ex) { result.Failed++; result.Cancelled = true; result.Errors.Add(ex.Message); logger?.Archive("extract-preflight-disk-stop", archives, null, ex); return result.Value; }
            }
            var tracker = new Tracker(progress, activePlans.Sum(p => p.Entries.Sum(e => e.Size)), activePlans.Sum(p => p.Entries.Count));
            foreach (var plan in plans)
            {
                if (plan.Outputs.Where(o => o.Item.Recorded).All(o => o.Skip)) { result.Skipped++; continue; }
                token.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew(); ArchiveWorkspace? workspace = null;
                try
                {
                    await Task.Run(() =>
                    {
                        ArchiveSpace.Require(directory, reserveBytes, plan.Entries.Sum(e => e.Size), AvailableBytes);
                        workspace = new ArchiveWorkspace(directory);
                        var stage = Path.Combine(workspace.Root, "stage"); Directory.CreateDirectory(stage);
                        ArchiveCodec.Extract(plan.Source, plan.Format, plan.Zip, plan.Names, stage,
                            (entry, n) => tracker.Update("展開", entry.Name, n, n == 0), n => ArchiveSpace.Require(directory, reserveBytes, n, AvailableBytes), token);
                        BeforeCommit?.Invoke(plan.Source);
                        result.Warnings.AddRange(new ArchiveTransaction(guard, workspace, expected, AfterPublish, BeforeRestore,
                            state => logger?.Archive("extract-rollback", [plan.Source], state, null)).Commit(plan.Outputs, stage, token));
                        foreach (var path in TopOutputs(plan.Outputs)) result.Outputs.Add(path);
                    }, token);
                    result.Succeeded++; logger?.Archive("extract-complete", [plan.Source], $"elapsedMs={watch.ElapsedMilliseconds}", null);
                }
                catch (ArchiveSpaceException ex) { result.Failed++; result.Errors.Add($"{plan.Source}: {ex.Message}"); result.Cancelled = true; logger?.Archive("extract-disk-stop", [plan.Source], null, ex); break; }
                catch (OperationCanceledException) { result.Cancelled = true; logger?.Archive("extract-cancelled", [plan.Source], "current archive rolled back or not committed", null); break; }
                catch (Exception ex) { result.Failed++; result.Errors.Add($"{plan.Source}: {ex.Message}"); logger?.Archive("extract-failed", [plan.Source], "rollback attempted; backup retained if reported", ex); }
                finally { DisposeWorkspace(workspace, result); }
            }
            tracker.Finish();
        }
        catch (OperationCanceledException) { result.Cancelled = true; }
        catch (Exception ex)
        {
            result.Failed += Math.Max(1, archives.Length - result.Succeeded - result.Failed - result.Skipped);
            result.Errors.Add($"{directory}: {ex.Message}");
            logger?.Archive("extract-batch-failed", archives, "preflight or batch stopped", ex);
        }
        finally { foreach (var plan in plans) plan.Dispose(); logger?.Archive("extract-end", archives, $"success={result.Succeeded}; failed={result.Failed}; skipped={result.Skipped}; cancelled={result.Cancelled}; elapsedMs={started.ElapsedMilliseconds}", null); }
        return result.Value;
    }
    public async Task<ArchiveBatchResult> CompressAsync(ArchiveCompressionRequest request, IArchiveInteraction interaction, IProgress<ArchiveProgress> progress, CancellationToken token)
    {
        var result = new Result(); var watch = Stopwatch.StartNew(); ArchiveWorkspace? workspace = null; string? temporary = null;
        var sourceGuards = new List<ArchiveDirectoryGuard>();
        logger?.Archive("compress-start", request.Sources, $"format={request.Format}; level={request.Level}", null);
        try
        {
            if (!ArchiveFormats.CanCreate(request.Format) || !ArchiveFormats.SupportsLevel(request.Format, request.Level)) throw new NotSupportedException("対応しない作成形式・圧縮レベルです。");
            var name = ArchivePaths.Relative(request.Name);
            if (name.Contains('/')) throw new ArgumentException("アーカイブ名にはフォルダーを含めないでください。");
            if (!name.EndsWith(ArchiveFormats.Extension(request.Format), StringComparison.OrdinalIgnoreCase)) name += ArchiveFormats.Extension(request.Format);
            using var guard = await Task.Run(() => new ArchiveDirectoryGuard(request.Directory), token);
            var entries = await Task.Run(() => Collect(request.Sources, result.Warnings, sourceGuards, token), token);
            if (!entries.Any()) throw new IOException("圧縮できる項目がありません。");
            var expected = new Dictionary<string, ArchiveStamp?>(StringComparer.OrdinalIgnoreCase);
            var destination = ArchivePaths.Under(request.Directory, name);
            await Task.Run(() => ArchiveSnapshots.Capture(destination, expected, true), token);
            if (expected[destination] is not null)
            {
                var choice = await interaction.ResolveConflictAsync(new("圧縮", destination, false, expected[destination]!.Attributes.HasFlag(FileAttributes.Directory)), token);
                if (choice.Action == ArchiveConflictAction.Cancel) throw new OperationCanceledException(token);
                if (choice.Action == ArchiveConflictAction.Skip) { result.Skipped++; return result.Value; }
                if (choice.Action == ArchiveConflictAction.Rename) { destination = Unique(destination, false, []); name = Path.GetFileName(destination); await Task.Run(() => ArchiveSnapshots.Capture(destination, expected), token); }
            }
            var tracker = new Tracker(progress, entries.Sum(e => e.Size), entries.Count);
            await Task.Run(() =>
            {
                ArchiveSpace.Require(request.Directory, request.ReserveBytes, 65536, AvailableBytes);
                workspace = new ArchiveWorkspace(request.Directory);
                temporary = Path.Combine(request.Directory, $".panecommander-{Guid.NewGuid():N}.tmp"); ArchiveTemporaryPaths.Add(temporary);
                ArchiveCodec.Create(temporary, request.Format, request.Level, entries, (entry, n) => tracker.Update("圧縮", entry.Name, n, n == 0), n => ArchiveSpace.Require(request.Directory, request.ReserveBytes, n, AvailableBytes), token);
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested(); var info = new FileInfo(entry.Source!);
                    if (File.GetLastWriteTimeUtc(entry.Source!) != entry.Modified || !entry.Directory && info.Length != entry.Size) throw new IOException($"圧縮中に入力が変更されました: {entry.Source}");
                }
                ZipNameMetadata? zip = null; string[]? names = null;
                using (var protection = new ArchiveLease(temporary, data: true))
                {
                    if (request.Format == ArchiveFormat.Zip) { using var input = File.OpenRead(temporary); zip = ZipNameMetadata.Read(input, token); names = zip.Names(ZipNameEncoding.Utf8); }
                    var verified = ArchiveCodec.Inspect(temporary, request.Format, zip, names, token);
                    var map = verified.ToDictionary(e => e.Name, StringComparer.Ordinal);
                    if (verified.Count != entries.Count || entries.Any(e => !map.TryGetValue(e.Name, out var other) || other.Directory != e.Directory || other.Size != e.Size)) throw new InvalidDataException("圧縮後の構造・件数・サイズ検証に失敗しました。");
                }
                var stage = Path.Combine(workspace.Root, "stage"); Directory.CreateDirectory(stage);
                using (var lease = new ArchiveLease(temporary, delete: true)) lease.Rename(Path.Combine(stage, name));
                ArchiveTemporaryPaths.Remove(temporary); temporary = null;
                BeforeCommit?.Invoke(destination);
                var item = new ArchiveItem(name, false, new FileInfo(Path.Combine(stage, name)).Length, null, 0);
                result.Warnings.AddRange(new ArchiveTransaction(guard, workspace, expected, AfterPublish, BeforeRestore,
                    state => logger?.Archive("compress-rollback", [destination], state, null)).Commit([new(item, destination)], stage, token));
                result.Outputs.Add(destination); result.Succeeded++; tracker.Finish();
            }, token);
        }
        catch (OperationCanceledException) { result.Cancelled = true; }
        catch (Exception ex) { result.Failed++; result.Errors.Add(ex.Message); logger?.Archive("compress-failed", request.Sources, "rollback attempted; backup retained if reported", ex); }
        finally
        {
            if (temporary is not null)
            {
                try { using var lease = new ArchiveLease(temporary, delete: true); lease.Delete(); }
                catch (Exception ex) { result.Warnings.Add($"一時ファイルを保持しました: {temporary}: {ex.Message}"); }
                ArchiveTemporaryPaths.Remove(temporary);
            }
            DisposeWorkspace(workspace, result);
            foreach (var sourceGuard in sourceGuards) sourceGuard.Dispose();
            logger?.Archive("compress-end", request.Sources, $"success={result.Succeeded}; failed={result.Failed}; cancelled={result.Cancelled}; elapsedMs={watch.ElapsedMilliseconds}", null);
        }
        return result.Value;
    }
    private static List<ArchiveItem> Collect(string[] sources, List<string> warnings, List<ArchiveDirectoryGuard> guards, CancellationToken token)
    {
        var result = new List<ArchiveItem>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ArchiveDirectoryGuard? guard = null;
        foreach (var source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            guard = new ArchiveDirectoryGuard(Path.GetDirectoryName(Path.GetFullPath(Path.TrimEndingDirectorySeparator(source)))!, writable: false); guards.Add(guard);
            Walk(source, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
        }
        return result;
        void Walk(string source, string name)
        {
            token.ThrowIfCancellationRequested(); var attributes = File.GetAttributes(source);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (LinkMetadata.Kind(source) is FileLinkKind.SymbolicLink or FileLinkKind.Junction) { warnings.Add($"リンクを除外: {source}"); return; }
                throw new IOException($"安全に扱えない再解析ポイントです: {source}");
            }
            ArchivePaths.NoLinks(source); name = ArchivePaths.Relative(name);
            if (!names.Add(name)) throw new IOException($"入力名が重複しています: {name}");
            var directory = attributes.HasFlag(FileAttributes.Directory);
            if (directory) guard!.Hold(source);
            result.Add(new(name, directory, directory ? 0 : new FileInfo(source).Length, File.GetLastWriteTimeUtc(source), attributes, source));
            if (directory) foreach (var child in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.OrdinalIgnoreCase)) Walk(child, name + "/" + Path.GetFileName(child));
        }
    }
    private static List<ArchiveOutput> Outputs(List<ArchiveItem> entries, string directory, string prefix)
    {
        var output = new Dictionary<string, ArchiveOutput>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var relative = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
            var path = ArchivePaths.Under(directory, relative);
            if (output.TryGetValue(path, out var duplicate) && (!entry.Directory || !duplicate.Item.Directory)) throw new InvalidDataException($"アーカイブ内の出力が重複しています: {entry.Name}");
            output[path] = new(entry, path);
            for (var parent = Path.GetDirectoryName(path); parent is not null && !parent.Equals(directory, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
            {
                if (!output.ContainsKey(parent)) output[parent] = new(new(Path.GetRelativePath(directory, parent).Replace('\\', '/'), true, 0, null, 0, Recorded: false), parent);
            }
        }
        return output.Values.ToList();
    }
    private static void ValidateOutputTree(List<ArchiveOutput> outputs)
    {
        var files = outputs.Where(o => !o.Item.Directory).Select(o => o.Destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs) for (var parent = Path.GetDirectoryName(output.Destination); parent is not null; parent = Path.GetDirectoryName(parent)) if (files.Contains(parent)) throw new InvalidDataException("アーカイブ内でファイルとフォルダーが衝突しています。");
    }
    private static string Unique(string path, bool directory, Dictionary<string, bool> occupied)
    {
        var parent = Path.GetDirectoryName(path)!; var name = Path.GetFileName(path); var extension = directory ? "" : Path.GetExtension(name); var stem = name[..^extension.Length];
        for (var index = 2; index < int.MaxValue; index++) { var candidate = Path.Combine(parent, $"{stem} ({index}){extension}"); if (!occupied.ContainsKey(candidate) && ArchivePaths.Attributes(candidate) is null) return candidate; }
        throw new IOException("名前変更先を確保できません。");
    }
    private static void RewriteSubtree(List<ArchiveOutput> outputs, ArchiveOutput root, string? destination)
    {
        var old = root.Destination;
        foreach (var output in outputs.Where(o => o.Destination.Equals(old, StringComparison.OrdinalIgnoreCase) || root.Item.Directory && o.Destination.StartsWith(old + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        { if (destination is null) output.Skip = true; else output.Destination = destination + output.Destination[old.Length..]; }
    }
    private static IEnumerable<string> TopOutputs(List<ArchiveOutput> outputs) => outputs.Where(o => !o.Skip && !outputs.Any(parent => !parent.Skip && parent.Item.Directory && o.Destination.StartsWith(parent.Destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).Select(o => o.Destination);
    private static void DisposeWorkspace(ArchiveWorkspace? workspace, Result result)
    {
        if (workspace is null) return;
        try { workspace.Dispose(); } catch (Exception ex) { result.Warnings.Add($"一時領域を削除できません。保持しました: {workspace.Root}: {ex.Message}"); }
    }
    private sealed class Result
    {
        internal int Succeeded, Failed, Skipped; internal bool Cancelled; internal readonly List<string> Outputs = [], Errors = [], Warnings = [];
        internal ArchiveBatchResult Value => new(Succeeded, Failed, Skipped, Cancelled, Outputs.ToArray(), Errors.ToArray(), Warnings.ToArray());
    }
    private sealed class Tracker(IProgress<ArchiveProgress> progress, long total, int files)
    {
        private readonly Stopwatch watch = Stopwatch.StartNew(); private long bytes, last; private int completed;
        internal void Update(string phase, string name, int count, bool finished)
        {
            bytes = checked(bytes + count); if (finished) completed++;
            if (watch.ElapsedMilliseconds - last < 100 && completed != files) return;
            last = watch.ElapsedMilliseconds; progress.Report(new(phase, name, bytes, total, completed, files));
        }
        internal void Finish() => progress.Report(new("完了", "", bytes, total, completed, files));
    }
}

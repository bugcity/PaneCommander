using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PaneCommander.Core;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PaneCommander.Tests")]

namespace PaneCommander.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class ShellFileOperationService : IFileOperationService
{
    private readonly string[]? testScopes;
    private readonly Action<string>? testRecycleReceipt;
    public ShellFileOperationService() { }
    private ShellFileOperationService(string[] scopes,Action<string>? receipt) { testScopes = scopes;testRecycleReceipt=receipt; }
    internal static ShellFileOperationService ForIsolatedTest(string scope,Action<string>? recycleReceipt=null)
    {
        var path = FileSystemService.NormalizePath(scope); var parent = Path.Combine(Path.GetTempPath(), "PaneCommanderReviewTests");
        var isolatedParent = string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\", StringComparison.Ordinal) && Path.GetFileName(Path.GetDirectoryName(path)) == "PaneCommanderReviewTests";
        if (!isolatedParent || !Guid.TryParse(Path.GetFileName(path), out _) || !Directory.Exists(path)) throw new ArgumentException("隔離されたテスト用GUIDディレクトリが必要です。");
        return new([path],recycleReceipt);
    }
    internal static ShellFileOperationService ForIsolatedTransferTest(string first, string second)
    {
        var firstScope = ForIsolatedTest(first); var secondScope = ForIsolatedTest(second);
        return new([firstScope.testScopes![0], secondScope.testScopes![0]], null);
    }
    public Task<FileOperationResult> ExecuteAsync(FileOperationRequest request, IntPtr owner, CancellationToken token = default)
    {
        DropPolicy.Validate(request); var completion = new TaskCompletionSource<FileOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            IFileOperation? operation = null; var items = new List<IntPtr>();IntPtr receiptPointer=IntPtr.Zero;RecycleReceiptSink? receipt=null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (request.Kind == FileOperationKind.Delete && request.Recycle)
                    foreach (var source in request.Sources)
                        if (FileSystemService.NormalizePath(source).StartsWith(@"\\", StringComparison.Ordinal))
                            throw new IOException("UNC共有のごみ箱への移動には対応していません。削除を中止しました。完全削除には自動で切り替えません。");
                if (testScopes is not null)
                {
                    foreach (var path in request.Sources.Concat(request.Destination is null ? [] : new[] { request.Destination }))
                    { var full = Path.GetFullPath(path); if (!testScopes.Any(scope => full.Equals(scope, StringComparison.OrdinalIgnoreCase) || full.StartsWith(scope + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("テスト操作が隔離領域外を参照しています。"); }
                }
                // Shell receives the link object itself. Never recursively enumerate/delete targets here.
                foreach (var path in request.Sources) { var parent = Path.GetDirectoryName(Path.GetFullPath(path))!; FileSystemService.ValidatePath(parent, token); _ = File.GetAttributes(path); }
                if (request.Destination is not null) FileSystemService.ValidatePath(request.Destination, token);
                if (request.Kind == FileOperationKind.CreateFile)
                {
                    token.ThrowIfCancellationRequested();
                    using (new FileStream(Path.Combine(request.Destination!, request.NewName!), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                    completion.TrySetResult(new(false)); return;
                }
                if (request.Kind == FileOperationKind.CreateDirectory)
                {
                    CreateDirectoryTree(request.Destination!, request.NewName!, token);
                    completion.TrySetResult(new(false)); return;
                }
                if(request.Kind==FileOperationKind.Delete&&request.Recycle)
                    foreach(var root in request.Sources.Select(Path.GetPathRoot).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var drive=new DriveInfo(root!);
                        if(drive.DriveType!=DriveType.Fixed||!drive.DriveFormat.Equals("NTFS",StringComparison.OrdinalIgnoreCase))throw new IOException("このボリュームのごみ箱は検証対象外のため削除を中止しました。完全削除には切り替えません。");
                    }
                if (request.Kind is FileOperationKind.Copy or FileOperationKind.Move)
                {
                    var physicalDestination = Path.TrimEndingDirectorySeparator(FileSystemService.ResolveTarget(request.Destination!, token));
                    foreach (var source in request.Sources)
                    {
                        var attributes = File.GetAttributes(source);
                        if (attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint))
                        { var physicalSource = Path.TrimEndingDirectorySeparator(FileSystemService.ResolveTarget(source, token)); if (physicalDestination.Equals(physicalSource, StringComparison.OrdinalIgnoreCase) || physicalDestination.StartsWith(physicalSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("リンク先を含め、対象自身の配下へはコピー・移動できません。"); }
                        var collision = Path.Combine(request.Destination!, request.NewName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
                        if (request.Kind == FileOperationKind.Copy && request.NewName is null && string.Equals(Path.GetFullPath(source), Path.GetFullPath(collision), StringComparison.OrdinalIgnoreCase)) continue;
                        try { if (File.GetAttributes(collision).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("送り先に既存のリンクがあります。リンク先を意図せず上書きしないよう、別の名前を指定してください。"); }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                    }
                }
                var containsLinks = false;
                foreach (var source in request.Sources) containsLinks |= LinkObjectOperations.ContainsLink(source, token);
                if (containsLinks)
                {
                    if (request.Kind == FileOperationKind.Delete && request.Recycle) throw new IOException("リンクを含む項目のごみ箱への移動は安全性未検証のため実行できません。完全削除には切り替えません。");
                    if (request.Kind == FileOperationKind.Delete && testScopes is null && MessageBox(owner, $"選択した{request.Sources.Length}項目を完全削除しますか？\nリンク先の内容は削除しません。", "PaneCommander — 完全削除", 0x134) != 6)
                    { completion.TrySetResult(new(true)); return; }
                    var destinations = request.Sources.Select(source => request.Destination is null ? "" : Path.Combine(request.Destination, request.NewName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(source)))).ToArray();
                    if (request.Kind is FileOperationKind.Copy or FileOperationKind.Move)
                    {
                        for (var i = 0; i < destinations.Length; i++)
                        {
                            if (request.Kind == FileOperationKind.Copy && request.NewName is null && string.Equals(Path.GetFullPath(request.Sources[i]), Path.GetFullPath(destinations[i]), StringComparison.OrdinalIgnoreCase))
                            { var original = destinations[i]; var suffix = 1; do { destinations[i] = original + $" - コピー ({suffix++})"; } while (Exists(destinations[i])); }
                            LinkObjectOperations.EnsureAbsent(destinations[i]);
                        }
                        if (destinations.Distinct(StringComparer.OrdinalIgnoreCase).Count() != destinations.Length) throw new IOException("送り先の名前が重複しています。");
                    }
                    for (var i = 0; i < request.Sources.Length; i++)
                    {
                        switch (request.Kind)
                        {
                            case FileOperationKind.Copy: LinkObjectOperations.Copy(request.Sources[i], destinations[i], token); LinkObjectOperations.Verify(request.Sources[i], destinations[i], token); break;
                            case FileOperationKind.Move: LinkObjectOperations.Move(request.Sources[i], destinations[i], token); break;
                            case FileOperationKind.Delete: LinkObjectOperations.Delete(request.Sources[i], token); break;
                        }
                    }
                    completion.TrySetResult(new(false)); return;
                }
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true)!)!;
                operation.SetOwnerWindow(owner);
                uint flags = 0x4000; // Warn before any permanent destruction; keep collision/error/progress UI.
                if (request.Kind != FileOperationKind.Delete || request.Recycle) flags |= 0x20000000;
                if (request.Kind == FileOperationKind.Delete && request.Recycle) flags |= 0x00080000;
                if (request.Kind == FileOperationKind.Copy && request.NewName is null && request.Sources.All(s => string.Equals(Path.GetDirectoryName(Path.GetFullPath(s)), Path.TrimEndingDirectorySeparator(request.Destination!), StringComparison.OrdinalIgnoreCase))) flags |= 8;
                operation.SetOperationFlags(testScopes is null ? flags : 0x00100614 | (flags & 8) | (request.Kind==FileOperationKind.Delete&&request.Recycle?0x00084000u:0)); // Tests preserve recycle semantics and nuke warning.
                if(testRecycleReceipt is not null && request.Kind==FileOperationKind.Delete && request.Recycle){receipt=new(testRecycleReceipt);receiptPointer=Marshal.GetComInterfaceForObject<RecycleReceiptSink,IRecycleReceiptSink>(receipt);}
                IntPtr Item(string path) { var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"); Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item)); items.Add(item); return item; }
                var destination = request.Destination is null ? IntPtr.Zero : Item(request.Destination);
                foreach (var path in request.Sources)
                {
                    var source = Item(path);
                    switch (request.Kind) { case FileOperationKind.Copy: operation.CopyItem(source, destination, request.NewName, IntPtr.Zero); break; case FileOperationKind.Move: operation.MoveItem(source, destination, request.NewName, IntPtr.Zero); break; case FileOperationKind.Delete: operation.DeleteItem(source, receiptPointer); break; }
                }
                token.ThrowIfCancellationRequested();
                var result=CompleteShellExecution(operation.PerformOperations,()=>{operation.GetAnyOperationsAborted(out var aborted);return aborted;});
                if(receipt?.Failure is { } failure)throw failure;
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { foreach (var item in items) Marshal.Release(item); if (operation is not null) Marshal.FinalReleaseComObject(operation);if(receiptPointer!=IntPtr.Zero)Marshal.Release(receiptPointer);GC.KeepAlive(receipt); }
        })
        { IsBackground = true, Name = "PaneCommander Shell operation" }; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    public static bool RequiresInternalDrag(IEnumerable<string> paths) => paths.Any(path => LinkObjectOperations.ContainsLink(path));
    private static void CreateDirectoryTree(string destination, string relativePath, CancellationToken token)
    {
        var parts = DirectoryCreation.ParseRelativePath(relativePath);
        var root = FileSystemService.ResolveTarget(destination, token);
        // Validate existing components before creating anything. Do not follow a link
        // encountered inside the newly requested relative path.
        static void CheckDirectory(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("作成する階層の途中にリンクがあります: " + path);
            if ((attributes & FileAttributes.Directory) == 0) throw new IOException("同名のファイルがあるためフォルダーを作成できません: " + path);
        }
        var current = root;
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested(); current = Path.Combine(current, part);
            try { CheckDirectory(current); }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
        }
        current = root;
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested();
            CheckDirectory(current);
            current = Path.Combine(current, part);
            Directory.CreateDirectory(current);
            CheckDirectory(current);
        }
    }
    internal static FileOperationResult CompleteShellExecution(Action perform,Func<bool> getAborted)
    {
        COMException? failure=null;
        try { perform(); }
        catch(COMException ex) { failure=ex; }
        // Query even after PerformOperations fails, as required by IFileOperation.
        var aborted=getAborted();
        if(failure is not null && !(aborted && failure.HResult==unchecked((int)0x80270000)))throw failure;
        return new(aborted);
    }
    private static bool Exists(string path) { try { _ = File.GetAttributes(path); return true; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IntPtr item);
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie); void Unadvise(uint cookie); void SetOperationFlags(uint flags); void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message); void SetProgressDialog(IntPtr dialog); void SetProperties(IntPtr properties); void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IntPtr item); void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink); void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink); void MoveItems(IntPtr items, IntPtr destination);
        void CopyItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink); void CopyItems(IntPtr items, IntPtr destination);
        void DeleteItem(IntPtr item, IntPtr sink); void DeleteItems(IntPtr items);
        void NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IntPtr sink);
        void PerformOperations(); void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}

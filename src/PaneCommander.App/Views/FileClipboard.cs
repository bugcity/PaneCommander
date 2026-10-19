using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.App.Views;

public enum FileClipboardOperation { Copy, Cut, Paste }

/// <summary>Uses the same Shell transfer and link policy as the context menu.</summary>
public static class FileClipboard
{
    public static Task ExecuteAsync(FileClipboardOperation operation, string directory, string[] paths, IntPtr owner,
        Func<FileOperationRequest, IntPtr, Task<FileOperationResult>> execute)
        => ExecuteAsync(operation, directory, paths, owner, execute, new WindowsFileClipboard());

    internal static async Task ExecuteAsync(FileClipboardOperation operation, string directory, string[] paths, IntPtr owner,
        Func<FileOperationRequest, IntPtr, Task<FileOperationResult>> execute, IFileClipboardBackend clipboard)
    {
        if (operation is FileClipboardOperation.Copy or FileClipboardOperation.Cut)
        {
            if (paths.Length == 0) return;
            var verb = operation == FileClipboardOperation.Copy ? "copy" : "cut";
            await Task.Run(() => ShellMenuSafety.CheckInvocation(verb, directory, paths, null));
            clipboard.SetFiles(directory, paths, owner, verb);
            return;
        }
        var sequence = clipboard.Sequence;
        var sources = clipboard.ReadPaths();
        if (sources.Length == 0) return; // Text and images are not file transfers.
        await Task.Run(() => ShellMenuSafety.CheckInvocation("paste", directory, [], sources));
        if (sequence != clipboard.Sequence) throw new IOException("検査中にクリップボードが変更されました。貼り付けをやり直してください。");
        using var transfer = clipboard.OpenTransfer();
        if (sequence != clipboard.Sequence) throw new IOException("貼り付け開始前にクリップボードが変更されました。やり直してください。");
        var result = await execute(new(transfer.Kind, sources, directory), owner);
        if (!result.Aborted)
        {
            try { transfer.ReportCompleted(); }
            catch (ExternalException ex) { throw new IOException("ファイル操作は終了しましたが、コピー元へのShell転送完了通知に失敗しました。", ex); }
        }
    }
}

internal interface IFileClipboardBackend
{
    uint Sequence { get; }
    string[] ReadPaths();
    void SetFiles(string directory, string[] paths, IntPtr owner, string verb);
    IFileClipboardTransfer OpenTransfer();
}

internal interface IFileClipboardTransfer : IDisposable
{
    FileOperationKind Kind { get; }
    void ReportCompleted();
}

internal sealed class WindowsFileClipboard : IFileClipboardBackend
{
    public uint Sequence => GetClipboardSequenceNumber();
    public string[] ReadPaths() => Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray() : [];
    public void SetFiles(string directory, string[] paths, IntPtr owner, string verb)
    {
        using var menu = new ShellContextMenu(directory, paths, owner);
        var command = menu.Commands().FirstOrDefault(c => c.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase));
        if (command.Id == 0 || !menu.IsEnabled(command.Id)) throw new IOException("この選択項目ではコピー・切り取りを実行できません。");
        menu.Invoke(command.Id, 0, 0);
    }
    public IFileClipboardTransfer OpenTransfer() => new Transfer();
    private sealed class Transfer : IFileClipboardTransfer
    {
        private readonly ShellPasteData data = new();
        public FileOperationKind Kind => data.Kind;
        public void ReportCompleted() => data.ReportCompleted();
        public void Dispose() => data.Dispose();
    }
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}

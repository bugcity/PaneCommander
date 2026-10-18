using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

/// <summary>Invoke the same canonical edit command offered by the native context menu.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ShellEditService : IShellEditService
{
    public Task EditAsync(FileEntry entry, IntPtr owner)
    {
        if (entry.IsDirectory || entry.IsReparsePoint)
            throw new NotSupportedException("通常ファイルを1件選択してください。リンクの編集は安全性未検証です。");
        var path = FileSystemService.ValidatePath(entry.FullPath);
        if (!File.Exists(path)) throw new FileNotFoundException("ファイルが見つかりません。", path);
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new NotSupportedException("通常ファイルを1件選択してください。");
        var directory = Path.GetDirectoryName(path)!;
        ShellMenuSafety.CheckInvocation("edit", directory, [path], null);
        using var menu = new ShellContextMenu(directory, [path], owner);
        var command = menu.Commands().FirstOrDefault(c => string.Equals(c.Verb, "edit", StringComparison.OrdinalIgnoreCase) && menu.IsEnabled(c.Id));
        if (command.Id == 0) throw new NotSupportedException("このファイルのWindows Shellに「編集（Edit）」が登録されていないか、現在利用できません。");
        menu.Invoke(command.Id, 0, 0, waitForCompletion: true);
        return Task.CompletedTask;
    }
}

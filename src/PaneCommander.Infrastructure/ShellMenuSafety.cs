using System.Runtime.Versioning;

namespace PaneCommander.Infrastructure;

[SupportedOSPlatform("windows")]
public static class ShellMenuSafety
{
    public static bool AllowedForDirectLink(string verb) => verb.ToLowerInvariant() is not ("copy" or "cut" or "paste" or "pastelink");
    public static void CheckInvocation(string verb, string directory, string[] paths, string[]? clipboardPaths)
    {
        verb = verb.ToLowerInvariant();
        // Only clipboard transfers use our link-specific restrictions. A user-selected
        // extension (including commands without canonical verbs) is invoked by Shell.
        if (verb is not ("copy" or "cut" or "paste" or "pastelink")) return;
        if (verb is "paste" or "pastelink")
        {
            if (clipboardPaths is not { Length: > 0 }) throw new IOException("安全性を確認できないクリップボード形式です。この貼り付け操作は無効です。");
            foreach (var path in clipboardPaths)
            {
                if (!Path.IsPathFullyQualified(path)) throw new IOException("ドライブの絶対パスまたはUNC共有パスが必要です。");
                FileSystemService.ValidatePath(Path.GetDirectoryName(path) ?? path);
                if (LinkObjectOperations.ContainsLink(path, default)) throw new IOException("リンクを含む項目のShell貼り付けは安全性未検証です。PaneCommanderのF5/F6を使用してください。");
            }
        }
        foreach (var path in paths.Length == 0 ? new[] { directory } : paths)
            if (LinkObjectOperations.ContainsLink(path, default)) throw new IOException("リンクを含む対象のShellコピー・切り取りは安全性未検証のため実行できません。F5/F6を使用してください。");
    }
}

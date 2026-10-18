namespace PaneCommander.Core;

public static class DirectoryCreation
{
    public static string[] ParseRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new ArgumentException("作成するフォルダーは現在のフォルダーからの相対パスで指定してください。");
        var parts = path.Replace('/', '\\').TrimEnd('\\').Split('\\');
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            var reserved = stem is "CON" or "PRN" or "AUX" or "NUL"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9';
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || reserved)
                throw new ArgumentException("フォルダー名が不正です。空の階層、.、..、使用できない文字や予約名は指定できません。");
        }
        return parts;
    }
}

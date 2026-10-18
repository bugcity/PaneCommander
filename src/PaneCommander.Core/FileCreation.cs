namespace PaneCommander.Core;

public static class FileCreation
{
    public static void ValidateName(string? name)
    {
        var parts = DirectoryCreation.ParseRelativePath(name);
        if (parts.Length != 1 || parts[0] != name)
            throw new ArgumentException("現在のフォルダーに作成するファイル名を指定してください。階層やパスは指定できません。");
    }
}

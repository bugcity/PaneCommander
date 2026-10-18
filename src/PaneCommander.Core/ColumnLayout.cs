namespace PaneCommander.Core;

public sealed record ColumnSpec(string Key, bool Visible = true, double Width = 100);
public sealed record ColumnLayout
{
    public bool NameFillsRemaining { get; init; } = true;
    public ColumnSpec[] Columns { get; init; } = [new("Name", true, 180), new("Kind", true, 70), new("Size", true, 92), new("FileAge", true, 78), new("LastWriteTime", true, 160)];
    public void Validate()
    {
        string[] keys = ["Name", "Kind", "Size", "FileAge", "LastWriteTime"];
        if (Columns is null || Columns.Length != 5 || Columns.Any(c => c is null || !keys.Contains(c.Key) || !double.IsFinite(c.Width) || c.Width is < 24 or > 4000)
            || Columns.Select(c => c.Key).Distinct().Count() != 5 || !Columns.Any(c => c.Visible))
            throw new ArgumentException("列は名前・種類・サイズ・File Age・更新日時の各1個。最低1列を表示し、幅は24〜4000 DIPで指定してください。");
    }
}

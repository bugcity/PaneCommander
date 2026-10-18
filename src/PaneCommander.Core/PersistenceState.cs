namespace PaneCommander.Core;

public sealed record FavoriteFolder(Guid Id, string Name, string Path);
public sealed record FavoritesDocument
{
    public int Version { get; init; } = 1;
    public FavoriteFolder[] Items { get; init; } = [];
    public void Validate()
    {
        if (Version != 1 || Items is null || Items.Length > 256) throw new InvalidDataException("お気に入りの形式または件数が不正です（最大256件）。");
        if (Items.Any(f => f is null || f.Id == Guid.Empty || !ValidName(f.Name) || !ValidPath(f.Path)) || Items.Select(f => f.Id).Distinct().Count() != Items.Length)
            throw new InvalidDataException("お気に入りの名前・パス・IDが不正です。");
    }
    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 120;
    public static bool ValidPath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 32767 && Path.IsPathFullyQualified(path) && !path.Contains('\0');
}

public sealed record TabSnapshot(string Path, string SortProperty = "Name", bool SortDescending = false, bool ShowHiddenFiles = false, bool ShowDotFiles = false);
public sealed record PaneSnapshot
{
    public TabSnapshot[] Tabs { get; init; } = [];
    public int SelectedIndex { get; init; }
    public void Validate()
    {
        if (Tabs is null || Tabs.Length is < 1 or > 256 || SelectedIndex < 0 || SelectedIndex >= Tabs.Length)
            throw new InvalidDataException("タブ構成が不正です（各ペイン1〜256タブ）。");
        if (Tabs.Any(t => t is null || !FavoritesDocument.ValidPath(t.Path) || t.SortProperty is not ("Name" or "Kind" or "Size" or "LastWriteTime" or "FileAge")))
            throw new InvalidDataException("タブのパス・表示設定が不正です。");
    }
}
public sealed record WindowLayout
{
    public double Left { get; init; } = 60;
    public double Top { get; init; } = 60;
    public double Width { get; init; } = 1280;
    public double Height { get; init; } = 800;
    public bool Maximized { get; init; }
    public double LeftPaneFraction { get; init; } = .5;
    public double SidebarWidth { get; init; } = 180;
    public void Validate()
    {
        if (new[] { Left, Top, Width, Height, LeftPaneFraction, SidebarWidth }.Any(v => !double.IsFinite(v))
            || Math.Abs(Left) > 100000 || Math.Abs(Top) > 100000 || Width is < 850 or > 16000 || Height is < 480 or > 16000
            || LeftPaneFraction is < .05 or > .95 || SidebarWidth is < 120 or > 360)
            throw new InvalidDataException("ウィンドウ配置が不正です。");
    }
}
public sealed record SessionDocument
{
    public int Version { get; init; } = 1;
    public PaneSnapshot Left { get; init; } = new();
    public PaneSnapshot Right { get; init; } = new();
    public PaneSide ActiveSide { get; init; }
    public WindowLayout Layout { get; init; } = new();
    public void Validate()
    {
        if (Version != 1 || Left is null || Right is null || Layout is null || !Enum.IsDefined(ActiveSide)) throw new InvalidDataException("セッション形式が不正です。");
        Left.Validate(); Right.Validate(); Layout.Validate();
    }
}
public sealed record StateLoadResult<T>(T? Value, string? Error = null) where T : class;
public interface IFavoritesStore
{
    Task<StateLoadResult<FavoritesDocument>> LoadAsync(CancellationToken token = default);
    Task SaveAsync(FavoritesDocument value, CancellationToken token = default);
}
public interface ISessionStore
{
    Task<StateLoadResult<SessionDocument>> LoadAsync(CancellationToken token = default);
    Task SaveAsync(SessionDocument value, CancellationToken token = default);
}

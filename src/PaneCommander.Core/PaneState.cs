using System.Collections.ObjectModel;

namespace PaneCommander.Core;

public sealed class PaneState : ObservableObject, IDisposable
{
    public const int MaxInactiveEntriesPerTab = 2_000;
    public const int MaxInactiveEntriesPerPane = 4_000;
    private readonly ObservableCollection<TabState> tabs = [];
    private readonly IFileSystemService fileSystem;
    private readonly IErrorLogger logger;
    private TabState selectedTab;
    private bool isActive;
    public PaneState(PaneSide side, string home, IFileSystemService fileSystem, IErrorLogger logger)
    {
        Side = side;
        this.fileSystem = fileSystem;
        this.logger = logger;
        selectedTab = new TabState(home, fileSystem, logger);
        tabs.Add(selectedTab);
        Tabs = new ReadOnlyObservableCollection<TabState>(tabs);
    }
    public PaneSide Side { get; }
    public string Label => Side == PaneSide.Left ? "左ペイン" : "右ペイン";
    public ReadOnlyObservableCollection<TabState> Tabs { get; }
    public TabState SelectedTab
    {
        get => selectedTab;
        set
        {
            if (value is null || !tabs.Contains(value) || value == selectedTab) return;
            selectedTab.CancelLoading();
            Set(ref selectedTab, value);
            TrimInactiveSnapshots();
            // NavigateAsync内で例外を状態へ変換。UIスレッドを待たせず再取得する。
            _ = selectedTab.EnsureLoadedAsync();
        }
    }
    public bool IsActive { get => isActive; internal set => Set(ref isActive, value); }
    public void SelectAdjacentTab(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        SelectedTab = tabs[(tabs.IndexOf(selectedTab) + direction + tabs.Count) % tabs.Count];
    }
    public bool MoveTab(TabState tab, int targetIndex)
    {
        var sourceIndex = tabs.IndexOf(tab);
        if (sourceIndex < 0 || targetIndex < 0 || targetIndex >= tabs.Count || sourceIndex == targetIndex) return false;
        tabs.Move(sourceIndex, targetIndex);
        return true;
    }
    public bool MoveSelectedTab(int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        return MoveTab(selectedTab, tabs.IndexOf(selectedTab) + direction);
    }
    public TabState AddTab()
    {
        var tab = new TabState(SelectedTab.CurrentPath, fileSystem, logger) { ShowHiddenFiles = SelectedTab.ShowHiddenFiles, ShowDotFiles = SelectedTab.ShowDotFiles };
        tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }
    public bool CloseTab(TabState tab)
    {
        if (tabs.Count <= 1 || !tabs.Contains(tab)) return false;
        var index = tabs.IndexOf(tab);
        if (SelectedTab == tab) SelectedTab = tabs[index == tabs.Count - 1 ? index - 1 : index + 1];
        tabs.Remove(tab);
        tab.Dispose();
        return true;
    }
    public PaneSnapshot Capture() => new()
    {
        Tabs = tabs.Select(t => new TabSnapshot(t.CurrentPath, t.SortProperty, t.SortDescending, t.ShowHiddenFiles, t.ShowDotFiles)).ToArray(),
        SelectedIndex = tabs.IndexOf(selectedTab)
    };
    public void Restore(PaneSnapshot snapshot)
    {
        snapshot.Validate();
        foreach (var tab in tabs) tab.Dispose();
        tabs.Clear();
        foreach (var tab in snapshot.Tabs)
            tabs.Add(new TabState(tab.Path, fileSystem, logger) { SortProperty = tab.SortProperty, SortDescending = tab.SortDescending, ShowHiddenFiles = tab.ShowHiddenFiles, ShowDotFiles = tab.ShowDotFiles });
        selectedTab = tabs[snapshot.SelectedIndex];
        Raise(nameof(SelectedTab));
        // No hidden-tab enumeration or visual creation during restore.
    }
    public void Dispose() { foreach (var tab in tabs) tab.Dispose(); }

    private void TrimInactiveSnapshots()
    {
        var retained = 0;
        foreach (var tab in tabs.Where(t => t != selectedTab).Reverse())
        {
            tab.CancelLoading();
            if (tab.Entries.Count > MaxInactiveEntriesPerTab || retained + tab.Entries.Count > MaxInactiveEntriesPerPane)
                tab.ReleaseSnapshot();
            else retained += tab.Entries.Count;
        }
    }
}

namespace PaneCommander.Core;

public sealed class WorkspaceState : ObservableObject, IDisposable
{
    private PaneSide activeSide = PaneSide.Left;
    public WorkspaceState(string home, IFileSystemService fileSystem, IErrorLogger logger)
    {
        Left = new PaneState(PaneSide.Left, home, fileSystem, logger);
        Right = new PaneState(PaneSide.Right, home, fileSystem, logger);
        Left.IsActive = true;
    }
    public PaneState Left { get; }
    public PaneState Right { get; }
    public PaneSide ActiveSide => activeSide;
    public PaneState ActivePane => activeSide == PaneSide.Left ? Left : Right;
    public PaneState OppositePane => activeSide == PaneSide.Left ? Right : Left;
    public void Activate(PaneSide side)
    {
        if (side is not (PaneSide.Left or PaneSide.Right)) throw new ArgumentOutOfRangeException(nameof(side));
        if (activeSide == side) return;
        activeSide = side;
        Left.IsActive = side == PaneSide.Left;
        Right.IsActive = side == PaneSide.Right;
        Raise(nameof(ActiveSide));
        Raise(nameof(ActivePane));
        Raise(nameof(OppositePane));
    }
    public void Toggle() => Activate(activeSide == PaneSide.Left ? PaneSide.Right : PaneSide.Left);
    public bool MoveTabToPane(TabState tab, PaneSide targetSide, int? targetIndex = null)
    {
        if (targetSide is not (PaneSide.Left or PaneSide.Right)) throw new ArgumentOutOfRangeException(nameof(targetSide));
        var destination = targetSide == PaneSide.Left ? Left : Right;
        var source = targetSide == PaneSide.Left ? Right : Left;
        if (!source.TransferTabTo(tab, destination, targetIndex ?? destination.Tabs.Count)) return false;
        Activate(targetSide);
        return true;
    }
    public Task InitializeAsync() => Task.WhenAll(Left.SelectedTab.RefreshAsync(), Right.SelectedTab.RefreshAsync());
    public void Restore(SessionDocument session)
    {
        session.Validate(); Left.Restore(session.Left); Right.Restore(session.Right); Activate(session.ActiveSide);
    }
    public SessionDocument Capture(WindowLayout layout) => new() { Left = Left.Capture(), Right = Right.Capture(), ActiveSide = ActiveSide, Layout = layout };
    public void Dispose() { Left.Dispose(); Right.Dispose(); }
}

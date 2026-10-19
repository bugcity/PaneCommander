using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public partial class PaneView
{
    private const string TabMoveFormat = "PaneCommander.TabMove";
    private sealed record TabMovePayload(PaneState Owner, TabState Tab);
    private TabInsertionAdorner? insertionMarker;
    private AdornerLayer? insertionLayer;

    private void AttachTabReordering()
    {
        Point? start = null;
        TabState? dragged = null;
        Tabs.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragged = null; start = null;
            if (FileDragBehavior.Ancestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
            if (FileDragBehavior.Ancestor<TabItem>(e.OriginalSource as DependencyObject)?.DataContext is TabState tab)
            {
                dragged = tab; start = e.GetPosition(Tabs);
                _ = Mouse.Capture(Tabs, CaptureMode.SubTree);
            }
        };
        Tabs.PreviewMouseLeftButtonUp += (_, _) =>
        {
            dragged = null; start = null;
            if (Tabs.IsMouseCaptured) Tabs.ReleaseMouseCapture();
        };
        Tabs.LostMouseCapture += (_, _) => { if (!Tabs.IsMouseCaptureWithin) { dragged = null; start = null; } };
        Tabs.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } from || dragged is null || e.LeftButton != MouseButtonState.Pressed || DataContext is not PaneViewModel pane) return;
            var now = e.GetPosition(Tabs);
            if (Math.Abs(now.X - from.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var tab = dragged; start = null; dragged = null;
            if (Tabs.IsMouseCaptured) Tabs.ReleaseMouseCapture();
            var data = new DataObject();
            data.SetData(TabMoveFormat, new TabMovePayload(pane.State, tab));
            data.SetData(FileDragBehavior.FavoriteFormat, new FavoriteTabPayload(tab.Title, tab.CurrentPath));
            try { _ = DragDrop.DoDragDrop(Tabs, data, DragDropEffects.Move | DragDropEffects.Link); }
            finally { ClearInsertionMarker(); }
        };
        Tabs.AllowDrop = true;
        Tabs.PreviewDragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(TabMoveFormat, false)) return;
            e.Handled = true;
            var target = FindTabDrop(e);
            e.Effects = target is null ? DragDropEffects.None : DragDropEffects.Move;
            ClearInsertionMarker();
            if (target is { } drop && AdornerLayer.GetAdornerLayer(drop.Header) is { } layer)
            {
                insertionLayer = layer;
                insertionMarker = new TabInsertionAdorner(drop.Header, drop.After, (Brush)FindResource("AccentBrush")) { IsHitTestVisible = false };
                layer.Add(insertionMarker);
            }
        };
        Tabs.PreviewDragLeave += (_, _) => ClearInsertionMarker();
        Tabs.PreviewDrop += (_, e) =>
        {
            if (!e.Data.GetDataPresent(TabMoveFormat, false)) return;
            e.Handled = true; e.Effects = DragDropEffects.None;
            ClearInsertionMarker();
            if (FindTabDrop(e) is not { } drop || DataContext is not PaneViewModel pane) return;
            var payload = (TabMovePayload)e.Data.GetData(TabMoveFormat, false);
            var sourceIndex = pane.State.Tabs.IndexOf(payload.Tab);
            var gap = pane.State.Tabs.IndexOf((TabState)drop.Header.DataContext) + (drop.After ? 1 : 0);
            var targetIndex = gap > sourceIndex ? gap - 1 : gap;
            _ = pane.State.MoveTab(payload.Tab, targetIndex);
            e.Effects = DragDropEffects.Move;
            if (Window.GetWindow(this)?.DataContext is MainViewModel main) main.Workspace.Activate(pane.State.Side);
            FocusList();
        };
        Unloaded += (_, _) => ClearInsertionMarker();
    }

    private (TabItem Header, bool After)? FindTabDrop(DragEventArgs e)
    {
        if (!e.AllowedEffects.HasFlag(DragDropEffects.Move) || DataContext is not PaneViewModel pane
            || e.Data.GetData(TabMoveFormat, false) is not TabMovePayload payload || !ReferenceEquals(payload.Owner, pane.State)
            || !pane.State.Tabs.Contains(payload.Tab)) return null;
        var header = FileDragBehavior.Ancestor<TabItem>(e.OriginalSource as DependencyObject);
        if (header?.DataContext is not TabState target || !pane.State.Tabs.Contains(target)) return null;
        return (header, e.GetPosition(header).X >= header.ActualWidth / 2);
    }

    private void ClearInsertionMarker()
    {
        if (insertionMarker is not null) insertionLayer?.Remove(insertionMarker);
        insertionMarker = null; insertionLayer = null;
    }

    private sealed class TabInsertionAdorner(TabItem header, bool after, Brush brush) : Adorner(header)
    {
        protected override void OnRender(DrawingContext drawingContext)
        {
            var x = after ? AdornedElement.RenderSize.Width - 1 : 1;
            drawingContext.DrawLine(new Pen(brush, 2), new Point(x, 0), new Point(x, AdornedElement.RenderSize.Height));
        }
    }
}

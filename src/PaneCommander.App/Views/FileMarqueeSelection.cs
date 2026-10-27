using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PaneCommander.App.Views;

// A gesture that starts on empty space selects rows. Starting on content still
// belongs to ListView's Ctrl/Shift selection and FileDragBehavior's file transfer.
internal sealed class FileMarqueeSelection
{
    private static readonly DependencyProperty BehaviorProperty = DependencyProperty.RegisterAttached(
        "Behavior", typeof(FileMarqueeSelection), typeof(FileMarqueeSelection));
    internal static FileMarqueeSelection Attach(ListView list, Func<bool> enabled)
    {
        var behavior = new FileMarqueeSelection(list, enabled);
        list.SetValue(BehaviorProperty, behavior);
        return behavior;
    }
    internal static FileMarqueeSelection? Active(ListView list) =>
        list.GetValue(BehaviorProperty) is FileMarqueeSelection { start: not null } behavior ? behavior : null;
    internal static FileMarqueeSelection Get(ListView list) => (FileMarqueeSelection)list.GetValue(BehaviorProperty);

    private readonly ListView list;
    private readonly Func<bool> enabled;
    private Point? start;
    private double startSlot;
    private HashSet<object> baseline = [];
    private ModifierKeys modifiers;
    private bool dragging;
    private SelectionAdorner? marker;
    private AdornerLayer? layer;
    private Window? owner;

    private FileMarqueeSelection(ListView list, Func<bool> enabled)
    {
        this.list = list; this.enabled = enabled;
        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1 && Begin(e.GetPosition(list), Keyboard.Modifiers, e.OriginalSource as DependencyObject)) e.Handled = true;
        };
        list.PreviewMouseMove += (_, e) =>
        {
            if (start is null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { Finish(); return; }
            Update(e.GetPosition(list)); e.Handled = true;
        };
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (start is null) return;
            Update(e.GetPosition(list)); Finish(); e.Handled = true;
        };
        list.LostMouseCapture += (_, _) => { if (!list.IsMouseCaptured) Finish(); };
        ((INotifyCollectionChanged)list.Items).CollectionChanged += (_, _) => Finish();
        list.Loaded += (_, _) => { owner = Window.GetWindow(list); if (owner is not null) owner.Deactivated += Deactivated; };
        list.Unloaded += (_, _) => { Finish(); if (owner is not null) owner.Deactivated -= Deactivated; owner = null; };
    }
    private void Deactivated(object? sender, EventArgs e) => Finish();

    internal bool Begin(Point point, ModifierKeys keys, DependencyObject? source)
    {
        if (!enabled() || ShellMenuBehavior.IsOpen || (keys & ModifierKeys.Alt) != 0
            || FileDragBehavior.Ancestor<TextBoxBase>(source) is not null
            || FileDragBehavior.Ancestor<GridViewColumnHeader>(source) is not null
            || FileDragBehavior.Ancestor<ScrollBar>(source) is not null
            || FileDragBehavior.Ancestor<TextBlock>(source) is not null
            || FileDragBehavior.Ancestor<ShellFileIcon>(source) is not null
            || FileDragBehavior.Ancestor<Grid>(source)?.Name == "FileIconArea"
            || !Viewport().Contains(point)) return false;
        Finish();
        if (!list.CaptureMouse()) return false;
        baseline = list.SelectedItems.Cast<object>().ToHashSet(); modifiers = keys;
        start = point; startSlot = Slot(point.Y); dragging = false;
        list.Focus();
        if ((keys & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) list.SelectedItems.Clear();
        return true;
    }

    internal void Update(Point point)
    {
        if (start is not { } from) return;
        if (!enabled()) { Finish(); return; }
        if (!dragging && Math.Abs(point.X - from.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - from.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragging = true;
        var viewport = Viewport();
        var scroll = Children<ScrollViewer>(list).FirstOrDefault();
        if (point.Y < viewport.Top) scroll?.LineUp();
        else if (point.Y > viewport.Bottom) scroll?.LineDown();
        list.UpdateLayout();
        point = new(Math.Clamp(point.X, viewport.Left, viewport.Right), Math.Clamp(point.Y, viewport.Top, viewport.Bottom));
        var currentSlot = Slot(point.Y);
        var first = Math.Clamp((int)Math.Floor(Math.Min(startSlot, currentSlot)), 0, list.Items.Count);
        var end = Math.Clamp((int)Math.Ceiling(Math.Max(startSlot, currentSlot)), 0, list.Items.Count);
        var desired = (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0 ? new HashSet<object>(baseline) : [];
        for (var index = first; index < end; index++)
        {
            var item = list.Items[index];
            if ((modifiers & ModifierKeys.Control) != 0 && baseline.Contains(item)) desired.Remove(item);
            else desired.Add(item);
        }
        // Keep unchanged selections in place so command bindings and focus do not churn.
        foreach (var item in list.SelectedItems.Cast<object>().Where(item => !desired.Contains(item)).ToArray()) list.SelectedItems.Remove(item);
        foreach (var item in desired.Where(item => !list.SelectedItems.Contains(item))) list.SelectedItems.Add(item);
        if (marker is null && AdornerLayer.GetAdornerLayer(list) is { } found)
        {
            layer = found; marker = new SelectionAdorner(list); layer.Add(marker);
        }
        if (marker is not null)
        {
            var origin = Geometry();
            var y = origin.Top + (startSlot - origin.Index) * origin.Height;
            marker.Bounds = Rect.Intersect(viewport, new Rect(new Point(from.X, y), point));
            marker.InvalidateVisual();
        }
    }

    internal void Finish(bool cancel = false)
    {
        if (start is null) return;
        start = null;
        if (marker is not null) layer?.Remove(marker);
        marker = null; layer = null;
        if (list.IsMouseCaptured) list.ReleaseMouseCapture();
        if (cancel)
        {
            list.SelectedItems.Clear();
            foreach (var item in baseline.Where(list.Items.Contains)) list.SelectedItems.Add(item);
        }
        baseline.Clear();
        if (list.IsLoaded && list.IsVisible && Window.GetWindow(list)?.IsActive == true)
        {
            if (list.SelectedItem is { } selected && list.ItemContainerGenerator.ContainerFromItem(selected) is ListViewItem row) Keyboard.Focus(row);
            else Keyboard.Focus(list);
        }
    }

    private Rect Viewport()
    {
        // GridView also has a separate presenter for the column headers.
        var presenter = Children<ScrollContentPresenter>(list).FirstOrDefault(p => Children<ItemsPresenter>(p).Any());
        return presenter is null ? new Rect(list.RenderSize) : new Rect(presenter.TranslatePoint(new Point(), list), presenter.RenderSize);
    }
    // GridView rows have a uniform height. Logical row coordinates preserve the
    // selection anchor while virtualized containers are recycled during scrolling.
    private (double Top, double Height, int Index) Geometry()
    {
        var row = Children<ListViewItem>(list).FirstOrDefault();
        if (row is null) return (Viewport().Top, 1, 0);
        return (row.TranslatePoint(new Point(), list).Y - row.Margin.Top,
            Math.Max(1, row.ActualHeight + row.Margin.Top + row.Margin.Bottom), list.ItemContainerGenerator.IndexFromContainer(row));
    }
    private double Slot(double y) { var geometry = Geometry(); return (y - geometry.Top) / geometry.Height + geometry.Index; }
    private static IEnumerable<T> Children<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T match) yield return match;
            else foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    private sealed class SelectionAdorner(ListView list) : Adorner(list)
    {
        public Rect Bounds { get; set; }
        protected override void OnRender(DrawingContext context)
        {
            if (Bounds.IsEmpty) return;
            var accent = (Brush)list.FindResource("AccentBrush");
            var fill = accent.Clone(); fill.Opacity = .15;
            context.DrawRectangle(fill, new Pen(accent, 1), Bounds);
        }
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
    }
}

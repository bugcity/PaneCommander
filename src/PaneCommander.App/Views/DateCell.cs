using System.Windows;
using System.Windows.Controls;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public sealed class DateCell : TextBlock
{
    private MainWindow? owner;
    public static readonly DependencyProperty EntryProperty = DependencyProperty.Register(nameof(Entry), typeof(FileEntry), typeof(DateCell), new PropertyMetadata(null, Changed));
    public FileEntry? Entry { get => (FileEntry?)GetValue(EntryProperty); set => SetValue(EntryProperty, value); }
    private string? lastColor;
    private string? lastForeground;
    public DateCell()
    {
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        TextTrimming = TextTrimming.CharacterEllipsis;
        TextAlignment = TextAlignment.Right;
        VerticalAlignment = VerticalAlignment.Center;
        Padding = new Thickness(4, 0, 4, 0);
        Margin = new Thickness(0, 1, 4, 1);
        Loaded += (_, _) => { owner = Window.GetWindow(this) as MainWindow; owner?.DateCells.Add(this); Refresh();  };
        Unloaded += (_, _) => { owner?.DateCells.Remove(this);  owner = null; };
    }
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args) => ((DateCell)value).Refresh();
    public void Refresh()
    {
        if (Window.GetWindow(this)?.DataContext is not MainViewModel model) return;
        var presentation = FileAge.Present(Entry?.LastWriteTime, DateTime.Now, model.Settings);
        Text = presentation.Text;
        var colored = model.Settings.ColorFileAge && Entry?.LastWriteTime is not null;
        var color = colored ? presentation.Color : null;
        if (lastColor != color)
        {
            Background = color is null ? System.Windows.Media.Brushes.Transparent : ThemeManager.Brush(color);
            lastColor = color;
        }
        var foreground = colored ? ThemeManager.ContrastTextColor(presentation.Color) : model.Settings.Foreground;
        if (lastForeground != foreground) { Foreground = ThemeManager.Brush(foreground); lastForeground = foreground; }
        ToolTip = Entry?.ModifiedText;
        var parent=System.Windows.Media.VisualTreeHelper.GetParent(this);
        while(parent is not null && parent is not FolderView) parent=System.Windows.Media.VisualTreeHelper.GetParent(parent);
        var shown=parent is FolderView folder && Entry is not null && folder.DataContext is TabState ? folder.ShowFileAge : model.Settings.ShowFileAge;
        Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }
}

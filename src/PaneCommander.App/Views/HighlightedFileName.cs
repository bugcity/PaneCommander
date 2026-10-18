using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public sealed class HighlightedFileName : TextBlock
{
    public static readonly DependencyProperty FileNameProperty = DependencyProperty.Register(nameof(FileName), typeof(string), typeof(HighlightedFileName), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.Register(nameof(Query), typeof(string), typeof(HighlightedFileName), new PropertyMetadata("", Changed));
    public string FileName { get => (string)GetValue(FileNameProperty); set => SetValue(FileNameProperty,value); }
    public string Query { get => (string)GetValue(QueryProperty); set => SetValue(QueryProperty,value); }
    public HighlightedFileName()
    {
        TextTrimming = TextTrimming.CharacterEllipsis;
        Unloaded += (_,_) => Clear(); Loaded += (_,_) => { if (IsVisible) Render(); };
        IsVisibleChanged += (_,_) => { if (IsVisible && IsLoaded) Render(); else Clear(); };
    }
    private void Clear() { Inlines.Clear(); }
    private static void Changed(DependencyObject source, DependencyPropertyChangedEventArgs args) { var control=(HighlightedFileName)source; if (control.IsLoaded && control.IsVisible) control.Render(); }
    private void Render()
    {
        var name = FileName ?? ""; var query = Query ?? "";
        Inlines.Clear();
        var ranges = FileNameFilter.Ranges(name,query);
        if (ranges.Count == 0) { Text = name; return; }
        var offset = 0;
        foreach (var range in ranges)
        {
            if (range.Start > offset) Inlines.Add(new Run(name[offset..range.Start]));
            var run = new Run(name.Substring(range.Start,range.Length)) { FontWeight = FontWeights.Bold };
            run.SetResourceReference(TextElement.ForegroundProperty,"FilterMatchBrush"); Inlines.Add(run); offset = range.Start + range.Length;
        }
        if (offset < name.Length) Inlines.Add(new Run(name[offset..]));
    }
}

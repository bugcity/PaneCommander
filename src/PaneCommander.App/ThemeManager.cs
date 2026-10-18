using System.Windows;
using System.Windows.Media;
using PaneCommander.Core;

namespace PaneCommander.App;

public static class ThemeManager
{
    public static string ContrastTextColor(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        static double Linear(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        var luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        return luminance > .179 ? "#000000" : "#FFFFFF";
    }
    public static System.Windows.Controls.BooleanToVisibilityConverter BooleanVisibility { get; } = new();
    public static Brush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze(); return brush;
    }
    public static void Apply(AppearanceSettings settings)
    {
        var resources = Application.Current.Resources;
        foreach (var pair in new Dictionary<string, string>
        {
            ["CanvasBrush"] = settings.Background, ["SurfaceBrush"] = settings.ActiveBackground, ["TextBrush"] = settings.Foreground,
            ["MutedBrush"] = settings.MutedForeground, ["AccentBrush"] = settings.ActiveBorder,
            ["FilterMatchBrush"] = settings.FilterMatchForeground,
            ["ActivePaneBrush"] = settings.ActiveBackground, ["InactivePaneBrush"] = settings.InactiveBackground,
            ["ActiveHeaderBrush"] = settings.ActiveHeader, ["InactiveHeaderBrush"] = settings.InactiveHeader,
            ["InactiveBorderBrush"] = settings.InactiveBorder, ["ActiveSelectionBrush"] = settings.ActiveSelection,
            ["InactiveSelectionBrush"] = settings.InactiveSelection,
            ["ActiveTabBrush"] = settings.ActiveTabBackground,["ActiveTabBorderBrush"] = settings.ActiveTabBorder,
            ["InactiveTabBrush"] = settings.InactiveTabBackground,["InactiveTabBorderBrush"] = settings.InactiveTabBorder,["TabBrush"] = settings.TabBackground,
            ["ScrollTrackBrush"] = settings.ScrollTrack,["ScrollThumbBrush"] = settings.ScrollThumb,["ScrollHoverBrush"] = settings.ScrollHover
        }) SetBrush(pair.Key,pair.Value);
        SetBrush("ActiveTabTextBrush",ContrastTextColor(settings.ActiveTabBackground));SetBrush("InactiveTabTextBrush",ContrastTextColor(settings.InactiveTabBackground));
        SetBrush(SystemColors.ControlBrushKey,settings.ActiveBackground);SetBrush(SystemColors.WindowBrushKey,settings.Background);SetBrush(SystemColors.WindowTextBrushKey,settings.Foreground);SetBrush(SystemColors.ControlTextBrushKey,settings.Foreground);SetBrush(SystemColors.HighlightBrushKey,settings.ActiveSelection);SetBrush(SystemColors.HighlightTextBrushKey,ContrastTextColor(settings.ActiveSelection));
        var font=ResolveFont(settings.FontFamily);if(resources["AppFont"] is not FontFamily current||current.Source!=font.Source)resources["AppFont"]=font;
        SetValue("AppFontSize",settings.FontSize);SetValue("RowHeight",settings.RowHeight);SetValue("RowMargin",new Thickness(0,settings.RowSpacing/2,0,settings.RowSpacing/2));SetValue("PaneBorderWidth",new Thickness(settings.BorderWidth));
        foreach (Window window in Application.Current.Windows) NativeTitleBar.Apply(window,settings);
        void SetBrush(object key,string hex){var color=(Color)ColorConverter.ConvertFromString(hex);if(resources[key] is not SolidColorBrush currentBrush||currentBrush.Color!=color)resources[key]=Brush(hex);}
        void SetValue(string key,object value){if(!Equals(resources[key],value))resources[key]=value;}
    }
    public static FontFamily ResolveFont(string name) => name.IndexOfAny([':', '/', '\\'])>=0 ? SystemFonts.MessageFontFamily : new(name + ", " + SystemFonts.MessageFontFamily.Source);
}

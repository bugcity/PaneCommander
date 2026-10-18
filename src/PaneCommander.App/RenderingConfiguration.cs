using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PaneCommander.Core;

namespace PaneCommander.App;

public static class RenderingConfiguration
{
    public static RenderMode ApplyBeforeWindowCreation(RenderingPreference preference)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        if (Application.Current?.Windows.Count > 0) throw new InvalidOperationException("描画方式はウィンドウ生成前に指定してください。");
        var mode = preference == RenderingPreference.SoftwareOnly ? RenderMode.SoftwareOnly : RenderMode.Default;
        RenderOptions.ProcessRenderMode = mode;
        return mode;
    }
}

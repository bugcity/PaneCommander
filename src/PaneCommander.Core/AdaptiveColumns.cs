namespace PaneCommander.Core;

public static class AdaptiveColumns
{
    // Preserve saved visibility/order/widths. Only the computed presentation is reduced.
    public static Dictionary<string,double> Calculate(ColumnLayout layout,double available,Func<string,double> minimum)
    {
        available=Math.Max(0,available);
        var widths=layout.Columns.ToDictionary(c=>c.Key,c=>c.Visible?Math.Max(c.Width,minimum(c.Key)):0);
        var name=layout.Columns.Single(c=>c.Key=="Name");
        var pinned=name.Visible?"Name":new[]{"Kind","FileAge","Size","LastWriteTime"}.First(k=>widths[k]>0);
        // A manually sized name column takes priority over optional metadata columns.
        // Only shrink it after all lower-priority columns have been omitted.
        var reserve=name.Visible&&!layout.NameFillsRemaining
            ? widths["Name"]
            : Math.Min(widths[pinned],Math.Max(minimum(pinned),pinned=="Name"?120:0));
        widths[pinned]=reserve;
        foreach(var key in new[]{"LastWriteTime","Size","FileAge","Kind"})
        {
            if(widths.Values.Sum()<=available)break;
            if(key!=pinned)widths[key]=0;
        }
        var remaining=Math.Max(0,available-widths.Where(p=>p.Key!=pinned).Sum(p=>p.Value));
        widths[pinned]=name.Visible && !layout.NameFillsRemaining?Math.Min(Math.Max(name.Width,minimum("Name")),remaining):remaining;
        return widths;
    }
}

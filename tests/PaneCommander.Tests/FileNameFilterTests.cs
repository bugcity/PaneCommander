using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;
[TestClass]
public sealed class FileNameFilterTests
{
    [TestMethod]
    public void PartialLiteralInsensitiveMatchesPreserveAllRanges()
    {
        foreach(var (name,query,start,length) in new[]{("report.xlsx","report",0,6),("monthly_report.pdf","REPORT",8,6),("2026_REPORT","report",5,6),("report","port",2,4),("note[1].txt","[1]",4,3),("banana","ana",1,5)})
        { Assert.IsTrue(FileNameFilter.Matches(name,query)); CollectionAssert.AreEqual(new[]{new NameMatch(start,length)},FileNameFilter.Ranges(name,query).ToArray()); }
        CollectionAssert.AreEqual(new[]{new NameMatch(0,6),new NameMatch(11,6)},FileNameFilter.Ranges("report_old_REPORT.txt","report").ToArray());
        Assert.IsFalse(FileNameFilter.Matches("report.txt","^report")); Assert.IsFalse(FileNameFilter.Matches("report.txt","pdf"));
        Assert.IsTrue(FileNameFilter.Matches("report.txt","")); Assert.AreEqual(0,FileNameFilter.Ranges("report.txt","").Count);
        Assert.IsFalse(FileNameFilter.Matches("report.txt"," "));
    }
    [TestMethod]
    public async Task LegacyPalettesGainMatchColorAndRoundTripCustomColor()
    {
        var root=Path.Combine(Path.GetTempPath(),"PaneCommander-filter-settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var colors=ColorSchemes.Capture(AppearanceSettings.Light(),"旧ライト").Colors.Where(p=>p.Key!="FilterMatchForeground").ToDictionary();
            var settings=AppearanceSettings.Light() with {CustomSchemes=[new("旧ライト",colors)]};
            var json=System.Text.Json.JsonSerializer.Serialize(settings);using(var document=System.Text.Json.JsonDocument.Parse(json))
            {var old=document.RootElement.EnumerateObject().Where(p=>p.Name!="FilterMatchForeground").ToDictionary(p=>p.Name,p=>p.Value.Clone());json=System.Text.Json.JsonSerializer.Serialize(old);}
            var path=Path.Combine(root,"settings.json");File.WriteAllText(path,json);var store=new JsonSettingsStore(path);var loaded=await store.LoadAsync();Assert.IsNull(loaded.Error);
            Assert.AreEqual(AppearanceSettings.Light().FilterMatchForeground,loaded.Settings.FilterMatchForeground);
            Assert.IsTrue(loaded.Settings.CustomSchemes![0].Colors.ContainsKey("FilterMatchForeground"));
            await store.SaveAsync(loaded.Settings with {FilterMatchForeground="#ABCDEF"});Assert.AreEqual("#ABCDEF",(await store.LoadAsync()).Settings.FilterMatchForeground);
            await File.WriteAllTextAsync(path,"{\"CustomSchemes\":[null]}");Assert.IsNotNull((await store.LoadAsync()).Error,"不正な配色要素は起動を壊さず設定エラーにする");
        }
        finally {if(!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath())+"PaneCommander-filter-settings-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();Directory.Delete(root,true);}
    }
}

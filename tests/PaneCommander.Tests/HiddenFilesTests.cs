using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.Core;
using PaneCommander.Infrastructure;

namespace PaneCommander.Tests;

[TestClass]
public sealed class HiddenFilesTests
{
    [TestMethod]
    public async Task EnumerationKeepsHiddenFileAndDirectoryAttributesAndDoesNotTreatDotNamesAsHidden()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaneCommander-hidden-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "hidden.txt");
            File.WriteAllText(file, "fixture"); File.SetAttributes(file, FileAttributes.Hidden);
            var folder = Directory.CreateDirectory(Path.Combine(root, "hidden-folder"));
            folder.Attributes |= FileAttributes.Hidden;
            File.WriteAllText(Path.Combine(root, ".visible"), "fixture");
            var entries = (await new FileSystemService().ReadFolderAsync(root, default)).Entries;
            Assert.AreEqual(3, entries.Count);
            Assert.IsTrue(entries.Single(e => e.Name == "hidden.txt").IsHidden);
            Assert.IsTrue(entries.Single(e => e.IsDirectory).IsHidden);
            Assert.IsFalse(entries.Single(e => e.Name == ".visible").IsHidden);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void HiddenPreferenceRoundTripsPerTabAndLegacySessionsDefaultToHiddenOff()
    {
        using var pane = new PaneState(PaneSide.Left, @"C:\fixture", new FakeFileSystem(), new FakeLogger());
        Assert.IsFalse(pane.SelectedTab.ShowHiddenFiles);
        Assert.IsFalse(pane.SelectedTab.ShowDotFiles);
        var first = pane.SelectedTab; first.ShowHiddenFiles = true; first.ShowDotFiles = false;
        var second = pane.AddTab(); Assert.IsTrue(second.ShowHiddenFiles);
        Assert.IsFalse(second.ShowDotFiles);
        second.ShowHiddenFiles = false;
        second.ShowDotFiles = true;
        var saved = JsonSerializer.Deserialize<PaneSnapshot>(JsonSerializer.Serialize(pane.Capture()))!;
        pane.Restore(saved);
        Assert.IsTrue(pane.Tabs[0].ShowHiddenFiles);
        Assert.IsFalse(pane.Tabs[1].ShowHiddenFiles);
        Assert.IsFalse(pane.Tabs[0].ShowDotFiles); Assert.IsTrue(pane.Tabs[1].ShowDotFiles);
        var legacy = JsonSerializer.Deserialize<PaneSnapshot>("{\"Tabs\":[{\"Path\":\"C:\\\\fixture\"}],\"SelectedIndex\":0}")!;
        pane.Restore(legacy); Assert.IsFalse(pane.SelectedTab.ShowHiddenFiles);
        Assert.IsFalse(pane.SelectedTab.ShowDotFiles);
    }

    [TestMethod]
    public void DotFilesExcludeItemsWithHiddenAttribute()
    {
        Assert.IsTrue(new FileEntry(@"C:\fixture\.config", ".config", false, 1, null).IsDotFile);
        Assert.IsTrue(new FileEntry(@"C:\fixture\.folder", ".folder", true, null, null).IsDotFile);
        Assert.IsFalse(new FileEntry(@"C:\fixture\.secret", ".secret", false, 1, null, IsHidden: true).IsDotFile);
        Assert.IsFalse(new FileEntry(@"C:\fixture\normal", "normal", false, 1, null).IsDotFile);
    }
}

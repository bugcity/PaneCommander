using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyTabReordering()
    {
        var files = new FakeFileSystem();
        using var model = new MainViewModel(files, new FakeLogger());
        model.Workspace.Left.Restore(new PaneSnapshot
        {
            Tabs = [new(@"C:\tabs\first"), new(@"C:\tabs\second"), new(@"C:\tabs\third")], SelectedIndex = 1
        });
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            var pane = model.Workspace.Left;
            var left = (PaneView)window.FindName("LeftPane");
            var tabs = (TabControl)left.FindName("Tabs");
            var selected = pane.SelectedTab;
            var entry = selected.Entries[0]; selected.SelectedEntry = entry; Pump();
            var reads = files.ReadCount;
            Assert.IsTrue(pane.MoveSelectedTab(-1)); Pump();
            Assert.AreSame(selected, tabs.SelectedItem);
            Assert.AreSame(selected, pane.Tabs[0]);
            Assert.AreSame(entry, left.CurrentFileList!.SelectedItem);
            Assert.IsTrue(pane.MoveSelectedTab(1)); Pump();
            Assert.AreSame(selected, tabs.SelectedItem);
            Assert.AreSame(selected, pane.Tabs[1]);
            Assert.AreSame(entry, left.CurrentFileList!.SelectedItem);
            Assert.AreEqual(reads, files.ReadCount, "並べ替えで再列挙しない");
            Assert.AreEqual(1, model.Workspace.Right.Tabs.Count);
            CollectionAssert.AreEqual(pane.Tabs.ToArray(), tabs.Items.Cast<TabState>().ToArray());
            Assert.IsTrue(tabs.AllowDrop);
        }
        finally { window.Close(); Pump(); }
    }
}

using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.Tests;

public sealed partial class WpfIntegrationTests
{
    private static void VerifyShellActionInputs()
    {
        var files = new ShellActionTests.ActionFiles(); var edit = new ShellActionTests.RecordingEdit();
        using var model = new MainViewModel(files, new FakeLogger(), shellEdit: edit);
        var window = new MainWindow { DataContext=model, ShowActivated=false, ShowInTaskbar=false, Left=-20000, Top=-20000 };
        try
        {
            window.Show(); Pump(); window.Ready.GetAwaiter().GetResult(); Pump();
            var panes = Descendants<PaneView>(window).ToArray();
            var pane = panes.Single(p => ((PaneViewModel)p.DataContext).State.Side == PaneSide.Left);
            var list = pane.CurrentFileList!; var source = PresentationSource.FromVisual(window)!;
            var entries = model.Left.State.SelectedTab.Entries;
            list.SelectedItem=entries[1]; Pump();
            var row=(ListViewItem)list.ItemContainerGenerator.ContainerFromItem(entries[1]);
            RaiseKey(row, source, Key.Enter); Pump(); Assert.AreEqual(entries[1], files.Opened.Single());
            var folder=Descendants<FolderView>(pane).Single();
            var doubleClick=typeof(FolderView).GetMethod("Files_MouseDoubleClick", BindingFlags.NonPublic|BindingFlags.Instance)!;
            doubleClick.Invoke(folder, [list, new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left) { RoutedEvent=Control.MouseDoubleClickEvent, Source=row }]);
            Pump(); Assert.AreEqual(2, files.Opened.Count); Assert.AreEqual(entries[1],files.Opened[1]);
            // With multiple selections, the focused row is the sole Enter target.
            list.SelectedItems.Add(entries[2]); Pump();
            var second=(ListViewItem)list.ItemContainerGenerator.ContainerFromItem(entries[2]);
            RaiseKey(second,source,Key.Enter); Pump(); Assert.AreEqual(3,files.Opened.Count); Assert.AreEqual(entries[2],files.Opened.Last());
            RaiseKey(second,source,Key.F4); Pump(); Assert.AreEqual(0,edit.Calls);
            list.SelectedItems.Clear(); Pump(); RaiseKey(row,source,Key.Enter); Pump();
            Assert.AreEqual(3,files.Opened.Count,"No selection must not open a formerly focused row");
            list.SelectedItems.Clear(); list.SelectedItem=entries[1]; Pump();
            Assert.IsTrue(Descendants<Button>(window).Single(b=>Equals(b.Content,"F4 編集")).IsEnabled);
            RaiseKey(row,source,Key.F4); Pump(); Assert.AreEqual(1,edit.Calls); Assert.AreEqual(entries[1],edit.Last);
            list.SelectedItems.Clear(); RaiseKey(list,source,Key.F4); Pump(); Assert.AreEqual(1,edit.Calls);
            list.SelectedItem=entries[0]; RaiseKey(list,source,Key.F4); Pump(); Assert.AreEqual(1,edit.Calls);
            RaiseKey(list,source,Key.Enter); Pump(); Assert.IsTrue(model.Left.State.SelectedTab.CurrentPath.EndsWith("child"));
            RaiseKey(list,source,Key.Back); Pump(); Assert.IsFalse(model.Left.State.SelectedTab.CurrentPath.EndsWith("child"));
            Assert.AreEqual(2,Descendants<FolderView>(window).Count());
            Assert.IsFalse(typeof(MainWindow).Assembly.GetTypes().Any(t=>t.Name.StartsWith("Miller",StringComparison.Ordinal)));
            Assert.IsFalse(Descendants<Button>(window).Any(b=>b.ToolTip?.ToString()?.Contains("階層") == true));
            var settings=new SettingsDialog(model) { Owner=window };
            try { settings.Show(); Pump(); Assert.IsFalse(Descendants<TextBlock>(settings).Any(t=>t.Text.Contains("階層",StringComparison.Ordinal))); }
            finally { settings.Close(); }
        }
        finally { window.Close(); Pump(); }
    }
}

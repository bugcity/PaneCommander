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
    private static void VerifyActivePaneTarget()
    {
        var operations = new ReviewUiOperations();
        using var model = new MainViewModel(new ReviewUiFiles(), new FakeLogger(), fileOperations: operations);
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        try
        {
            window.Show(); Pump();
            CompleteWithDispatcher(model.Workspace.Left.SelectedTab.NavigateAsync(@"C:\pane-target\left"));
            CompleteWithDispatcher(model.Workspace.Right.SelectedTab.NavigateAsync(@"C:\pane-target\right")); Pump();
            var left = (PaneView)window.FindName("LeftPane"); var right = (PaneView)window.FindName("RightPane");
            void Check(PaneSide side)
            {
                Assert.AreEqual(side, model.Workspace.ActiveSide);
                Assert.AreEqual(side == PaneSide.Left, model.Workspace.Left.IsActive);
                Assert.AreEqual(side == PaneSide.Right, model.Workspace.Right.IsActive);
                Assert.AreEqual(side == PaneSide.Left ? "left" : "right", window.Title);
                Assert.AreEqual(side == PaneSide.Left ? Visibility.Visible : Visibility.Hidden, Descendants<TextBlock>(left).Single(t => t.Text == "ACTIVE").Visibility);
                Assert.AreEqual(side == PaneSide.Right ? Visibility.Visible : Visibility.Hidden, Descendants<TextBlock>(right).Single(t => t.Text == "ACTIVE").Visibility);
                Assert.AreEqual(1, Descendants<TextBlock>(window).Count(t => t.Text == "ACTIVE" && t.Visibility == Visibility.Visible));
            }
            void ClickBlank(PaneView pane)
            {
                ((Border)pane.Content).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent }); Pump();
            }
            left.CurrentFileList!.SelectedIndex = 1; left.FocusList(); Pump(); Check(PaneSide.Left);
            ClickBlank(right); Check(PaneSide.Right);
            Assert.IsTrue(right.CurrentFileList!.IsKeyboardFocusWithin, "パネルの余白クリックでも一覧へフォーカスを移す");
            left.FocusList(); Pump(); Check(PaneSide.Left); // Keyboard/programmatic focus entry updates the same target.
            var selectedPath = model.GetSelectedPaths!().Single();
            var titleButton = Descendants<Button>(window).Single(b => Equals(b.Content, "お気に入り"));
            titleButton.Focus(); Pump(); Check(PaneSide.Left);
            Assert.IsFalse(left.HasInputFocus); Assert.IsFalse(right.HasInputFocus);
            typeof(Window).GetMethod("OnDeactivated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [EventArgs.Empty]);
            Pump(); Check(PaneSide.Left);
            Capture(window, "active-target-unfocused.png");
            var delete = Descendants<Button>(window).Single(b => b.DataContext is FunctionAction { Number: 8 });
            Assert.IsFalse(delete.Focusable); Assert.IsTrue(delete.IsEnabled);
            delete.Command.Execute(null); Pump();
            Assert.IsNotNull(operations.Last); CollectionAssert.AreEqual(new[] { selectedPath }, operations.Last.Sources);
            Check(PaneSide.Left);
            right.BeginFilter(); Pump(); Check(PaneSide.Right);
            Assert.IsTrue(((TextBox)right.FindName("FilterBox")).IsKeyboardFocused, "入力欄のフォーカスを奪わない");
            var pathLabel = Descendants<TextBlock>(left).Single(t => t.Text == model.Workspace.Left.SelectedTab.CurrentPath);
            pathLabel.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Pump(); Check(PaneSide.Left); Assert.IsTrue(left.CurrentFileList!.IsKeyboardFocusWithin);
            Capture(window, "active-target-focused.png");
        }
        finally { window.Close(); Pump(); }
    }
}

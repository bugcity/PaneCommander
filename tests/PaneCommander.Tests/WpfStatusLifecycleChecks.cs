using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
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
    private static void VerifyStatusLifecycle()
    {
        using var model = new MainViewModel(new FakeFileSystem(), new FakeLogger());
        var window = new MainWindow { DataContext = model, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        Window? receiver = null;
        try
        {
            window.Show(); window.Activate(); Pump();
            var pane = (PaneView)window.FindName("LeftPane"); var tab = model.Workspace.Left.SelectedTab;
            pane.FocusList(); Pump();
            Button Dismiss() => Descendants<Button>(pane).Single(b => AutomationProperties.GetName(b) == "操作通知を閉じる");
            var report = tab.BeginOperationStatus("テスト処理中");
            Assert.IsFalse(Dismiss().IsVisible);
            report("テスト正常完了", OperationStatusKind.Success); Pump();
            Assert.IsTrue(Dismiss().IsVisible); Assert.IsFalse(Dismiss().Focusable);
            var elapsed = Stopwatch.StartNew();
            while (tab.OperationKind != OperationStatusKind.None && elapsed.Elapsed < TimeSpan.FromSeconds(7)) { Pump(); Thread.Sleep(10); }
            Assert.AreEqual(OperationStatusKind.None, tab.OperationKind, "実際のDispatcher期限で消去する");
            Assert.IsTrue(elapsed.Elapsed >= TimeSpan.FromSeconds(4.8), "5秒より早く消さない");
            Assert.IsFalse(Dismiss().IsVisible); StringAssert.Contains(tab.Status, "件");

            report = tab.BeginOperationStatus("処理中"); report("確認が必要", OperationStatusKind.Attention); Pump();
            var list = pane.CurrentFileList!; list.SelectedIndex = 0; pane.FocusList(); Pump();
            var selection = list.SelectedItem;
            tab.FilterText = "sample"; Pump();
            RaiseKey(list, PresentationSource.FromVisual(list)!, Key.Escape); Pump();
            Assert.AreEqual("", tab.FilterText); Assert.AreEqual(OperationStatusKind.Attention, tab.OperationKind, "Escは既存フィルター解除を優先");
            RaiseKey(list, PresentationSource.FromVisual(list)!, Key.Escape); Pump();
            Assert.AreEqual(OperationStatusKind.None, tab.OperationKind); Assert.AreSame(selection, list.SelectedItem);
            report = tab.BeginOperationStatus("処理中"); report("エラー確認", OperationStatusKind.Attention); Pump();
            Dismiss().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert.AreEqual(OperationStatusKind.None, tab.OperationKind);
            Assert.IsTrue(list.IsKeyboardFocusWithin, "通知を閉じても一覧からフォーカスを奪わない");

            report = tab.BeginOperationStatus("処理中"); report("別タブでは停止", OperationStatusKind.Success); Pump();
            model.Workspace.Left.AddTab(); Pump();
            var paused = tab.OperationStatusTimeRemaining;
            Thread.Sleep(100); Pump(); Assert.AreEqual(paused, tab.OperationStatusTimeRemaining);
            model.Workspace.Left.SelectedTab = tab; Pump();
            receiver = new Window { Owner = window, Content = new TextBox(), ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 200, Height = 100 };
            receiver.Show(); receiver.Activate(); Pump(); Assert.IsFalse(window.IsActive);
            paused = tab.OperationStatusTimeRemaining;
            Thread.Sleep(100); Pump(); Assert.AreEqual(paused, tab.OperationStatusTimeRemaining, "他のウィンドウがアクティブな間は停止");
            receiver.Close(); receiver = null; window.Activate(); Pump();
            Assert.IsTrue(tab.OperationStatusTimeRemaining <= paused);
            tab.DismissOperationStatus();
            report = tab.BeginOperationStatus("処理中"); report("終了時に解除", OperationStatusKind.Success); Pump();
            window.Close(); Pump(); paused = tab.OperationStatusTimeRemaining;
            Thread.Sleep(100); Pump(); Assert.AreEqual(paused, tab.OperationStatusTimeRemaining, "アンロード後に期限やイベントを動かさない");
        }
        finally { receiver?.Close(); window.Close(); Pump(); }
    }
}

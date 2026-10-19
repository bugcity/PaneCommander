using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using PaneCommander.Core;

namespace PaneCommander.App.Views;

public partial class FolderView
{
    private Window? statusWindow;
    private TabState? statusTab;
    private DispatcherTimer? statusTimeout;

    private void InitializeStatusLifecycle()
    {
        Loaded += (_, _) => BindStatusLifecycle();
        Unloaded += (_, _) => UnbindStatusLifecycle();
        DataContextChanged += (_, _) => { if (IsLoaded) BindStatusLifecycle(); };
        IsVisibleChanged += (_, _) => UpdateStatusLifecycle();
    }
    private void BindStatusLifecycle()
    {
        UnbindStatusLifecycle();
        statusTab = DataContext as TabState;
        if (statusTab is not null) PropertyChangedEventManager.AddHandler(statusTab, StatusLifecycleChanged, nameof(TabState.OperationStatusTimeRemaining));
        statusWindow = Window.GetWindow(this);
        if (statusWindow is not null)
        {
            statusWindow.Activated += StatusWindowChanged;
            statusWindow.Deactivated += StatusWindowChanged;
            statusWindow.StateChanged += StatusWindowChanged;
        }
        UpdateStatusLifecycle();
    }
    private void UnbindStatusLifecycle()
    {
        if (statusTimeout is not null) { statusTimeout.Stop(); statusTimeout.Tick -= StatusTimeout_Tick; statusTimeout = null; }
        if (statusTab is not null)
        {
            PropertyChangedEventManager.RemoveHandler(statusTab, StatusLifecycleChanged, nameof(TabState.OperationStatusTimeRemaining));
            statusTab.SetStatusPresented(false); statusTab = null;
        }
        if (statusWindow is not null)
        {
            statusWindow.Activated -= StatusWindowChanged;
            statusWindow.Deactivated -= StatusWindowChanged;
            statusWindow.StateChanged -= StatusWindowChanged;
            statusWindow = null;
        }
    }
    private void StatusLifecycleChanged(object? sender, PropertyChangedEventArgs e) => UpdateStatusLifecycle();
    private void StatusWindowChanged(object? sender, EventArgs e) => UpdateStatusLifecycle();
    private void UpdateStatusLifecycle()
    {
        statusTimeout?.Stop();
        var presented = IsLoaded && IsVisible && statusWindow is { IsActive: true } && statusWindow.WindowState != WindowState.Minimized;
        statusTab?.SetStatusPresented(presented);
        if (!presented || statusTab?.OperationStatusTimeRemaining is not { } remaining) return;
        if (remaining <= TimeSpan.Zero) { statusTab.ExpireOperationStatus(); return; }
        // A single deadline, only while an expiring message is actually visible.
        // Background refreshes do not restart its five seconds.
        if (statusTimeout is null)
        {
            statusTimeout = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            statusTimeout.Tick += StatusTimeout_Tick;
        }
        statusTimeout.Interval = remaining; statusTimeout.Start();
    }
    private void StatusTimeout_Tick(object? sender, EventArgs e)
    {
        statusTimeout?.Stop(); statusTab?.ExpireOperationStatus(); UpdateStatusLifecycle();
    }
    private void DismissStatus_Click(object sender, RoutedEventArgs e)
    {
        if (statusTab?.DismissOperationStatus() == true) FileDragBehavior.Ancestor<PaneView>(this)?.FocusList(statusTab);
    }
}

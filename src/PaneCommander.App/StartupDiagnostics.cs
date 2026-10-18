using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PaneCommander.App.ViewModels;

namespace PaneCommander.App;

// Opt-in verification of the actual product EXE. No polling/timer on normal startup.
internal static class StartupDiagnostics
{
    public static async Task RunAsync(MainWindow window, MainViewModel model, string directory, RenderMode modeBeforeWindow, bool visitTabs, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        try
        {
            await window.Ready;
            if (visitTabs)
                foreach (var pane in new[] { model.Workspace.Left, model.Workspace.Right })
                {
                    var original = pane.SelectedTab;
                    foreach (var tab in pane.Tabs)
                    {
                        pane.SelectedTab = tab;
                        while (tab.IsLoading) await Task.Delay(10, token);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    }
                    pane.SelectedTab = original;
                }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(10000, token);
            var eventName = @"Local\PaneCommander.Verify." + Guid.NewGuid().ToString("N");
            using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            var dpi = VisualTreeHelper.GetDpi(window);
            var gc = GC.GetGCMemoryInfo();
            var readyPath = Path.Combine(directory, "ready.json");
            await File.WriteAllTextAsync(readyPath + ".tmp", JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId, runtime = RuntimeInformation.FrameworkDescription,
                modeBeforeWindow = modeBeforeWindow.ToString(), effectiveRenderMode = RenderOptions.ProcessRenderMode.ToString(),
                settings = model.Settings, session = model.Workspace.Capture(window.CaptureLayout()), favorites = model.Favorites.ToArray(),
                startupWarning = model.StartupWarning, eventName, warmupSeconds = 10, productionExecutable = true, visitTabs,
                dpiX = dpi.DpiScaleX, dpiY = dpi.DpiScaleY, window.Width, window.Height,
                realizedRows = Walk(window).OfType<ListViewItem>().Count(), folderViews = Walk(window).OfType<Views.FolderView>().Count(),
                tabs = model.Workspace.Left.Tabs.Count + model.Workspace.Right.Tabs.Count,
                loadedTabs = model.Workspace.Left.Tabs.Concat(model.Workspace.Right.Tabs).Count(t => t.Entries.Count > 0),
                statuses = model.Workspace.Left.Tabs.Concat(model.Workspace.Right.Tabs).Select(t => new { t.CurrentPath, t.Status, t.NavigationSucceeded, count = t.Entries.Count }),
                managedBytesWithoutGc = GC.GetTotalMemory(false), gcHeapAtLastGc = gc.HeapSizeBytes, gcCommittedAtLastGc = gc.TotalCommittedBytes,
                forcedGc = false, offscreen = false
            }, new JsonSerializerOptions { WriteIndented = true }), token);
            File.Move(readyPath + ".tmp", readyPath, true);
            await Task.Run(() => WaitHandle.WaitAny([stop, token.WaitHandle]), token);
            if (!token.IsCancellationRequested)
            {
                var finalGc = GC.GetGCMemoryInfo();
                await File.WriteAllTextAsync(Path.Combine(directory, "closing.json"), JsonSerializer.Serialize(new
                {
                    managedBytesWithoutGc = GC.GetTotalMemory(false), gcHeapAtLastGc = finalGc.HeapSizeBytes,
                    gcCommittedAtLastGc = finalGc.TotalCommittedBytes, gcIndex = finalGc.Index, forcedGc = false
                }), token);
                window.Close();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "diagnostics-failure.json"), JsonSerializer.Serialize(new { error = ex.ToString() }));
            // Diagnostic failure does not terminate normal file work.
        }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var item in Walk(child)) yield return item; }
    }
}

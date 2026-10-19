using System.Text.Json;
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
    private static void VerifyColumnViewportFit()
    {
        using var model = new MainViewModel(new ViewportFiles(), new FakeLogger());
        var window = new MainWindow { DataContext = model, Width = 1800, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        var measurements = new List<object>();
        try
        {
            window.Show(); Pump();
            var left = ((PaneView)window.FindName("LeftPane")).CurrentFileList!;
            var right = ((PaneView)window.FindName("RightPane")).CurrentFileList!;
            var leftGrid = (GridView)left.View; var rightGrid = (GridView)right.View;
            var metadata = leftGrid.Columns.Skip(1).Select(c => c.Width).ToArray();
            Assert.IsTrue(metadata.All(w => w > 0));
            window.Width = 1280; Pump();
            ColumnLayout Fitting(ListView list)
            {
                var viewport = Descendants<ScrollViewer>(list).First().ViewportWidth;
                return new ColumnLayout
                {
                    NameFillsRemaining = false,
                    Columns = [new("Name", true, viewport - 8 - metadata.Sum() - 2), new("Kind", true, metadata[0]),
                        new("Size", true, metadata[1]), new("FileAge", true, metadata[2]), new("LastWriteTime", true, metadata[3])]
                };
            }
            var leftLayout = Fitting(left); var rightLayout = Fitting(right);
            CompleteWithDispatcher(model.ApplySettingsAsync(model.Settings with { LeftColumns = leftLayout, RightColumns = rightLayout })); Pump();
            void Check(ListView list, string phase, bool allVisible)
            {
                var scroll = Descendants<ScrollViewer>(list).First(); var grid = (GridView)list.View;
                measurements.Add(new { Phase = phase, list.ActualWidth, scroll.ViewportWidth, scroll.ExtentWidth,
                    Vertical = scroll.ComputedVerticalScrollBarVisibility.ToString(), Widths = grid.Columns.ToDictionary(c => (string)c.Header, c => c.Width) });
                if (allVisible) Assert.IsTrue(grid.Columns.All(c => c.Width > 0), phase + ": 実表示幅に収まる全列を表示する");
                Assert.IsTrue(grid.Columns.Sum(c => c.Width) + 8 <= scroll.ViewportWidth + .1, phase);
                Assert.AreEqual(Visibility.Collapsed, scroll.ComputedHorizontalScrollBarVisibility, phase + ": 横スクロール不要");
            }
            Check(left, "left-fit", true); Check(right, "right-fit", true);
            var expandedName = leftLayout with { Columns = leftLayout.Columns.Select(c => c.Key == "Name" ? c with { Width = c.Width + 12 } : c).ToArray() };
            CompleteWithDispatcher(model.ApplySettingsAsync(model.Settings with { LeftColumns = expandedName })); Pump();
            Assert.AreEqual(0d, leftGrid.Columns[4].Width);
            Check(left, "left-with-scrollbar", false);
            var outerWidth = left.ActualWidth;
            CompleteWithDispatcher(model.Workspace.Left.SelectedTab.NavigateAsync(@"C:\columns\empty")); Pump();
            Assert.AreEqual(outerWidth, left.ActualWidth, .1, "ListView外寸を変えずにスクロールバーだけが消える");
            Check(left, "left-without-scrollbar", true);
            CompleteWithDispatcher(model.Workspace.Left.SelectedTab.NavigateAsync(@"C:\columns\many")); Pump();
            Assert.AreEqual(0d, leftGrid.Columns[4].Width);
            Check(left, "left-scrollbar-restored", false);
            Check(right, "right-unchanged", true);
            Assert.AreEqual(rightLayout, model.Settings.RightColumns);
        }
        finally
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "column-viewport-metrics.json"), JsonSerializer.Serialize(measurements));
            window.Close(); Pump();
        }
    }

    private sealed class ViewportFiles : IFileSystemService
    {
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,
            path.EndsWith("empty", StringComparison.Ordinal) ? [] : Enumerable.Range(0, 1200).Select(i => new FileEntry(Path.Combine(path, $"item-{i:D4}.txt"), $"item-{i:D4}.txt", false, 1, null)).ToArray()));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
}

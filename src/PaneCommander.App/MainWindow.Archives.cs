using System.IO;
using System.Windows;
using PaneCommander.App.ViewModels;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.App;

public partial class MainWindow
{
    internal async Task RequestArchiveAsync(bool compress)
    {
        if (Model.IsFileOperationRunning || !(compress ? Model.CanCompressArchives : Model.CanExtractArchives)) return;
        var pane = Model.Workspace.ActivePane; var tab = pane.SelectedTab; var directory = tab.CurrentPath;
        var sources = Model.GetSelectedPaths?.Invoke() ?? [];
        ArchiveCompressionRequest? request = null;
        if (compress)
        {
            var entry = sources.Length == 1 ? tab.Entries.FirstOrDefault(e => e.FullPath == sources[0]) : null;
            var initial = sources.Length == 1 ? entry?.IsDirectory == true ? Path.GetFileName(sources[0]) : Path.GetFileNameWithoutExtension(sources[0]) : Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            var dialog = new ArchiveCompressionDialog(string.IsNullOrEmpty(initial) ? "archive" : initial) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var name = dialog.ArchiveName;
            if (ArchiveFormats.Detect(name) is { } old && old != dialog.Format) name = ArchiveFormats.Stem(name);
            request = new(sources, directory, name, dialog.Format, dialog.Level, Model.Settings.GetArchiveReserveBytes());
        }
        var interaction = new ArchiveWindowInteraction(this);
        var result = await Model.ExecuteArchiveAsync(tab, (service, progress, token) => compress
            ? service.CompressAsync(request!, interaction, progress, token)
            : service.ExtractAsync(sources, directory, Model.Settings.GetArchiveReserveBytes(), interaction, progress, token));
        if (closed) return;
        if (tab.CurrentPath == directory) tab.RequestSelection(result.Outputs);
        if (ReferenceEquals(pane.SelectedTab, tab) && tab.CurrentPath == directory)
        {
            var view = pane.Side == PaneSide.Left ? LeftPane : RightPane;
            if (view.CurrentFileList is { } list)
            {
                if (pane.IsActive && result.Outputs.Count > 0) view.FocusList(tab);
            }
        }
        else if (tab.CurrentPath != directory)
            MessageBox.Show(this, directory + "\n" + MainViewModel.ArchiveSummary(result), "圧縮・展開の結果", MessageBoxButton.OK, result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}

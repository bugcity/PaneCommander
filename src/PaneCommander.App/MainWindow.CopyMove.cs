using System.IO;
using System.Windows.Interop;
using PaneCommander.App.Views;
using PaneCommander.Core;

namespace PaneCommander.App;

public partial class MainWindow
{
    private bool preparingCopyMove;

    private async Task CopyMoveAsync(FileOperationKind kind, string[] sources, TabState sourceTab, string destination)
    {
        if (preparingCopyMove || Model.IsFileOperationRunning || sources.Length == 0) return;
        preparingCopyMove = true;
        try
        {
            // Read all destination names, including items hidden by the current view.
            var target = await Model.FileSystem.ReadFolderAsync(destination, CancellationToken.None);
            if (closed || Model.IsFileOperationRunning) return;
            var names = target.Entries.Select(entry => entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requests = new List<FileOperationRequest>();
            foreach (var source in sources)
            {
                var originalName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
                var name = originalName;
                if (names.Contains(originalName))
                {
                    var entry = sourceTab.Entries.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.FullPath, source));
                    var dialog = new FileOperationDialog(kind, 1, destination, Path.GetDirectoryName(source),
                        originalName: originalName, originalIsDirectory: entry?.IsDirectory == true, collisionOnly: true) { Owner = this };
                    if (dialog.ShowDialog() != true) return;
                    name = dialog.NewName ?? originalName;
                }
                // The entered name is final. Do not prompt again if it also exists.
                requests.Add(new(kind, [source], destination, name, Overwrite: true));
                names.Add(name);
            }
            await Model.ExecuteFileOperationBatchAsync(requests, new WindowInteropHelper(this).Handle, sourceTab);
        }
        finally { preparingCopyMove = false; }
    }
}

using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

public sealed class FolderWatchService : IFolderWatchService
{
    public IDisposable Watch(string path, Action changed, Action<Exception> failed)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size
                | NotifyFilters.LastWrite | NotifyFilters.Attributes
        };
        watcher.Created += (_, _) => changed();
        watcher.Deleted += (_, _) => changed();
        watcher.Changed += (_, _) => changed();
        watcher.Renamed += (_, _) => changed();
        watcher.Error += (_, e) => failed(e.GetException());
        try { watcher.EnableRaisingEvents = true; return watcher; }
        catch { watcher.Dispose(); throw; }
    }
}

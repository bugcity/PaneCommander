namespace PaneCommander.Core;

public interface IShellEditService
{
    Task EditAsync(FileEntry entry, IntPtr owner);
}

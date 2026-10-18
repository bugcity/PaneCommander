using PaneCommander.Core;

namespace PaneCommander.App.ViewModels;

public sealed class PathDialogViewModel(TabState target) : ObservableObject
{
    private string path = target.CurrentPath;
    private string error = "";
    private bool busy;
    private bool canceled;
    public string Path { get => path; set => Set(ref path, value); }
    public string Error { get => error; private set => Set(ref error, value); }
    public bool Busy { get => busy; private set => Set(ref busy, value); }
    public async Task<bool> SubmitAsync()
    {
        if (Busy || canceled) return false;
        Busy = true;
        try
        {
            await target.NavigateAsync(Path);
            if (canceled) return false;
            Error = target.NavigationSucceeded ? "" : target.Status;
            return target.NavigationSucceeded;
        }
        finally { Busy = false; }
    }
    public void Cancel() { canceled = true; if (Busy) target.CancelLoading(); }
}

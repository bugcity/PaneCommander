using System.Windows;
using PaneCommander.App.ViewModels;
using PaneCommander.Infrastructure;
using System.IO;

namespace PaneCommander.App;

public partial class App : Application
{
    private MainViewModel? viewModel;
    private readonly CancellationTokenSource diagnosticsLifetime = new();
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var options = StartupOptions.Parse(e.Args);
        var store = new JsonSettingsStore(Path.Combine(options.ProfileDirectory, "settings.json"));
        var favoritesStore = new JsonFavoritesStore(Path.Combine(options.ProfileDirectory, "favorites.json"));
        var sessionStore = new JsonSessionStore(Path.Combine(options.ProfileDirectory, "session.json"));
        var logger = new ErrorLogger(Path.Combine(options.ProfileDirectory, "Logs"));
        var loaded = await store.LoadAsync();
        var favorites = await favoritesStore.LoadAsync();
        var session = loaded.Settings.RestoreSession ? await sessionStore.LoadAsync() : new PaneCommander.Core.StateLoadResult<PaneCommander.Core.SessionDocument>(null);
        var modeBeforeWindow = RenderingConfiguration.ApplyBeforeWindowCreation(loaded.Settings.RenderingMode);
        viewModel = new MainViewModel(new FileSystemService(), logger, store, loaded.Settings, favoritesStore, favorites.Value, sessionStore, loaded.Settings.RestoreSession ? session.Value : null,new ShellFileOperationService(),new ShellEditService(),new ArchiveService(logger));
        EventManager.RegisterClassHandler(typeof(Window),FrameworkElement.LoadedEvent,new RoutedEventHandler((sender,_)=>NativeTitleBar.Apply((Window)sender,viewModel.Settings)));
        viewModel.StartupWarning = string.Join(" ／ ", new[] { loaded.Error, favorites.Error is null ? null : "お気に入りを復元できません: " + favorites.Error,
            session.Error is null ? null : "作業状態を復元できないため初期状態を使用します: " + session.Error }.Where(s => s is not null));
        ThemeManager.Apply(loaded.Settings);
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
        var folderMonitor = new WorkspaceDirectoryMonitor(window, viewModel, new FolderWatchService(), logger);
        window.Closed += (_, _) => folderMonitor.Dispose();
        if (options.DiagnosticsDirectory is not null)
            await StartupDiagnostics.RunAsync(window, viewModel, options.DiagnosticsDirectory, modeBeforeWindow, options.VisitTabs, diagnosticsLifetime.Token);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        diagnosticsLifetime.Cancel();
        viewModel?.Dispose();
        base.OnExit(e);
    }
}

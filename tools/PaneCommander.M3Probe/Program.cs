using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var mode = args.Length > 1 ? Enum.Parse<RenderingPreference>(args[1]) : RenderingPreference.SoftwareOnly;
        RenderingConfiguration.ApplyBeforeWindowCreation(mode);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/PaneCommander.App;component/Themes/Controls.xaml") });
        var errors = new List<string>();
        app.DispatcherUnhandledException += (_, e) => { errors.Add(e.Exception.ToString()); e.Handled = true; };
        var store = new ImmediateStore();
        using var model = new MainViewModel(new FakeFiles(), new NullLogger(), sessionStore: store);
        var window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Width = 850, Height = 480, Left = -20000, Top = -20000 };
        double rightEdge = 0, availableWidth = 0, sidebar = 0; bool closed = false;
        window.Loaded += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            ((ColumnDefinition)window.FindName("SidebarColumn")).Width = new GridLength(360); window.UpdateLayout();
            var right = (FrameworkElement)window.FindName("RightPane"); var root = (FrameworkElement)window.Content;
            rightEdge = right.TransformToAncestor(root).Transform(new Point(right.ActualWidth, 0)).X;
            availableWidth = root.ActualWidth; sidebar = ((ColumnDefinition)window.FindName("SidebarColumn")).ActualWidth;
            window.Close();
            app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                closed = !window.IsVisible;
                if (!closed) window.Close();
                app.Shutdown();
            });
        });
        app.Run(window);
        var fits = rightEdge <= availableWidth + .01;
        File.WriteAllText(output, JsonSerializer.Serialize(new { mode = mode.ToString(), fits, rightEdge, availableWidth, sidebar, firstCloseSucceeded = closed, saves = store.Saves, errors }, new JsonSerializerOptions { WriteIndented = true }));
        return fits && closed && errors.Count == 0 ? 0 : 1;
    }
    private sealed class ImmediateStore : ISessionStore
    {
        public int Saves { get; private set; }
        public Task<StateLoadResult<SessionDocument>> LoadAsync(CancellationToken token = default) => Task.FromResult(new StateLoadResult<SessionDocument>(null));
        public Task SaveAsync(SessionDocument value, CancellationToken token = default) { value.Validate(); Saves++; return Task.CompletedTask; }
    }
    private sealed class FakeFiles : IFileSystemService
    {
        public Task<FolderSnapshot> ReadFolderAsync(string path, CancellationToken token) => Task.FromResult(new FolderSnapshot(path,[new FileEntry(Path.Combine(path,"sample.txt"),"sample.txt",false,0,DateTime.Now)]));
        public Task OpenFileAsync(FileEntry entry, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class NullLogger : IErrorLogger { public void Log(string context, Exception error) { } }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using PaneCommander.App;
using PaneCommander.App.ViewModels;
using PaneCommander.Core;

namespace PaneCommander.Performance;
internal static class FilterComparison
{
    public static int Run(string output,string mode)
    {
        Directory.CreateDirectory(output);RenderOptions.ProcessRenderMode=mode=="A"?System.Windows.Interop.RenderMode.Default:System.Windows.Interop.RenderMode.SoftwareOnly;
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/PaneCommander.App;component/Themes/Controls.xaml")});
        var files=new Fixture();using var model=new MainViewModel(files,new Logger());var window=new MainWindow{DataContext=model,ShowActivated=false,Left=60,Top=60,Width=1280,Height=800};
        var checkpoints=new List<object>();var timings=new List<object>();var result=0;
        var filter=typeof(TabState).GetProperty("FilterText");
        window.Loaded+=async(_,_)=>
        {
            try
            {
                await window.Ready;await model.Workspace.Left.SelectedTab.NavigateAsync(@"C:\filter-fixture\left");await model.Workspace.Right.SelectedTab.NavigateAsync(@"C:\filter-fixture\right");await Idle();await Task.Delay(5000);
                Write("phase.json",new{phase="baseline"});Write("ready.json",new{mode,filterImplemented=filter is not null,files=10000,renderMode=RenderOptions.ProcessRenderMode.ToString(),dpi=VisualTreeHelper.GetDpi(window).PixelsPerDip,forcedGc=false});
                await Hold("unused","");await Hold("few","item-09999");await Hold("many","item");
                await Repeat("typing",new[]{"i","it","ite","item","ite","it","i",""});
                await Repeat("start-clear",new[]{"item",""});
                Write("phase.json",new{phase="six-tabs"});
                foreach(var pane in new[]{model.Workspace.Left,model.Workspace.Right})for(var i=0;i<2;i++){pane.AddTab();await pane.SelectedTab.NavigateAsync(@"C:\filter-fixture\tabs");await Idle();}
                await Hold("six-tabs","item");await Hold("recovery","");
                Write("completed.json",new{forcedGc=false,filterImplemented=filter is not null,checkpoints,timings,enumerations=files.Reads});
                await Task.Delay(1000); // Let the external sampler observe completion before the process exits.
            }
            catch(Exception ex){result=1;File.WriteAllText(Path.Combine(output,"error.txt"),ex.ToString());}
            finally{window.Close();app.Shutdown(result);}
        };
        window.Show();return app.Run();
        async Task Hold(string phase,string query){Write("phase.json",new{phase});await Set(query,phase);await Task.Delay(3000);Point(phase);}
        async Task Repeat(string phase,string[] queries){Write("phase.json",new{phase});var before=files.Reads;for(var i=0;i<100;i++)await Set(queries[i%queries.Length],phase);if(files.Reads!=before)throw new InvalidOperationException("Filter caused enumeration");Point(phase);await Task.Delay(3000);}
        async Task Set(string query,string phase)
        {
            var tab=model.Workspace.Left.SelectedTab;var clock=Stopwatch.StartNew();var reads=files.Reads;
            if(filter is not null)filter.SetValue(tab,query);
            else {var view=CollectionViewSource.GetDefaultView(tab.Entries);view.Filter=query.Length==0?null:value=>value is FileEntry entry && entry.Name.Contains(query,StringComparison.OrdinalIgnoreCase);}
            await Idle();timings.Add(new{phase,query,layoutMs=clock.Elapsed.TotalMilliseconds,visible=CollectionViewSource.GetDefaultView(tab.Entries).Cast<object>().Count()});
            if(files.Reads!=reads)throw new InvalidOperationException("Unexpected enumeration");
        }
        void Point(string phase){using var process=Process.GetCurrentProcess();process.Refresh();checkpoints.Add(new{phase,privateBytes=process.PrivateMemorySize64,workingSet=process.WorkingSet64,rows=Children<ListViewItem>(window).Count(),folderViews=Children<PaneCommander.App.Views.FolderView>(window).Count(),tabs=model.Workspace.Left.Tabs.Count+model.Workspace.Right.Tabs.Count,managed=GC.GetTotalMemory(false),enumerations=files.Reads});}
        void Write(string name,object value){var path=Path.Combine(output,name);File.WriteAllText(path+".tmp",JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));File.Move(path+".tmp",path,true);}
    }
    private static async Task Idle(){await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
    private static IEnumerable<T> Children<T>(DependencyObject parent)where T:DependencyObject{for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T value)yield return value;foreach(var item in Children<T>(child))yield return item;}}
    private sealed class Fixture:IFileSystemService
    {
        public int Reads;public Task<FolderSnapshot> ReadFolderAsync(string path,CancellationToken token){Reads++;return Task.FromResult(new FolderSnapshot(path,Enumerable.Range(0,10000).Select(i=>new FileEntry(Path.Combine(path,$"item-{i:D5}.txt"),$"item-{i:D5}.txt",false,i,DateTime.Today.AddDays(-i))).ToArray()));}public Task OpenFileAsync(FileEntry entry,CancellationToken token)=>Task.CompletedTask;
    }
    private sealed class Logger:IErrorLogger{public void Log(string context,Exception exception)=>throw new InvalidOperationException(context,exception);}
}

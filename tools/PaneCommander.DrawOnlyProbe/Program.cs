using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

// Standalone functional rendering control. No product assembly reference, resource sampling, timer or polling.
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if(args.Length!=2 || args[0] is not ("Default" or "SoftwareOnly"))return 2;
        var mode=args[0];var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode=mode=="Default"?RenderMode.Default:RenderMode.SoftwareOnly;
        var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
        var panel=new StackPanel{Margin=new Thickness(24)};
        panel.Children.Add(new TextBlock{Text="Standalone WPF / "+mode,Foreground=Brushes.White,FontSize=26});
        panel.Children.Add(new Border{Height=90,Margin=new Thickness(0,20,0,20),Background=Brushes.OrangeRed,Child=new TextBlock{Text="RED / 初期描画",FontSize=24}});
        panel.Children.Add(new Border{Height=90,Background=Brushes.CornflowerBlue,Child=new TextBlock{Text="BLUE / 対照画面",FontSize=24}});
        var button=new Button{Content="Counter: 0",Height=50,Margin=new Thickness(0,20,0,0)};panel.Children.Add(button);
        var window=new Window{Title="DrawOnly "+mode+" "+Path.GetFileName(output),Width=800,Height=640,Left=80,Top=80,WindowStartupLocation=WindowStartupLocation.Manual,Background=new SolidColorBrush(Color.FromRgb(25,37,47)),Content=panel};
        void Save(string stage)
        {
            var dpi=VisualTreeHelper.GetDpi(window);
            File.WriteAllText(Path.Combine(output,stage+".json"),JsonSerializer.Serialize(new{stage,Mode=mode,AppliedMode=RenderOptions.ProcessRenderMode.ToString(),ProcessId=Environment.ProcessId,WindowHandle=$"0x{new WindowInteropHelper(window).Handle:X}",window.Title,Runtime=RuntimeInformation.FrameworkDescription,Dpi=dpi.DpiScaleX,RenderTier=RenderCapability.Tier>>16,PhysicalDisplayVerified=false,NoMemoryOrCpuMeasurement=true,NoTimer=true},new JsonSerializerOptions{WriteIndented=true}));
        }
        var count=0;button.Click+=(_,_)=>{button.Content="Counter: "+ ++count;Save("counter-"+count);};
        window.ContentRendered+=(_,_)=>Save("content-rendered-not-physical-present");
        window.Closed+=(_,_)=>Save("closed");
        return app.Run(window);
    }
}

using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PaneCommander.Performance;

// Diagnostic-only minimal WPF surface. Not a product rendering workaround or a GUI acceptance substitute.
internal static class CaptureProbe
{
    internal static int Run(string mode,string output)
    {
        if(mode is not ("Default" or "SoftwareOnly"))return 2;
        Directory.CreateDirectory(output);
        RenderOptions.ProcessRenderMode=mode=="Default"?RenderMode.Default:RenderMode.SoftwareOnly;
        var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
        var clock=Stopwatch.StartNew();var events=new List<object>();var snapshots=new List<object>();
        void Record(string kind,object? detail=null){if(events.Count<2000)events.Add(new{ElapsedMs=clock.Elapsed.TotalMilliseconds,Kind=kind,Detail=detail});}
        var panel=new ProbePanel(()=>Record("RootOnRender")){Margin=new Thickness(24),Background=new SolidColorBrush(Color.FromRgb(25,37,47))};
        panel.Children.Add(new TextBlock{Text="WPF initial / partial redraw diagnostic",FontSize=26,Foreground=Brushes.White});
        panel.Children.Add(new Border{Height=80,Margin=new Thickness(0,20,0,20),Background=Brushes.OrangeRed,Child=new TextBlock{Text="RED / 白い画面ではありません",FontSize=24}});
        panel.Children.Add(new Border{Height=80,Background=Brushes.CornflowerBlue,Child=new TextBlock{Text="BLUE / minimal WPF",FontSize=24}});
        var button=new Button{Content="Counter: 0",Margin=new Thickness(0,20,0,0),Height=48};panel.Children.Add(button);var count=0;
        var whole=new Button{Content="全面の背景色を変更（1回）",Margin=new Thickness(0,10,0,0),Height=40};panel.Children.Add(whole);
        var bitmap=new Button{Content="診断用Bitmapを保存（実画面とは別）",Margin=new Thickness(0,10,0,0),Height=40};panel.Children.Add(bitmap);
        var mark=new Button{Content="計測点を記録",Margin=new Thickness(0,10,0,0),Height=40};panel.Children.Add(mark);
        var window=new Window{Title="PaneCommander capture probe "+mode,Width=800,Height=640,Left=80,Top=80,WindowStartupLocation=WindowStartupLocation.Manual,Background=new SolidColorBrush(Color.FromRgb(25,37,47)),Content=panel};
        void Save(string stage)
        {
            using var process=Process.GetCurrentProcess();process.Refresh();
            snapshots.Add(new{Stage=stage,ElapsedMs=clock.Elapsed.TotalMilliseconds,CpuSeconds=process.TotalProcessorTime.TotalSeconds,PrivateBytes=process.PrivateMemorySize64,WorkingSet=process.WorkingSet64,Handles=process.HandleCount,Processors=Environment.ProcessorCount,RootOnRenderCount=panel.RenderCount,Counter=count,window.Width,window.Height});
            File.WriteAllText(Path.Combine(output,"observations.json"),JsonSerializer.Serialize(new{Mode=mode,Events=events,Snapshots=snapshots,ForcedGc=false,ConstantTimer=false,UiCompletionDefinition="Dispatcher ContextIdle only; not physical frame presentation",Product=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        void AfterInput(string stage){Record(stage);window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>Save(stage+"-ContextIdle")));}
        button.Click+=(_,_)=>{button.Content="Counter: "+ ++count;AfterInput("CounterClick");};
        var changed=false;whole.Click+=(_,_)=>{changed=!changed;panel.Background=new SolidColorBrush(changed?Color.FromRgb(45,65,80):Color.FromRgb(25,37,47));AfterInput("WholeBackgroundChange");};
        bitmap.Click+=(_,_)=>
        {
            var watch=Stopwatch.StartNew();var dpi=VisualTreeHelper.GetDpi(panel);
            var target=new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(panel.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);
            target.Render(panel);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(target));
            using(var file=File.Create(Path.Combine(output,"logical-visual-bitmap.png")))encoder.Save(file);
            Record("RenderTargetBitmap",new{ElapsedMs=watch.Elapsed.TotalMilliseconds,IsPhysicalScreenCapture=false});AfterInput("LogicalBitmapSaved");
        };
        mark.Click+=(_,_)=>AfterInput("ManualCheckpoint");
        button.MouseEnter+=(_,_)=>AfterInput("CounterMouseEnter");button.MouseLeave+=(_,_)=>AfterInput("CounterMouseLeave");
        window.PreviewMouseWheel+=(_,e)=>AfterInput("MouseWheel-"+e.Delta);
        window.ContentRendered+=(_,_)=>{Record("WindowContentRendered-NotPhysicalPresent");Save("ContentRendered");};
        HwndSource? source=null;
        IntPtr Hook(IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled){if(msg is 0x000f or 0x0005 or 0x0014 or 0x031e)Record("HwndMessage",new{Message=$"0x{msg:X4}"});return IntPtr.Zero;}
        window.SourceInitialized+=(_,_)=>{source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);source.AddHook(Hook);Record("SourceInitialized");};
        window.Closed+=(_,_)=>{Save("Closed");source?.RemoveHook(Hook);};
        app.MainWindow=window;
        window.Loaded+=async(_,_)=>
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var dpi=VisualTreeHelper.GetDpi(window);
            var dwmHr=DwmIsCompositionEnabled(out var composition);
            File.WriteAllText(Path.Combine(output,"ready.json"),JsonSerializer.Serialize(new{Mode=mode,ActualMode=RenderOptions.ProcessRenderMode.ToString(),RenderTier=RenderCapability.Tier>>16,Runtime=RuntimeInformation.FrameworkDescription,DpiX=dpi.DpiScaleX,DpiY=dpi.DpiScaleY,ProcessId=Environment.ProcessId,Product=false,PhysicalDisplayVerified=false,Width=window.Width,Height=window.Height,DwmComposition=composition,DwmHr=$"0x{dwmHr:X8}"},new JsonSerializerOptions{WriteIndented=true}));
            Record("LayoutApplicationIdle-NotPhysicalPresent");Save("InitialApplicationIdle");
        };
        return app.Run(window);
    }
    private sealed class ProbePanel(Action rendered):StackPanel
    {
        public int RenderCount{get;private set;}
        protected override void OnRender(DrawingContext context){base.OnRender(context);RenderCount++;rendered();}
    }
    [DllImport("dwmapi.dll")]private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)]out bool enabled);
}

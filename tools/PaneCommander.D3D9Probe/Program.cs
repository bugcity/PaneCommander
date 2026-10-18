using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Diagnostics;

// Standalone diagnostic. No product/WPF reference, timer, registry write, resource sampling or fallback renderer.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("gdi" or "d3d9" or "d3d9ex")) return 2;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new ProbeForm(args[0], Path.GetFullPath(args[1])));
        return 0;
    }
}

internal sealed class ProbeForm : Form
{
    private readonly string mode, output;
    private IntPtr factory, device;
    private int paints;
    private string? error, luid;
    private int createHr, clearHr, presentHr, presentExHr, stateHr;
    private readonly List<object> stages=[];
    private object? pixelReadback;
    public ProbeForm(string mode, string output)
    {
        this.mode=mode; this.output=output; Directory.CreateDirectory(output);
        Text="Standalone "+mode+" / red-blue control";
        StartPosition=FormStartPosition.Manual; Location=new Point(120,120); ClientSize=new Size(800,600);
        FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false;
        SetStyle(ControlStyles.Opaque|ControlStyles.UserPaint, true);
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            if(mode!="gdi")
            {
                if(mode=="d3d9ex")
                {
                    Marshal.ThrowExceptionForHR(Direct3DCreate9Ex(32,out factory));
                    Marshal.ThrowExceptionForHR(Method<GetAdapterLuid>(factory,21)(factory,0,out var adapter));
                    luid=$"luid_0x{unchecked((uint)adapter.High):X8}_0x{adapter.Low:X8}";
                }
                else
                {
                    factory=Direct3DCreate9(32);
                    if(factory==IntPtr.Zero) throw new InvalidOperationException("Direct3DCreate9 returned null");
                }
                var parameters=new PresentParameters { Width=(uint)ClientSize.Width, Height=(uint)ClientSize.Height,
                    Count=1, SwapEffect=1, Window=Handle, Windowed=1 };
                createHr=mode=="d3d9ex"
                    ?Method<CreateDevice>(factory,20)(factory,0,1,Handle,0x20,ref parameters,IntPtr.Zero,out device)
                    :Method<CreateLegacyDevice>(factory,16)(factory,0,1,Handle,0x20,ref parameters,out device);
                Marshal.ThrowExceptionForHR(createHr);
                RenderGpu();
            }
            Save("shown");
            // One deferred callback separates pre-visible startup from a running message loop; no timer/retry loop.
            BeginInvoke(new Action(() =>
            {
                try { if(device!=IntPtr.Zero) RenderGpu(); Save("message-loop-once"); }
                catch(Exception ex) { Fail(ex); }
            }));
        }
        catch(Exception ex) { Fail(ex); }
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            if(mode=="gdi")
            {
                e.Graphics.Clear(Color.FromArgb(25,37,47));
                e.Graphics.FillRectangle(Brushes.OrangeRed,20,20,ClientSize.Width-40,ClientSize.Height/2-30);
                e.Graphics.FillRectangle(Brushes.CornflowerBlue,20,ClientSize.Height/2+10,ClientSize.Width-40,ClientSize.Height/2-30);
            }
            else if(device!=IntPtr.Zero) RenderGpu();
            if(++paints<=20) Save("paint");
        }
        catch(Exception ex) { Fail(ex); }
    }
    private void RenderGpu()
    {
        var clear=Method<Clear>(device,43);
        clearHr=clear(device,0,IntPtr.Zero,1,0xFF19252F,1,0); Marshal.ThrowExceptionForHR(clearHr);
        var pointer=Marshal.AllocHGlobal(Marshal.SizeOf<Rect>());
        try
        {
            Marshal.StructureToPtr(new Rect{Left=20,Top=20,Right=ClientSize.Width-20,Bottom=ClientSize.Height/2-10},pointer,false);
            clearHr=clear(device,1,pointer,1,0xFFFF4500,1,0); Marshal.ThrowExceptionForHR(clearHr);
            Marshal.StructureToPtr(new Rect{Left=20,Top=ClientSize.Height/2+10,Right=ClientSize.Width-20,Bottom=ClientSize.Height-20},pointer,false);
            clearHr=clear(device,1,pointer,1,0xFF6495ED,1,0); Marshal.ThrowExceptionForHR(clearHr);
        }
        finally { Marshal.FreeHGlobal(pointer); }
        // One bounded pixel-content check before first Present, not resource/heap profiling or a screen capture.
        if(pixelReadback==null) pixelReadback=ReadPixels();
        presentHr=Method<Present>(device,17)(device,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        Marshal.ThrowExceptionForHR(presentHr);
        if(mode=="d3d9ex")
        {
            presentExHr=Method<PresentEx>(device,121)(device,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0);
            stateHr=Method<CheckDeviceState>(device,128)(device,Handle);
        }
    }
    private void Fail(Exception ex) { error=ex.ToString(); Text="FAILED "+mode; Save("error"); }
    private object ReadPixels()
    {
        IntPtr back=IntPtr.Zero,copy=IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(Method<GetBackBuffer>(device,18)(device,0,0,0,out back));
            Marshal.ThrowExceptionForHR(Method<GetDescription>(back,12)(back,out var desc));
            if(desc.Format is not (21 or 22)) return new { UnsupportedFormat=desc.Format };
            Marshal.ThrowExceptionForHR(Method<CreateSurface>(device,36)(device,desc.Width,desc.Height,desc.Format,2,out copy,IntPtr.Zero));
            Marshal.ThrowExceptionForHR(Method<CopyTarget>(device,32)(device,back,copy));
            Marshal.ThrowExceptionForHR(Method<LockSurface>(copy,13)(copy,out var locked,IntPtr.Zero,0x10));
            try
            {
                uint Pixel(int x,int y)=>unchecked((uint)Marshal.ReadInt32(locked.Bits,y*locked.Pitch+x*4))&0x00FFFFFF;
                var bg=Pixel(10,10); var red=Pixel(40,40); var blue=Pixel(40,(int)desc.Height/2+40);
                return new { Format=desc.Format,desc.Width,desc.Height,BackgroundRgb=$"{bg:X6}",RedRgb=$"{red:X6}",BlueRgb=$"{blue:X6}",
                    ExpectedColorsMatch=bg==0x19252F&&red==0xFF4500&&blue==0x6495ED,BeforeFirstPresent=true };
            }
            finally { Marshal.ThrowExceptionForHR(Method<UnlockSurface>(copy,14)(copy)); }
        }
        catch(Exception ex) { return new { Error=ex.ToString() }; }
        finally { if(copy!=IntPtr.Zero) Marshal.Release(copy); if(back!=IntPtr.Zero) Marshal.Release(back); }
    }
    private void Save(string stage)
    {
        var cloakHr=DwmGetWindowAttribute(Handle,14,out var cloaked,4);
        var item=new
        {
        Stage=stage,Mode=mode,ProcessId=Environment.ProcessId,WindowHandle=$"0x{Handle:X}",Runtime=RuntimeInformation.FrameworkDescription,
        Dpi=DeviceDpi,ClientWidth=ClientSize.Width,ClientHeight=ClientSize.Height,PaintCount=paints,AdapterLuid=luid,
        CreateDeviceHr=mode=="gdi"?null:$"0x{createHr:X8}",ClearHr=mode=="gdi"?null:$"0x{clearHr:X8}",PresentHr=mode=="gdi"?null:$"0x{presentHr:X8}",Error=error,
        PixelReadback=pixelReadback,
        PresentExHr=mode=="d3d9ex"?$"0x{presentExHr:X8}":null,CheckDeviceStateHr=mode=="d3d9ex"?$"0x{stateHr:X8}":null,NativeVisible=IsWindowVisible(Handle),
        NativeIconic=IsIconic(Handle),FormVisible=Visible,WindowState=WindowState.ToString(),Cloaked=cloaked,CloakQueryHr=$"0x{cloakHr:X8}",
        RemoteSession=GetSystemMetrics(0x1000)!=0,
        Desktop=DesktopSnapshot(),
        HardwareDeviceRequested=mode!="gdi",WpfReferenced=false,NoTimer=true,NoResourceSampling=true,PhysicalDisplayVerified=false
        };
        File.WriteAllText(Path.Combine(output,"observations.json"),JsonSerializer.Serialize(item,new JsonSerializerOptions{WriteIndented=true}));
        if(stages.Count<32) stages.Add(item);
        File.WriteAllText(Path.Combine(output,"stages.json"),JsonSerializer.Serialize(stages,new JsonSerializerOptions{WriteIndented=true}));
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Save("closed");
        if(device!=IntPtr.Zero){Marshal.Release(device);device=IntPtr.Zero;}
        if(factory!=IntPtr.Zero){Marshal.Release(factory);factory=IntPtr.Zero;}
        base.OnFormClosed(e);
    }
    private static T Method<T>(IntPtr pointer,int slot) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer),slot*IntPtr.Size));
    private object DesktopSnapshot()
    {
        var input=OpenInputDesktop(0,false,1);
        var inputError=input==IntPtr.Zero?Marshal.GetLastWin32Error():0;
        var inputName=input==IntPtr.Zero?null:ObjectName(input);
        if(input!=IntPtr.Zero) CloseDesktop(input);
        var windowMonitor=MonitorFromWindow(Handle,2);
        var adapterMonitor=factory==IntPtr.Zero?IntPtr.Zero:Method<GetAdapterMonitor>(factory,15)(factory,0);
        var notificationHr=SHQueryUserNotificationState(out var notificationState);
        var compositionHr=DwmIsCompositionEnabled(out var composition);
        return new { WindowStation=ObjectName(GetProcessWindowStation()),ThreadDesktop=ObjectName(GetThreadDesktop(GetCurrentThreadId())),
            InputDesktop=inputName,InputDesktopError=inputError,ProcessSession=Process.GetCurrentProcess().SessionId,
            ActiveConsoleSession=WTSGetActiveConsoleSessionId(),WindowMonitor=$"0x{windowMonitor:X}",AdapterMonitor=$"0x{adapterMonitor:X}",
            MonitorsMatch=adapterMonitor==IntPtr.Zero?(bool?)null:adapterMonitor==windowMonitor,
            MonitorBounds=Screen.FromHandle(Handle).Bounds.ToString(),MonitorDevice=Screen.FromHandle(Handle).DeviceName,
            ShellNotificationHr=$"0x{notificationHr:X8}",ShellNotificationState=notificationState,
            DwmCompositionHr=$"0x{compositionHr:X8}",DwmCompositionEnabled=composition,
            D3DAdapterCount=factory==IntPtr.Zero?(uint?)null:Method<GetAdapterCount>(factory,4)(factory),
            ForegroundWindow=$"0x{GetForegroundWindow():X}",IsForeground=GetForegroundWindow()==Handle };
    }
    private static string ObjectName(IntPtr handle)
    {
        var text=new StringBuilder(256);
        return GetUserObjectInformation(handle,2,text,512,out _)?text.ToString():$"error:{Marshal.GetLastWin32Error()}";
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr GetAdapterMonitor(IntPtr self,uint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint GetAdapterCount(IntPtr self);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder info,int size,out int returned);
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct SurfaceDescription { public uint Format,Type,Usage,Pool,Multisample,Quality,Width,Height; }
    [StructLayout(LayoutKind.Sequential)] private struct LockedRect { public int Pitch; public IntPtr Bits; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBackBuffer(IntPtr self,uint chain,uint index,uint type,out IntPtr surface);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr self,out SurfaceDescription desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSurface(IntPtr self,uint width,uint height,uint format,uint pool,out IntPtr surface,IntPtr shared);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CopyTarget(IntPtr self,IntPtr source,IntPtr destination);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockSurface(IntPtr self,out LockedRect locked,IntPtr rect,uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UnlockSurface(IntPtr self);
    [StructLayout(LayoutKind.Sequential)] private struct PresentParameters
    {
        public uint Width,Height,Format,Count,Multisample,Quality,SwapEffect;
        public IntPtr Window;
        public int Windowed,AutoDepth;
        public uint DepthFormat,Flags,RefreshRate,Interval;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterLuid(IntPtr self,uint adapter,out Luid luid);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateDevice(IntPtr self,uint adapter,uint type,IntPtr focus,uint flags,ref PresentParameters parameters,IntPtr fullscreen,out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateLegacyDevice(IntPtr self,uint adapter,uint type,IntPtr focus,uint flags,ref PresentParameters parameters,out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Clear(IntPtr self,uint count,IntPtr rectangles,uint flags,uint color,float z,uint stencil);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Present(IntPtr self,IntPtr source,IntPtr destination,IntPtr overrideWindow,IntPtr dirtyRegion);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PresentEx(IntPtr self,IntPtr source,IntPtr destination,IntPtr overrideWindow,IntPtr dirtyRegion,uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CheckDeviceState(IntPtr self,IntPtr window);
    [DllImport("d3d9.dll")] private static extern int Direct3DCreate9Ex(uint version,out IntPtr factory);
    [DllImport("d3d9.dll")] private static extern IntPtr Direct3DCreate9(uint version);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window,uint attribute,out int value,uint size);
}

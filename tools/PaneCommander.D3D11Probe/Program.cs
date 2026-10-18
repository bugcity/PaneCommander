using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Standalone bounded rendering diagnostic: no WPF/product DLL, fallback, timer or settings writes.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length!=1 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0])) return 2;
        Directory.CreateDirectory(args[0]);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new ProbeForm(args[0]));
        return 0;
    }
}

internal sealed class ProbeForm : Form
{
    private readonly string output;
    private readonly List<IntPtr> owned=[];
    private readonly List<object> events=[];
    private IntPtr device,context,swap,back,backView,patch,patchView,staging;
    private string phase="startup";
    private string? error;
    private int? createHr,presentHr,removedHr;
    private uint featureLevel;
    private int frames;
    private object? adapterInfo,pixels,swapInfo;
    public ProbeForm(string output)
    {
        this.output=output;
        Text="Standalone D3D11 hardware / red-blue control";
        StartPosition=FormStartPosition.Manual; Location=new Point(120,120); ClientSize=new Size(800,600);
        FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false;
        SetStyle(ControlStyles.Opaque|ControlStyles.UserPaint,true);
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            Save("before-create");
            InitializeGpu(); Render();
            // Exactly one deferred comparison after the message loop starts; no retry loop.
            BeginInvoke(new Action(()=> { if(error==null) TryRender(); }));
        }
        catch(Exception ex) { Fail(ex); }
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e)
    {
        // At most three frames total, including shown/deferred/one native paint.
        if(device!=IntPtr.Zero && error==null && frames<3) TryRender();
    }
    private void TryRender() { try { if(frames<3) Render(); } catch(Exception ex) { Fail(ex); } }
    private IntPtr Own(IntPtr value) { if(value!=IntPtr.Zero) owned.Add(value); return value; }
    private void InitializeGpu()
    {
        phase="create-device-and-swapchain";
        var desc=new SwapDesc { Mode=new ModeDesc { Width=800,Height=600,Format=28 },
            Sample=new SampleDesc{Count=1},Usage=0x20,Count=1,Window=Handle,Windowed=1,Effect=0 };
        createHr=D3D11CreateDeviceAndSwapChain(IntPtr.Zero,1,IntPtr.Zero,0,IntPtr.Zero,0,7,ref desc,
            out swap,out device,out featureLevel,out context);
        Own(swap); Own(device); Own(context);
        Marshal.ThrowExceptionForHR(createHr.Value);
        phase="inspect-adapter";
        IntPtr dxgi=IntPtr.Zero,adapter=IntPtr.Zero;
        try
        {
            var iid=new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device,in iid,out dxgi));
            Marshal.ThrowExceptionForHR(Method<GetAdapter>(dxgi,7)(dxgi,out adapter));
            Marshal.ThrowExceptionForHR(Method<GetAdapterDesc>(adapter,8)(adapter,out var ad));
            var outputs=new List<object>();
            for(uint i=0;i<16;i++)
            {
                var hr=Method<EnumOutputs>(adapter,7)(adapter,i,out var target);
                if(hr==unchecked((int)0x887A0002)) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var outputHr=Method<GetOutputDesc>(target,7)(target,out var od);
                    Marshal.ThrowExceptionForHR(outputHr);
                    outputs.Add(new { od.DeviceName,Attached=od.Attached!=0,Monitor=$"0x{od.Monitor:X}",
                        Bounds=new {od.Bounds.Left,od.Bounds.Top,od.Bounds.Right,od.Bounds.Bottom},od.Rotation,
                        MatchesWindow=od.Monitor==MonitorFromWindow(Handle,2) });
                }
                finally { Marshal.Release(target); }
            }
            adapterInfo=new { ad.Description,ad.VendorId,ad.DeviceId,ad.SubsystemId,ad.Revision,
                Luid=$"{unchecked((uint)ad.AdapterLuid.High):X8}:{ad.AdapterLuid.Low:X8}",Outputs=outputs };
        }
        finally { if(adapter!=IntPtr.Zero) Marshal.Release(adapter); if(dxgi!=IntPtr.Zero) Marshal.Release(dxgi); }
        phase="inspect-swapchain";
        Marshal.ThrowExceptionForHR(Method<GetSwapDesc>(swap,12)(swap,out var actual));
        swapInfo=new { actual.Mode.Width,actual.Mode.Height,actual.Mode.Format,BufferFormat="R8G8B8A8_UNORM (non-sRGB)",
            RefreshNumerator=actual.Mode.Refresh.Numerator,RefreshDenominator=actual.Mode.Refresh.Denominator,
            actual.Mode.Scanline,actual.Mode.Scaling,SampleCount=actual.Sample.Count,SampleQuality=actual.Sample.Quality,
            BufferCount=actual.Count,BufferUsage=actual.Usage,Window=$"0x{actual.Window:X}",actual.Windowed,
            SwapEffect=actual.Effect,SwapEffectName="DISCARD",actual.Flags,PresentSyncInterval=1,PresentFlags=0 };
        phase="create-render-targets";
        var textureIid=new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        Marshal.ThrowExceptionForHR(Method<GetBuffer>(swap,9)(swap,0,ref textureIid,out back)); Own(back);
        Marshal.ThrowExceptionForHR(Method<CreateView>(device,9)(device,back,IntPtr.Zero,out backView)); Own(backView);
        var texture=new TextureDesc { Width=760,Height=270,Mips=1,ArraySize=1,Format=28,
            Sample=new SampleDesc{Count=1},Bind=0x20 };
        Marshal.ThrowExceptionForHR(Method<CreateTexture>(device,5)(device,ref texture,IntPtr.Zero,out patch)); Own(patch);
        Marshal.ThrowExceptionForHR(Method<CreateView>(device,9)(device,patch,IntPtr.Zero,out patchView)); Own(patchView);
        texture.Width=800; texture.Height=600; texture.Usage=3; texture.Bind=0; texture.CpuAccess=0x20000;
        Marshal.ThrowExceptionForHR(Method<CreateTexture>(device,5)(device,ref texture,IntPtr.Zero,out staging)); Own(staging);
        Save("initialized");
    }
    private void Render()
    {
        frames++;
        phase="gpu-clear-and-copy";
        var clear=Method<ClearView>(context,50);
        clear(context,backView,[25/255f,37/255f,47/255f,1]);
        clear(context,patchView,[1,69/255f,0,1]);
        Method<CopyRegion>(context,46)(context,back,0,20,20,0,patch,0,IntPtr.Zero);
        clear(context,patchView,[100/255f,149/255f,237/255f,1]);
        Method<CopyRegion>(context,46)(context,back,0,20,310,0,patch,0,IntPtr.Zero);
        phase="copy-to-staging-and-map";
        Method<CopyResource>(context,47)(context,staging,back);
        // Blocking READ Map waits for preceding GPU copy; no DO_NOT_WAIT/polling or unsynchronized reads.
        var mapHr=Method<Map>(context,14)(context,staging,0,1,0,out var mapped);
        Marshal.ThrowExceptionForHR(mapHr);
        bool matches;
        try
        {
            string Rgb(int x,int y)
            {
                var offset=checked(y*(int)mapped.RowPitch+x*4);
                return $"{Marshal.ReadByte(mapped.Data,offset):X2}{Marshal.ReadByte(mapped.Data,offset+1):X2}{Marshal.ReadByte(mapped.Data,offset+2):X2}";
            }
            var bg=Rgb(10,10); var red=Rgb(40,40); var blue=Rgb(40,340);
            matches=bg=="19252F"&&red=="FF4500"&&blue=="6495ED";
            pixels=new { BackgroundRgb=bg,RedRgb=red,BlueRgb=blue,ExpectedColorsMatch=matches,
                Format="R8G8B8A8_UNORM",MapHr=Hex(mapHr),mapped.RowPitch,BeforePresent=true,
                Synchronization="Immediate-context CopyResource followed by blocking READ Map(flags=0)" };
        }
        finally { Method<Unmap>(context,15)(context,staging,0); }
        if(!matches) throw new InvalidDataException("GPU pixel readback differs from expected colors; Present not attempted for this frame.");
        removedHr=Method<RemovedReason>(device,39)(device);
        Marshal.ThrowExceptionForHR(removedHr.Value);
        phase="present";
        presentHr=null;
        Save("before-present");
        presentHr=Method<Present>(swap,8)(swap,1,0);
        removedHr=Method<RemovedReason>(device,39)(device);
        Save("after-present");
        Marshal.ThrowExceptionForHR(presentHr.Value);
        Marshal.ThrowExceptionForHR(removedHr.Value);
    }
    private void Fail(Exception ex) { error=ex.ToString(); Text="FAILED D3D11: "+phase; Save("error"); }
    private void Save(string stage)
    {
        var cloakHr=DwmGetWindowAttribute(Handle,14,out var cloak,4);
        var input=OpenInputDesktop(0,false,1); var inputError=input==IntPtr.Zero?Marshal.GetLastWin32Error():0;
        var inputName=input==IntPtr.Zero?null:ObjectName(input); if(input!=IntPtr.Zero) CloseDesktop(input);
        var compositionHr=DwmIsCompositionEnabled(out var composition);
        var item=new { Stage=stage,Phase=phase,TimestampUtc=DateTimeOffset.UtcNow,ProcessId=Environment.ProcessId,
            ProcessPath=Environment.ProcessPath,Runtime=RuntimeInformation.FrameworkDescription,WindowHandle=$"0x{Handle:X}",
            Dpi=DeviceDpi,ClientWidth=ClientSize.Width,ClientHeight=ClientSize.Height,Frames=frames,
            DriverType="D3D_DRIVER_TYPE_HARDWARE (1), default adapter",CreateDeviceHr=Hex(createHr),FeatureLevel=$"0x{featureLevel:X4}",
            Adapter=adapterInfo,SwapChain=swapInfo,Pixels=pixels,PresentHr=Hex(presentHr),DeviceRemovedReason=Hex(removedHr),Error=error,
            WindowStation=ObjectName(GetProcessWindowStation()),ThreadDesktop=ObjectName(GetThreadDesktop(GetCurrentThreadId())),
            InputDesktop=inputName,InputDesktopError=inputError,ProcessSession=Process.GetCurrentProcess().SessionId,
            ActiveConsoleSession=WTSGetActiveConsoleSessionId(),WindowMonitor=$"0x{MonitorFromWindow(Handle,2):X}",
            MonitorDevice=Screen.FromHandle(Handle).DeviceName,MonitorBounds=Screen.FromHandle(Handle).Bounds.ToString(),
            NativeVisible=IsWindowVisible(Handle),NativeIconic=IsIconic(Handle),Cloaked=cloak,CloakQueryHr=Hex(cloakHr),
            ForegroundWindow=$"0x{GetForegroundWindow():X}",IsForeground=GetForegroundWindow()==Handle,
            DwmCompositionEnabled=composition,DwmCompositionHr=Hex(compositionHr),
            WpfReferenced=false,ProductReferenced=false,AutomaticFallback=false,NoTimer=true,MaximumFrames=3,
            ScreenVerifiedByApp=false };
        if(events.Count<40) events.Add(item);
        File.WriteAllText(Path.Combine(output,"stages.json"),JsonSerializer.Serialize(events,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"observations.json"),JsonSerializer.Serialize(item,new JsonSerializerOptions{WriteIndented=true}));
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Save("closed");
        for(int i=owned.Count-1;i>=0;i--) Marshal.Release(owned[i]);
        owned.Clear(); base.OnFormClosed(e);
    }
    private static string? Hex(int? hr)=>hr.HasValue?$"0x{hr.Value:X8}":null;
    private static T Method<T>(IntPtr p,int slot) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(p),slot*IntPtr.Size));
    private static string ObjectName(IntPtr handle) { var name=new StringBuilder(256); return GetUserObjectInformation(handle,2,name,512,out _)?name.ToString():$"error:{Marshal.GetLastWin32Error()}"; }

    [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint Numerator,Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct ModeDesc { public uint Width,Height; public Rational Refresh; public uint Format,Scanline,Scaling; }
    [StructLayout(LayoutKind.Sequential)] private struct SampleDesc { public uint Count,Quality; }
    [StructLayout(LayoutKind.Sequential)] private struct SwapDesc { public ModeDesc Mode; public SampleDesc Sample; public uint Usage,Count; public IntPtr Window; public int Windowed; public uint Effect,Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc { public uint Width,Height,Mips,ArraySize,Format; public SampleDesc Sample; public uint Usage,Bind,CpuAccess,Misc; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped { public IntPtr Data; public uint RowPitch,DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct AdapterDesc { [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Description; public uint VendorId,DeviceId,SubsystemId,Revision; public UIntPtr DedicatedVideo,DedicatedSystem,SharedSystem; public Luid AdapterLuid; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct OutputDesc { [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string DeviceName; public Rect Bounds; public int Attached; public uint Rotation; public IntPtr Monitor; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapter(IntPtr self,out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterDesc(IntPtr self,out AdapterDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumOutputs(IntPtr self,uint index,out IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDesc(IntPtr self,out OutputDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSwapDesc(IntPtr self,out SwapDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBuffer(IntPtr self,uint index,ref Guid iid,out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture(IntPtr self,ref TextureDesc desc,IntPtr data,out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateView(IntPtr self,IntPtr resource,IntPtr desc,out IntPtr view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ClearView(IntPtr self,IntPtr view,[In,MarshalAs(UnmanagedType.LPArray,SizeConst=4)] float[] color);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyRegion(IntPtr self,IntPtr destination,uint destSub,uint x,uint y,uint z,IntPtr source,uint sourceSub,IntPtr box);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopyResource(IntPtr self,IntPtr destination,IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Map(IntPtr self,IntPtr resource,uint sub,uint type,uint flags,out Mapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Unmap(IntPtr self,IntPtr resource,uint sub);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Present(IntPtr self,uint interval,uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RemovedReason(IntPtr self);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDeviceAndSwapChain(IntPtr adapter,uint driver,IntPtr software,uint flags,IntPtr levels,uint levelCount,uint sdk,ref SwapDesc desc,out IntPtr swap,out IntPtr device,out uint level,out IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window,uint attribute,out int value,uint size);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder info,int size,out int returned);
}

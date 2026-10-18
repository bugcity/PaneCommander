using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaneCommander.Performance;

// Self-process diagnostic instrumentation, not linked into the product.
// Restores every patched IAT slot; never changes registry or installed extensions.
internal sealed class EventImportProbe : IDisposable
{
    private readonly List<(IntPtr Slot,IntPtr Original,Delegate Callback)> patches=[];
    private readonly List<IntPtr> pins=[];
    private readonly List<object> calls=[];
    public IReadOnlyList<object> Calls=>calls;
    public IReadOnlyList<string> Imports=>imports;
    private readonly List<string> imports=[];
    public List<string> ModulePaths { get; }=[];
    private readonly (long Base,int Size,string Name)[] modules;
    internal EventImportProbe(bool instrument=true)
    {
        using var process=Process.GetCurrentProcess();var loaded=process.Modules.Cast<ProcessModule>().ToArray();modules=loaded.Select(m=>(m.BaseAddress.ToInt64(),m.ModuleMemorySize,m.ModuleName)).ToArray();
        foreach(var module in loaded.Where(m=>m.ModuleName.Equals("nvui.dll",StringComparison.OrdinalIgnoreCase)))
        {
            pins.Add(NativeLibrary.Load(module.FileName));ModulePaths.Add(module.FileName);if(!instrument)continue;var image=module.BaseAddress;
            var nt=Marshal.ReadInt32(image,0x3c);if(Marshal.ReadInt16(image,nt+24)!=0x20b)throw new NotSupportedException("x64 diagnostic only");
            var descriptor=image+Marshal.ReadInt32(image,nt+24+120);
            for(var d=0;d<128;d++,descriptor+=20)
            {
                var nameRva=Marshal.ReadInt32(descriptor,12);if(nameRva==0)break;var library=Marshal.PtrToStringAnsi(image+nameRva)??"";
                var lookup=Marshal.ReadInt32(descriptor);var first=Marshal.ReadInt32(descriptor,16);if(lookup==0)continue;
                for(var i=0;i<4096;i++)
                {
                    var name=Marshal.ReadInt64(image+lookup,i*8);if(name==0)break;if(name<0)continue;var function=Marshal.PtrToStringAnsi(image+(int)name+2)??"";
                    if(!function.Contains("Event",StringComparison.Ordinal)&&function!="CloseHandle")continue;imports.Add(library+"!"+function);
                    if(function is "OpenEventW" or "OpenEventA")
                    {
                        var openSlot=image+first+i*8;var openOriginal=Marshal.ReadIntPtr(openSlot);var open=Marshal.GetDelegateForFunctionPointer<OpenFunction>(openOriginal);var openWide=function.EndsWith('W');
                        OpenFunction openCallback=(access,inherit,eventName)=>{var value=open(access,inherit,eventName);var error=Marshal.GetLastPInvokeError();try{Record(module.ModuleName,function,eventName==IntPtr.Zero?"":(openWide?Marshal.PtrToStringUni(eventName):Marshal.PtrToStringAnsi(eventName)),value);}finally{SetLastError((uint)error);}return value;};
                        patches.Add((openSlot,openOriginal,openCallback));Write(openSlot,Marshal.GetFunctionPointerForDelegate(openCallback));continue;
                    }
                    if(function=="CloseHandle")
                    {
                        var closeSlot=image+first+i*8;var closeOriginal=Marshal.ReadIntPtr(closeSlot);var close=Marshal.GetDelegateForFunctionPointer<CloseFunction>(closeOriginal);
                        CloseFunction closeCallback=handle=>{var success=close(handle);var error=Marshal.GetLastPInvokeError();try{lock(calls)calls.Add(new{Module=module.ModuleName,Import=function,Handle=handle.ToInt64(),Success=success});}finally{SetLastError((uint)error);}return success;};
                        patches.Add((closeSlot,closeOriginal,closeCallback));Write(closeSlot,Marshal.GetFunctionPointerForDelegate(closeCallback));continue;
                    }
                    if(function is not ("CreateEventW" or "CreateEventA"))continue;
                    var slot=image+first+i*8;var original=Marshal.ReadIntPtr(slot);var invoke=Marshal.GetDelegateForFunctionPointer<EventFunction>(original);var wide=function.EndsWith('W');
                    EventFunction callback=(security,manual,initial,eventName)=>
                    {
                        var value=invoke(security,manual,initial,eventName);var error=Marshal.GetLastPInvokeError();
                        try
                        {
                            var text=eventName==IntPtr.Zero?"":(wide?Marshal.PtrToStringUni(eventName):Marshal.PtrToStringAnsi(eventName));
                            Record(module.ModuleName,function,text,value);
                        }
                        finally{SetLastError((uint)error);}
                        return value;
                    };
                    patches.Add((slot,original,callback));Write(slot,Marshal.GetFunctionPointerForDelegate(callback));
                }
            }
        }
    }
    private void Record(string module,string function,string? name,IntPtr handle){var frames=new IntPtr[48];var count=RtlCaptureStackBackTrace(0,(uint)frames.Length,frames,IntPtr.Zero);lock(calls)calls.Add(new{Module=module,Import=function,Name=name,Handle=handle.ToInt64(),Stack=frames.Take(count).Select(Format).ToArray()});}
    private string Format(IntPtr value){var address=value.ToInt64();var module=modules.FirstOrDefault(m=>address>=m.Base&&address<m.Base+m.Size);return module.Name is null?$"0x{address:X}":$"{module.Name}+0x{address-module.Base:X}";}
    private static void Write(IntPtr slot,IntPtr value){if(!VirtualProtect(slot,(UIntPtr)IntPtr.Size,4,out var old))throw new System.ComponentModel.Win32Exception();try{Marshal.WriteIntPtr(slot,value);}finally{VirtualProtect(slot,(UIntPtr)IntPtr.Size,old,out _);}}
    public void Dispose(){foreach(var patch in patches)Write(patch.Slot,patch.Original);patches.Clear();foreach(var pin in pins)NativeLibrary.Free(pin);pins.Clear();}
    [UnmanagedFunctionPointer(CallingConvention.StdCall,SetLastError=true)]private delegate IntPtr EventFunction(IntPtr security,int manual,int initial,IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall,SetLastError=true)]private delegate IntPtr OpenFunction(uint access,int inherit,IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall,SetLastError=true)]private delegate int CloseFunction(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool VirtualProtect(IntPtr address,UIntPtr size,uint protection,out uint previous);
    [DllImport("kernel32.dll")]private static extern void SetLastError(uint error);
    [DllImport("kernel32.dll")]private static extern ushort RtlCaptureStackBackTrace(uint skip,uint capture,[Out]IntPtr[] stack,IntPtr hash);
}

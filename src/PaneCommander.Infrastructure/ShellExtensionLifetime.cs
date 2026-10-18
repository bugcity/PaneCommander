using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaneCommander.Infrastructure;

// nvui 8.17.15.5527 leaks a HomePageEvent handle on each DLL load/unload cycle.
// Reproduced independently of Shell/RCWs. Keep one loader reference until process exit.
// No extension is disabled; no foreign event handle is closed; cache limit is one module.
internal static class ShellExtensionLifetime
{
    private static readonly object gate=new();
    private static bool inspected;
    private static IntPtr retainedModule;
    static ShellExtensionLifetime() => AppDomain.CurrentDomain.ProcessExit+=(_,_)=>Release();
    internal static void AfterBackgroundQuery()
    {
        lock(gate)
        {
            if(inspected)return;
            using var process=Process.GetCurrentProcess();
            var module=process.Modules.Cast<ProcessModule>().FirstOrDefault(m=>m.ModuleName.Equals("nvui.dll",StringComparison.OrdinalIgnoreCase));
            if(module is null){inspected=true;return;}
            var version=FileVersionInfo.GetVersionInfo(module.FileName);
            if(version.FileVersion=="8.17.15.5527"&&version.CompanyName=="NVIDIA Corporation")
            {
                if(!NativeLibrary.TryLoad(module.FileName,out retainedModule))throw new IOException("既知のShell拡張のリソース寿命を保護できないため、背景メニューを中止しました。");
            }
            inspected=true;
        }
    }
    private static void Release(){lock(gate){if(retainedModule!=IntPtr.Zero){NativeLibrary.Free(retainedModule);retainedModule=IntPtr.Zero;}}}
}

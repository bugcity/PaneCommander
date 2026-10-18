param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore
Add-Type @'
using System;using System.Runtime.InteropServices;
public static class ClipboardTransferReadOnly {
 [ComImport,Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]public interface IAsync {
 [PreserveSig]int SetAsyncMode([MarshalAs(UnmanagedType.Bool)]bool v);
 [PreserveSig]int GetAsyncMode([MarshalAs(UnmanagedType.Bool)]out bool v);
 [PreserveSig]int StartOperation(IntPtr b);
 [PreserveSig]int InOperation([MarshalAs(UnmanagedType.Bool)]out bool v);
 [PreserveSig]int EndOperation(int hr,IntPtr b,uint e);
 }
 [DllImport("ole32.dll")]public static extern int OleInitialize(IntPtr p);
 [DllImport("ole32.dll")]public static extern void OleUninitialize();
 [DllImport("ole32.dll")]public static extern int OleGetClipboard([MarshalAs(UnmanagedType.Interface)]out object d);
 public static object Read(){object d=null;Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));try{Marshal.ThrowExceptionForHR(OleGetClipboard(out d));var a=d as IAsync;if(a==null)return new{SupportsAsync=false};bool mode,active;var m=a.GetAsyncMode(out mode);var i=a.InOperation(out active);return new{SupportsAsync=true,AsyncMode=mode,InOperation=active,ModeHr=m,InOperationHr=i};}finally{if(d!=null)Marshal.ReleaseComObject(d);OleUninitialize();}}
}
'@
@{State=[ClipboardTransferReadOnly]::Read();ReadOnly=$true;ClipboardContentsRestored=$false;FixturePaths=@([Windows.Clipboard]::GetFileDropList()|Where-Object {$_ -like '*\panecommander\artifacts\shell-gui-resume\*'});Utc=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 5|Set-Content $Output
Get-Content $Output

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using PaneCommander.Core;

namespace PaneCommander.Infrastructure;

/// <summary>Reads Shell clipboard transfer intent and reports optimized copy/move completion.</summary>
[SupportedOSPlatform("windows")]
public sealed class ShellPasteData : IDisposable
{
    private IDataObject? data;
    public FileOperationKind Kind {get;}
    public uint PreferredEffect {get;private set;}=1;
    public ShellPasteData()
    {
        try
        {
            Marshal.ThrowExceptionForHR(OleGetClipboard(out var value));data=value;
            var format=Format("Preferred DropEffect");
            if(data.QueryGetData(ref format)<0){Kind=FileOperationKind.Copy;return;}
            data.GetData(ref format,out var medium);
            uint effect;
            try
            {
                if(medium.tymed!=TYMED.TYMED_HGLOBAL||GlobalSize(medium.unionmember).ToUInt64()<4)throw new IOException("Shellクリップボードの転送方式が不正です。");
                var pointer=GlobalLock(medium.unionmember);if(pointer==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
                try{effect=unchecked((uint)Marshal.ReadInt32(pointer));}finally{GlobalUnlock(medium.unionmember);}
            }
            finally{ReleaseStgMedium(ref medium);}
            PreferredEffect=effect;
            if((effect&~7u)!=0)throw new IOException($"このクリップボードの転送方式は対応していません（0x{effect:X8}）。");
            Kind=(effect&1)!=0?FileOperationKind.Copy:(effect&2)!=0?FileOperationKind.Move:throw new IOException($"このクリップボードの転送方式は対応していません（0x{effect:X8}）。");
        }
        catch{Dispose();throw;}
    }
    public void ReportCompleted()
    {
        ObjectDisposedException.ThrowIf(data is null,this);
        // IFileOperation already removed originals on a move. Do not request deletion twice.
        Write("Performed DropEffect",Kind==FileOperationKind.Move?0u:1u);
        Write("Logical Performed DropEffect",Kind==FileOperationKind.Move?2u:1u);
        if(Kind==FileOperationKind.Move)Write("Paste Succeeded",2);
    }
    private void Write(string name,uint effect)
    {
        var memory=GlobalAlloc(0x42,(UIntPtr)4);if(memory==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
        try
        {
            var pointer=GlobalLock(memory);if(pointer==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
            try{Marshal.WriteInt32(pointer,unchecked((int)effect));}finally{GlobalUnlock(memory);}
            var medium=new STGMEDIUM{tymed=TYMED.TYMED_HGLOBAL,unionmember=memory};var format=Format(name);
            try{data!.SetData(ref format,ref medium,true);memory=IntPtr.Zero;}
            catch(COMException ex){ex.Data["ShellCompletionFormat"]=name;throw;}
        }
        finally{if(memory!=IntPtr.Zero)GlobalFree(memory);}
    }
    private static FORMATETC Format(string name)=>new(){cfFormat=unchecked((short)RegisterClipboardFormat(name)),dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=-1,tymed=TYMED.TYMED_HGLOBAL};
    public void Dispose(){if(data is not null){var value=data;data=null;Marshal.ReleaseComObject(value);}}
    [DllImport("ole32.dll")]private static extern int OleGetClipboard(out IDataObject data);
    [DllImport("ole32.dll")]private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalAlloc(uint flags,UIntPtr bytes);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern UIntPtr GlobalSize(IntPtr memory);
}

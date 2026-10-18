using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PaneCommander.Infrastructure;

// Optional receipt for isolated tests. Callback item pointers are borrowed, never released here.
[ComVisible(true),ClassInterface(ClassInterfaceType.None),SupportedOSPlatform("windows")]
internal sealed class RecycleReceiptSink(Action<string> receipt) : IRecycleReceiptSink
{
    public Exception? Failure { get; private set; }
    public int PostDeleteItem(uint flags,IntPtr item,int result,IntPtr recycled)
    {
        try
        {
            Marshal.ThrowExceptionForHR(result);
            if(recycled==IntPtr.Zero)throw new IOException("Shellからごみ箱項目が返らず、ごみ箱への移動を証明できません。");
            var method=Marshal.GetDelegateForFunctionPointer<GetName>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(recycled),5*IntPtr.Size));
            Marshal.ThrowExceptionForHR(method(recycled,0x80058000,out var text));
            try{receipt(Marshal.PtrToStringUni(text)??throw new IOException("Recycle path missing"));}finally{Marshal.FreeCoTaskMem(text);}
            return 0;
        }
        catch(Exception error){Failure=error;return unchecked((int)0x80004005);}
    }
    public int StartOperations()=>0;public int FinishOperations(int result)=>0;
    public int PreRenameItem(uint flags,IntPtr item,IntPtr name)=>0;public int PostRenameItem(uint flags,IntPtr item,IntPtr name,int result,IntPtr created)=>0;
    public int PreMoveItem(uint flags,IntPtr item,IntPtr destination,IntPtr name)=>0;public int PostMoveItem(uint flags,IntPtr item,IntPtr destination,IntPtr name,int result,IntPtr created)=>0;
    public int PreCopyItem(uint flags,IntPtr item,IntPtr destination,IntPtr name)=>0;public int PostCopyItem(uint flags,IntPtr item,IntPtr destination,IntPtr name,int result,IntPtr created)=>0;
    public int PreDeleteItem(uint flags,IntPtr item)=>0;public int PreNewItem(uint flags,IntPtr destination,IntPtr name)=>0;
    public int PostNewItem(uint flags,IntPtr destination,IntPtr name,IntPtr template,uint attributes,int result,IntPtr created)=>0;
    public int UpdateProgress(uint total,uint done)=>0;public int ResetTimer()=>0;public int PauseTimer()=>0;public int ResumeTimer()=>0;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int GetName(IntPtr self,uint kind,out IntPtr text);
}
[ComVisible(true),Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IRecycleReceiptSink
{
    [PreserveSig]int StartOperations();[PreserveSig]int FinishOperations(int result);
    [PreserveSig]int PreRenameItem(uint flags,IntPtr item,IntPtr name);[PreserveSig]int PostRenameItem(uint flags,IntPtr item,IntPtr name,int result,IntPtr created);
    [PreserveSig]int PreMoveItem(uint flags,IntPtr item,IntPtr destination,IntPtr name);[PreserveSig]int PostMoveItem(uint flags,IntPtr item,IntPtr destination,IntPtr name,int result,IntPtr created);
    [PreserveSig]int PreCopyItem(uint flags,IntPtr item,IntPtr destination,IntPtr name);[PreserveSig]int PostCopyItem(uint flags,IntPtr item,IntPtr destination,IntPtr name,int result,IntPtr created);
    [PreserveSig]int PreDeleteItem(uint flags,IntPtr item);[PreserveSig]int PostDeleteItem(uint flags,IntPtr item,int result,IntPtr recycled);
    [PreserveSig]int PreNewItem(uint flags,IntPtr destination,IntPtr name);[PreserveSig]int PostNewItem(uint flags,IntPtr destination,IntPtr name,IntPtr template,uint attributes,int result,IntPtr created);
    [PreserveSig]int UpdateProgress(uint total,uint done);[PreserveSig]int ResetTimer();[PreserveSig]int PauseTimer();[PreserveSig]int ResumeTimer();
}

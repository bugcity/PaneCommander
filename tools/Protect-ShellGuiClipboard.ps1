param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
throw 'Disabled: the 2026-10-14 GUI run could not confirm clipboard restoration (CLIPBRD_E_CANT_CLOSE). Do not overwrite user clipboard using this helper until a lossless snapshot/restore method is verified.'
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Output))|Out-Null
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShellGuiClipboardBackup {
 [DllImport("ole32.dll")] public static extern int OleInitialize(IntPtr reserved);
 [DllImport("ole32.dll")] public static extern void OleUninitialize();
 [DllImport("ole32.dll")] public static extern int OleGetClipboard(out IntPtr data);
 [DllImport("ole32.dll")] public static extern int OleSetClipboard(IntPtr data);
 [DllImport("ole32.dll")] public static extern int OleFlushClipboard();
}
'@
$clipboardObject=[IntPtr]::Zero
$initialized=$false
try {
    [Runtime.InteropServices.Marshal]::ThrowExceptionForHR([ShellGuiClipboardBackup]::OleInitialize([IntPtr]::Zero));$initialized=$true
    [Runtime.InteropServices.Marshal]::ThrowExceptionForHR([ShellGuiClipboardBackup]::OleGetClipboard([ref]$clipboardObject))
    @{Captured=$true;Pid=$PID;Time=[DateTimeOffset]::UtcNow.ToString('o');ContentsLogged=$false}|ConvertTo-Json|Set-Content (Join-Path $Output 'ready.json')
    while(-not(Test-Path (Join-Path $Output 'restore'))){Start-Sleep -Milliseconds 250}
    [Runtime.InteropServices.Marshal]::ThrowExceptionForHR([ShellGuiClipboardBackup]::OleSetClipboard($clipboardObject))
    [Runtime.InteropServices.Marshal]::ThrowExceptionForHR([ShellGuiClipboardBackup]::OleFlushClipboard())
    @{Restored=$true;Flushed=$true;Time=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $Output 'restored.json')
} finally {
    if($clipboardObject -ne [IntPtr]::Zero){[void][Runtime.InteropServices.Marshal]::Release($clipboardObject)}
    if($initialized){[ShellGuiClipboardBackup]::OleUninitialize()}
}

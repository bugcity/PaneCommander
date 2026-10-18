param([Parameter(Mandatory)][int]$TargetProcessId,[Parameter(Mandatory)][long]$WindowHandle,[Parameter(Mandatory)][string]$Output,[int]$MaxMinutes=90)
$ErrorActionPreference='Stop'
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Output))|Out-Null
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ShellPopupObservation {
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
 [StructLayout(LayoutKind.Sequential)] public struct Info { public int Size; public uint Flags; public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret; public Rect CaretRect; }
 [DllImport("user32.dll",SetLastError=true)] public static extern bool GetGUIThreadInfo(uint thread,ref Info value);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
 [DllImport("user32.dll",SetLastError=true)] public static extern uint GetGuiResources(IntPtr process,uint flags);
}
'@
[uint32]$ownerProcess=0
$thread=[ShellPopupObservation]::GetWindowThreadProcessId([IntPtr]$WindowHandle,[ref]$ownerProcess)
if($ownerProcess -ne $TargetProcessId -or !$thread){throw 'Returned window does not belong to the target process.'}
$target=Get-Process -Id $TargetProcessId
$watch=[Diagnostics.Stopwatch]::StartNew()
$csv=[IO.StreamWriter]::new((Join-Path $Output 'samples.csv'))
$events=[IO.StreamWriter]::new((Join-Path $Output 'popup-events.jsonl'))
$csv.WriteLine('ElapsedMs,Stage,PrivateBytes,WorkingSet,Handles,CpuSeconds,GdiObjects,UserObjects,PopupStarts')
$lastSample=-500.0;$lastStage='';$inPopup=$false;$starts=0;$failures=0
try{
 while($watch.Elapsed.TotalMinutes -lt $MaxMinutes -and !(Test-Path -LiteralPath (Join-Path $Output 'stop'))){
  $target.Refresh();if($target.HasExited){break}
  $stagePath=Join-Path $Output 'stage.txt'
  $stage=if(Test-Path -LiteralPath $stagePath){[IO.File]::ReadAllText($stagePath).Trim()}else{'baseline'}
  $info=[ShellPopupObservation+Info]::new();$info.Size=[Runtime.InteropServices.Marshal]::SizeOf($info)
  $ok=[ShellPopupObservation]::GetGUIThreadInfo($thread,[ref]$info)
  if(!$ok){$failures++}
  $popup=$ok -and (($info.Flags -band 0x10) -ne 0) -and $info.MenuOwner.ToInt64() -eq $WindowHandle
  if($popup -ne $inPopup){
   if($popup){$starts++}
   @{ElapsedMs=$watch.Elapsed.TotalMilliseconds;Stage=$stage;Entering=$popup;Flags=$info.Flags;MenuOwner=$info.MenuOwner.ToInt64();Starts=$starts}|ConvertTo-Json -Compress|ForEach-Object {$events.WriteLine($_)}
   $events.Flush();$inPopup=$popup
  }
  if($watch.Elapsed.TotalMilliseconds-$lastSample -ge 500 -or $stage -ne $lastStage){
   $lastSample=$watch.Elapsed.TotalMilliseconds;$lastStage=$stage
   $gdi=[ShellPopupObservation]::GetGuiResources($target.Handle,0);$user=[ShellPopupObservation]::GetGuiResources($target.Handle,1)
   $csv.WriteLine(('{0},{1},{2},{3},{4},{5},{6},{7},{8}' -f $lastSample,$stage,$target.PrivateMemorySize64,$target.WorkingSet64,$target.HandleCount,$target.TotalProcessorTime.TotalSeconds,$gdi,$user,$starts));$csv.Flush()
  }
  Start-Sleep -Milliseconds 10
 }
}finally{
 $csv.Dispose();$events.Dispose();$target.Dispose()
 @{TargetProcessId=$TargetProcessId;WindowHandle=$WindowHandle;UiThread=$thread;LogicalProcessors=[Environment]::ProcessorCount;PopupStarts=$starts;GuiThreadQueryFailures=$failures;DurationSeconds=$watch.Elapsed.TotalSeconds;ReadOnlyDiagnostic=$true;GuiInputGenerated=$false;ForcedGC=$false;GuiStatePollingMilliseconds=10;ResourceSamplingMilliseconds=500;PopupDefinition='GUI_POPUPMENUMODE and hwndMenuOwner equals the validated product HWND'}|ConvertTo-Json|Set-Content (Join-Path $Output 'conditions.json')
}

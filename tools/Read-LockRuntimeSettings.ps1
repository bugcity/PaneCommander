param([string]$Output='docs/verification/session-lock/20261014')
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LockRuntimeReadOnly {
 [DllImport("user32.dll",SetLastError=true)] public static extern bool SystemParametersInfo(uint action,uint parameter,out int value,uint flags);
 [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO {public uint Size;public uint Time;}
 [DllImport("user32.dll",SetLastError=true)] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
'@
$values=@{}
foreach($pair in @(@('ScreenSaverTimeoutSeconds',14),@('ScreenSaverActive',16),@('ScreenSaverRunning',114),@('ScreenSaverSecure',118),@('BlockSendInputResets',4134))){
    $value=0;$ok=[LockRuntimeReadOnly]::SystemParametersInfo($pair[1],0,[ref]$value,0)
    $values[$pair[0]]=@{Success=$ok;Value=$value;Error=$(if($ok){0}else{[Runtime.InteropServices.Marshal]::GetLastWin32Error()})}
}
$last=[LockRuntimeReadOnly+LASTINPUTINFO]::new();$last.Size=8
$ok=[LockRuntimeReadOnly]::GetLastInputInfo([ref]$last)
$values['LastInputInfo']=@{Success=$ok;Tick32=$last.Time;CurrentTick32=([Environment]::TickCount64 -band 0xffffffff)}
$values['Collected']=[DateTimeOffset]::UtcNow.ToString('o')
$values|ConvertTo-Json -Depth 5|Set-Content (Join-Path $Output 'runtime-settings.json')
& powercfg /a 2>&1|Set-Content (Join-Path $Output 'available-sleep-states.txt')
Get-Content (Join-Path $Output 'runtime-settings.json')

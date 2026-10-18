param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$directory=[IO.Path]::GetFullPath($Output)
if(-not(Test-Path (Join-Path $directory 'samples.csv')) -or (Test-Path (Join-Path $directory 'idle-segment.json'))){throw 'Active new sampler output required'}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RedrawLastInput {
 [StructLayout(LayoutKind.Sequential)]public struct Info {public uint Size;public uint Tick;}
 [DllImport("user32.dll")]public static extern bool GetLastInputInfo(ref Info value);
 public static uint Read(){var value=new Info{Size=8};if(!GetLastInputInfo(ref value))throw new InvalidOperationException("Last input query failed");return value.Tick;}
}
'@
$result=[ordered]@{WarmupSeconds=30;IdleSeconds=60;ForcedGc=$false;InjectedInput=$false;WarmupStartUtc=[DateTimeOffset]::UtcNow.ToString('o')}
'post-ops-warmup'|Set-Content (Join-Path $directory 'stage.txt')
Start-Sleep -Seconds 30
$result.IdleStartUtc=[DateTimeOffset]::UtcNow.ToString('o')
$result.LastInputAtIdleStart=[RedrawLastInput]::Read()
'post-ops-idle-60'|Set-Content (Join-Path $directory 'stage.txt')
Start-Sleep -Seconds 60
$result.LastInputAtIdleEnd=[RedrawLastInput]::Read()
$result.IdleEndUtc=[DateTimeOffset]::UtcNow.ToString('o')
$result.InputTickUnchanged=$result.LastInputAtIdleStart -eq $result.LastInputAtIdleEnd
'post-ops-ended'|Set-Content (Join-Path $directory 'stage.txt')
$result|ConvertTo-Json|Set-Content (Join-Path $directory 'idle-segment.json')
'done'|Set-Content (Join-Path $directory 'stop')

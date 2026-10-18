param([switch]$FreeStacks)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repo ('docs/verification/shell-lifetime-20261016/'+$(if($FreeStacks){'native-heap-free'}else{'native-heap'}))
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'The diagnostic target must not be elevated.'}
if(!(Test-Path -LiteralPath (Join-Path $output 'ready.json'))){throw 'Elevated recorder has not reported ready.'}
if(Test-Path -LiteralPath (Join-Path $output 'restoration.json')){throw 'Recorder already ended; do not run more tests.'}
$exe=Join-Path $repo 'tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe'
$run=Join-Path $output 'file-phases'
$blocks=if($FreeStacks){2}else{1};$count=if($FreeStacks){50}else{100};$idle=if($FreeStacks){5}else{10}
$process=Start-Process -FilePath $exe -ArgumentList @('shell-phases','file',('"'+$run+'"'),$blocks,$count,$idle,'stream','Default') -PassThru -WindowStyle Hidden
@{ProcessId=$process.Id;Started=(Get-Date).ToString('o');Elevated=$false;Executable=$exe;ExecutableHash=(Get-FileHash $exe).Hash;Warmup=20;Measured=100;Blocks=$blocks;CountPerBlock=$count;IdleSeconds=$idle;ClipboardWritten=$false;CommandInvoked=$false;ForcedGC=$false}|ConvertTo-Json|Set-Content (Join-Path $output 'target-started.json')
$process.WaitForExit()
@{ProcessId=$process.Id;ExitCode=$process.ExitCode;Completed=(Get-Date).ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $output 'target-complete.json')
if($process.ExitCode -ne 0){throw 'Target diagnostic failed.'}

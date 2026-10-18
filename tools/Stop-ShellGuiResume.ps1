param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$absolute=[IO.Path]::GetFullPath($Root,$repo)
if([IO.Path]::GetDirectoryName($absolute) -ne (Join-Path $repo 'artifacts/shell-gui-resume') -or [IO.Path]::GetFileName($absolute) -notmatch '^(Default|SoftwareOnly)-[a-f0-9]{32}$'){throw 'Owned fixture required'}
$record=Get-Content (Join-Path $absolute 'verification.json') -Raw|ConvertFrom-Json
$owned=Get-CimInstance Win32_Process -Filter ('ProcessId='+$record.ProcessId)
if($owned){
    if($owned.Name -ne 'PaneCommander.App.exe' -or -not $owned.CommandLine.Contains((Join-Path $absolute 'profile'))){throw 'Process identity mismatch'}
    $ready=Get-Content (Join-Path $absolute 'diagnostics/ready.json') -Raw|ConvertFrom-Json
    $signal=[Threading.EventWaitHandle]::OpenExisting($ready.eventName)
    try{[void]$signal.Set()}finally{$signal.Dispose()}
    $process=Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue
    if($process -and -not $process.WaitForExit(10000)){throw 'Graceful diagnostic shutdown did not complete; no forced termination performed'}
}
@{OwnedProcessStopped=$true;GuiInput=$false;Method='Existing opt-in diagnostic completion event'}|ConvertTo-Json|Set-Content (Join-Path $absolute 'shutdown.json')

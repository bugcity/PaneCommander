param([ValidateSet('Default','SoftwareOnly')][string]$Mode='SoftwareOnly')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$probe=Join-Path $repo 'tools/PaneCommander.DropProbe/bin/Release/net10.0'
if(-not(Test-Path -LiteralPath (Join-Path $probe 'PaneCommander.DropProbe.exe'))){throw 'Build the test-only DropProbe first'}
$root=Join-Path $repo ('artifacts/dnd-destination/'+$Mode+'-'+[Guid]::NewGuid().ToString('N'))
$profile=Join-Path $root 'profile'
[IO.Directory]::CreateDirectory($profile)|Out-Null
foreach($side in @('left','right')){
  $folder=Join-Path $root $side
  [IO.Directory]::CreateDirectory((Join-Path $folder '50-subdirectory'))|Out-Null
  [IO.File]::WriteAllText((Join-Path $folder '50-subdirectory/sentinel.txt'),'subdirectory must remain unchanged')
  [IO.File]::WriteAllText((Join-Path $folder '10-normal.txt'),'target row must remain unchanged')
  foreach($file in @('PaneCommander.DropProbe.dll','PaneCommander.DropProbe.runtimeconfig.json','PaneCommander.DropProbe.deps.json')){Copy-Item -LiteralPath (Join-Path $probe $file) -Destination $folder}
  Copy-Item -LiteralPath (Join-Path $probe 'PaneCommander.DropProbe.exe') -Destination (Join-Path $folder '20-probe.exe')
  [IO.File]::WriteAllText((Join-Path $folder '30-probe.cmd'),'@echo launch detected> "%~dp0script-launched.marker"')
  [IO.File]::WriteAllText((Join-Path $folder '31-probe.ps1'),'[IO.File]::WriteAllText((Join-Path $PSScriptRoot "ps1-launched.marker"), "launch detected")')
  $shell=New-Object -ComObject WScript.Shell
  try {$link=$shell.CreateShortcut((Join-Path $folder '40-probe.lnk'));$link.TargetPath=Join-Path $folder '20-probe.exe';$link.Arguments='--shortcut';$link.Save()}
  finally {if($null -ne $link){[Runtime.InteropServices.Marshal]::ReleaseComObject($link)|Out-Null};[Runtime.InteropServices.Marshal]::ReleaseComObject($shell)|Out-Null}
  $calibration=Start-Process -FilePath (Join-Path $folder '20-probe.exe') -ArgumentList '--calibrate' -WindowStyle Hidden -PassThru -Wait
  if($calibration.ExitCode -ne 0 -or -not(Test-Path -LiteralPath (Join-Path $folder 'calibration.marker'))){throw 'Launch detector calibration failed'}
}
[IO.File]::WriteAllText((Join-Path $root 'left/00-payload.txt'),'PaneCommander DnD payload; identical contents for every target row')
[IO.Directory]::CreateDirectory((Join-Path $root 'explorer-source'))|Out-Null
[IO.File]::WriteAllText((Join-Path $root 'explorer-source/01-explorer.txt'),'Explorer incoming DnD payload')
@{RenderingMode=$(if($Mode -eq 'SoftwareOnly'){0}else{1})}|ConvertTo-Json|Set-Content (Join-Path $profile 'settings.json')
@{Version=1;Left=@{Tabs=@(@{Path=(Join-Path $root 'left');SortProperty='Name';SortDescending=$false});SelectedIndex=0};Right=@{Tabs=@(@{Path=(Join-Path $root 'right');SortProperty='Name';SortDescending=$false});SelectedIndex=0};ActiveSide=0;Layout=@{Left=20;Top=20;Width=1600;Height=900;Maximized=$false;LeftPaneFraction=.5;SidebarWidth=120}}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $profile 'session.json')
$baseline=@(Get-ChildItem -LiteralPath (Join-Path $root 'left'),(Join-Path $root 'right') -File -Recurse | Get-FileHash | Select-Object Path,Hash)
$product=Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.exe'
$priorTrace=$env:PANECOMMANDER_DND_TRACE
try {
  $env:PANECOMMANDER_DND_TRACE=Join-Path $root 'drop-trace.jsonl'
  $process=Start-Process -FilePath $product -ArgumentList @('--profile-directory',('"'+$profile+'"')) -WindowStyle Hidden -PassThru
} finally {$env:PANECOMMANDER_DND_TRACE=$priorTrace}
@{Root=$root;ProcessId=$process.Id;Mode=$Mode;Baseline=$baseline;ProductHash=(Get-FileHash $product).Hash;LaunchDetectionCalibrated=$true;ResourceDiagnostics=$false}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'verification.json')
$root

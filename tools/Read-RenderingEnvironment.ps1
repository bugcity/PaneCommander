param([Parameter(Mandatory)][string]$Output,[Parameter(Mandatory)][string]$Fixture)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$outputPath=[IO.Path]::GetFullPath($Output,$repo)
if(Test-Path -LiteralPath $outputPath){throw 'Use a fresh evidence directory'}
[IO.Directory]::CreateDirectory($outputPath)|Out-Null
$fixturePath=[IO.Path]::GetFullPath($Fixture,$repo)
if([IO.Path]::GetDirectoryName($fixturePath) -ne (Join-Path $repo 'artifacts/shell-gui-resume') -or [IO.Path]::GetFileName($fixturePath) -notmatch '^(Default|SoftwareOnly)-[a-f0-9]{32}$'){throw 'Owned fixture required'}
$record=Get-Content (Join-Path $fixturePath 'verification.json') -Raw|ConvertFrom-Json
$result=[ordered]@{CollectedUtc=[DateTimeOffset]::UtcNow.ToString('o');ReadOnly=$true;OsSettingsChanged=$false;ClipboardWritten=$false;Fixture=$Fixture}
try{$result.VideoControllers=@(Get-CimInstance Win32_VideoController|Select-Object Name,DriverVersion,DriverDate,Status,VideoModeDescription,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)}catch{$result.VideoControllerError=$_.Exception.Message}
try{$result.DisplayDrivers=@(Get-CimInstance Win32_PnPSignedDriver -Filter "DeviceClass='DISPLAY'"|Select-Object DeviceName,DriverVersion,DriverDate,Manufacturer,InfName,IsSigned)}catch{$result.DisplayDriverError=$_.Exception.Message}
try{
 $owned=Get-CimInstance Win32_Process -Filter ('ProcessId='+$record.ProcessId)
 if($owned){
  if($owned.Name -ne 'PaneCommander.App.exe' -or -not $owned.CommandLine.Contains((Join-Path $fixturePath 'profile'))){throw 'Owned process identity mismatch'}
  $process=Get-Process -Id $record.ProcessId
  $result.ProductProcessId=$process.Id
  $result.RenderingModules=@($process.Modules|Where-Object {$_.ModuleName -match '^(d3d|dxgi|dwm|milcore|wpfgfx|Presentation|igd|nv)' }|ForEach-Object {[pscustomobject]@{Name=$_.ModuleName;Path=$_.FileName;Version=$_.FileVersionInfo.FileVersion}})
 }else{$result.ProductProcessExited=$true}
}catch{$result.ModuleError=$_.Exception.Message}
$startTime=(Get-Date).AddHours(-12)
$queries=@()
foreach($provider in @('Display','Microsoft-Windows-Dwm-Core','Microsoft-Windows-DxgKrnl')){
 try{
  $events=@(Get-WinEvent -FilterHashtable @{LogName='System';ProviderName=$provider;StartTime=$startTime} -MaxEvents 50 -ErrorAction Stop|Select-Object TimeCreated,ProviderName,Id,LevelDisplayName)
  $queries+=@{Log='System';Provider=$provider;Events=$events}
 }catch{$queries+=@{Log='System';Provider=$provider;QueryError=$_.Exception.Message;ErrorId=$_.FullyQualifiedErrorId}}
}
$result.EventQueries=$queries
$logs=@()
foreach($log in @('Microsoft-Windows-Dwm-Core/Operational','Microsoft-Windows-DxgKrnl/Operational')){
 try{$logs+=Get-WinEvent -ListLog $log -ErrorAction Stop|Select-Object LogName,IsEnabled,RecordCount}
 catch{$logs+=@{LogName=$log;QueryError=$_.Exception.Message}}
}
$result.DiagnosticLogAvailability=$logs
$result.Note='Module presence does not identify rendering allocation owner or fault origin. Missing/disabled logs do not prove absence of a rendering fault.'
$result|ConvertTo-Json -Depth 8|Set-Content (Join-Path $outputPath 'rendering-environment.json')

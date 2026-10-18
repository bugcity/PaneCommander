param([string]$Output='artifacts/shell-context-performance',[switch]$IsolateTypes,[switch]$LifetimeDiagnosis,[switch]$DesktopEnvironment,[string]$NativeModulePath,[string[]]$Scenarios)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$outputPath=[IO.Path]::GetFullPath($Output,$repo)
if(Test-Path -LiteralPath $outputPath){throw 'Use a new output directory to preserve prior evidence.'}
[IO.Directory]::CreateDirectory($outputPath)|Out-Null
$exe=Join-Path $repo 'tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe'
$product=Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.dll'
$before=(Get-FileHash -LiteralPath $product).Hash
if($NativeModulePath){$env:PANECOMMANDER_DIAGNOSTIC_MODULE=$NativeModulePath}
foreach($run in $(if($Scenarios){$Scenarios}elseif($NativeModulePath){@('module-cycle')}elseif($DesktopEnvironment){@('environment')}elseif($LifetimeDiagnosis){@('baseline','acquire-only','minimal','product','default-verb','product-pin','product-probe')}elseif($IsolateTypes){@('file','directory','multiple','background')}else{1..3})){
    $runPath=Join-Path $outputPath "run-$run"
    $arguments=if($Scenarios){@('shell-menu-type',$run,('"'+$runPath+'"'))}elseif($NativeModulePath){@('shell-lifetime',$run,('"'+$runPath+'"'))}elseif($DesktopEnvironment){@('desktop-environment',('"'+$runPath+'"'))}elseif($LifetimeDiagnosis){@('shell-lifetime',$run,('"'+$runPath+'"'))}elseif($IsolateTypes){@('shell-menu-type',$run,('"'+$runPath+'"'))}else{@('shell-menu',('"'+$runPath+'"'))}
    $process=Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if(-not $process.WaitForExit(120000)){throw 'Measurement did not complete within 120 seconds; inspect the owned process.'}
    if($process.ExitCode -ne 0){throw "Measurement failed: $($process.ExitCode)"}
}
@{ProductHashBefore=$before;ProductHashAfter=(Get-FileHash -LiteralPath $product).Hash;OS=(Get-CimInstance Win32_OperatingSystem|Select-Object Caption,Version,BuildNumber);Timestamp=(Get-Date).ToString('o');ActualPopupMeasured=$false;ForcedGC=$false}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $outputPath 'environment.json')
$outputPath

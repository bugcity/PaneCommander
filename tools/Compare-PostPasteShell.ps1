$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repo 'docs/verification/paste-authorized-20261015/shell-acquire'
if(Test-Path -LiteralPath $output){throw 'Preserve existing evidence; output already exists.'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$exe=Join-Path $repo 'tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe'
$product=Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.dll'
$hash=(Get-FileHash -LiteralPath $product).Hash
$manifest=@(Get-ChildItem -LiteralPath (Split-Path $exe) -File|Where-Object Extension -in '.dll','.exe','.json'|ForEach-Object {@{Path=$_.FullName;Hash=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$manifest|ConvertTo-Json|Set-Content (Join-Path $output 'binary-manifest.json')
foreach($repeat in 1..3){
 foreach($scenario in @('baseline','file','directory','multiple','background')){
  $run=Join-Path $output "$scenario-$repeat"
  & $exe shell-menu-type-count $scenario $run 420
  if($LASTEXITCODE -ne 0){throw "Shell diagnostic failed: $scenario $repeat"}
  Write-Output "Completed acquire: $scenario $repeat $(Get-Date -Format HH:mm:ss)"
 }
}
if((Get-FileHash -LiteralPath $product).Hash -ne $hash){throw 'Product changed during Shell measurements.'}
if(@($manifest|Where-Object {(Get-FileHash -LiteralPath $_.Path).Hash -ne $_.Hash}).Count){throw 'Diagnostic binaries changed.'}
@{Finished=(Get-Date).ToString('o');ProductHash=$hash;FrozenBinariesUnchanged=$true;ForcedGC=$false;WarmupIterations=20;MeasuredIterations=400;ActualPopup=$false;BaselineDelayMilliseconds=50;IdleBeforeSeconds=2;IdleAfterSeconds=2;GPU=@(Get-CimInstance Win32_VideoController|Select-Object Name,DriverVersion)}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output 'conditions.json')

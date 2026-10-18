param([string]$Output='docs/verification/shell-lifetime-20261016/exploratory')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$outputPath=[IO.Path]::GetFullPath((Join-Path $repo $Output))
if(Test-Path -LiteralPath $outputPath){throw 'Preserve previous evidence; output exists.'}
[IO.Directory]::CreateDirectory($outputPath)|Out-Null
$exe=Join-Path $repo 'tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe'
$product=Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.dll'
$hash=(Get-FileHash $product).Hash
$cases=@(@('baseline','legacy'),@('baseline','stream'),@('file','legacy'),@('file','stream'),@('directory','stream'),@('multiple','stream'),@('host-only','stream'),@('background','stream'))
@{Started=(Get-Date).ToString('o');ProductHash=$hash;DiagnosticHash=(Get-FileHash $exe).Hash;Blocks=3;CountPerBlock=100;IdleSeconds=10;Warmup=20;ForcedGC=$false;Popup=$false;Mode='Default';Purpose='Exploratory sampler and lifetime controls; not product GUI acceptance'}|ConvertTo-Json|Set-Content (Join-Path $outputPath conditions.json)
foreach($case in $cases){
 $kind=$case[0];$logging=$case[1];$run=Join-Path $outputPath "$kind-$logging"
 & $exe shell-phases $kind $run 3 100 10 $logging Default
 if($LASTEXITCODE -ne 0){throw "Phase diagnostic failed: $kind $logging"}
 Write-Output "Completed $kind $logging $(Get-Date -Format HH:mm:ss)"
}
if((Get-FileHash $product).Hash -ne $hash){throw 'Product changed.'}
@{Completed=(Get-Date).ToString('o');ProductUnchanged=$true}|ConvertTo-Json|Set-Content (Join-Path $outputPath completed.json)

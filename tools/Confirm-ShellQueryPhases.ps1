$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repo 'docs/verification/shell-lifetime-20261016/query-confirmation'
if(Test-Path -LiteralPath $output){throw 'Output exists; preserve evidence.'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$exe=Join-Path $repo 'tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe'
$dll=Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.dll'
$hash=(Get-FileHash $exe).Hash;$product=(Get-FileHash $dll).Hash
@{Started=(Get-Date).ToString('o');DiagnosticHash=$hash;ProductHash=$product;Mode='Default';Popup=$false;ForcedGC=$false;Blocks=3;CountPerBlock=100;IdleSeconds=10;Warmup=20;Repetitions=3;Purpose='Confirm QueryContextMenu boundary, not GUI rendering comparison'}|ConvertTo-Json|Set-Content (Join-Path $output conditions.json)
foreach($repeat in 1..3){
 $cases=if($repeat -eq 2){@('file','file-context')}else{@('file-context','file')}
 foreach($kind in $cases){
  & $exe shell-phases $kind (Join-Path $output "$kind-$repeat") 3 100 10 stream Default
  if($LASTEXITCODE -ne 0){throw "Failed $kind $repeat"}
  Write-Output "Completed $kind $repeat $(Get-Date -Format HH:mm:ss)"
 }
}
if((Get-FileHash $exe).Hash -ne $hash -or (Get-FileHash $dll).Hash -ne $product){throw 'Binary changed.'}
@{Completed=(Get-Date).ToString('o');FrozenBinariesUnchanged=$true}|ConvertTo-Json|Set-Content (Join-Path $output completed.json)

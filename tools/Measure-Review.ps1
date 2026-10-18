param([string]$Output='artifacts/review-performance-final',[string]$ReuseBaseline='',[switch]$CompleteFreshBaseline)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$root=[IO.Path]::GetFullPath($Output,$repo)
if($CompleteFreshBaseline) {
    if(!(Test-Path (Join-Path $root 'completed.json')) -or !(Get-Content (Join-Path $root 'after-repeat/integrity.json') -Raw | ConvertFrom-Json).FrozenBinariesUnchanged) {throw 'After measurements must complete first'}
    foreach($entry in (Get-Content (Join-Path $root 'after-repeat/binary-manifest.json') -Raw | ConvertFrom-Json)) {if((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.SHA256) {throw 'Frozen binaries changed'}}
    & (Join-Path $PSScriptRoot 'Compare-Rendering.ps1') -Output (Join-Path $root 'before-repeat') -Scenarios T6,T8 -Repetitions 3 -DriverExecutable 'artifacts/review-baseline/driver/PaneCommander.Performance.exe'
    if($LASTEXITCODE -ne 0) {throw 'Fresh baseline repeat failed'}
    @{Finished=(Get-Date -Format o);Completed=$true;FreshRepeatBaselineCompleted=$true} | ConvertTo-Json | Set-Content (Join-Path $root 'completed.json') -Encoding utf8
    exit 0
}
if(Test-Path -LiteralPath $root) {throw 'Use a new output directory'}
[IO.Directory]::CreateDirectory($root) | Out-Null
$baseline=Join-Path $repo 'artifacts/review-baseline/app/PaneCommander.App.dll'
$oldManifest=Get-Content (Join-Path $repo 'artifacts/m3-product-verified/binary-manifest.json') -Raw | ConvertFrom-Json
$expected=($oldManifest | Where-Object {$_.Path -like '*PaneCommander.App.dll'}).SHA256
if((Get-FileHash $baseline -Algorithm SHA256).Hash -ne $expected) {throw 'Baseline is not the M3 measured product'}
@{BaselineAppSHA256=$expected;HistoricalBaseline='artifacts/m3-product-verified';HistoricalRepeat='docs/verification/m3/repeat';FreshBaselineHandlesRepetitions=1;HistoricalRepetitions=3;AfterRepetitions=3;ForcedGc=$false} | ConvertTo-Json | Set-Content (Join-Path $root 'protocol.json') -Encoding utf8
if($ReuseBaseline) {
    $reuse=[IO.Path]::GetFullPath($ReuseBaseline,$repo)
    foreach($name in @('before-handles','before-scroll')) {
        $prior=Join-Path $reuse $name
        if(!(Get-Content (Join-Path $prior 'integrity.json') -Raw | ConvertFrom-Json).FrozenBinariesUnchanged) {throw 'Baseline measurements are incomplete'}
        foreach($entry in (Get-Content (Join-Path $prior 'binary-manifest.json') -Raw | ConvertFrom-Json)) {if((Get-FileHash -LiteralPath $entry.Path -Algorithm SHA256).Hash -ne $entry.SHA256) {throw 'Baseline binaries changed'}}
        Copy-Item -LiteralPath $prior -Destination (Join-Path $root $name) -Recurse
    }
    @{BaselineSource=$reuse;Reason='Reuse completed baseline; only final product selection notification changed'} | ConvertTo-Json | Set-Content (Join-Path $root 'baseline-reuse.json') -Encoding utf8
} else {
    & (Join-Path $PSScriptRoot 'Measure-M3Product.ps1') -Output (Join-Path $root 'before-handles') -Scenarios two,six-restored,six-visited,large,idle -Repetitions 1 -IdleSeconds 10 -ProductExecutable 'artifacts/review-baseline/app/PaneCommander.App.exe'
    if($LASTEXITCODE -ne 0) {throw 'Baseline handles failed'}
    & (Join-Path $PSScriptRoot 'Compare-Rendering.ps1') -Output (Join-Path $root 'before-scroll') -Scenarios T9 -Repetitions 3 -DriverExecutable 'artifacts/review-baseline/driver/PaneCommander.Performance.exe'
    if($LASTEXITCODE -ne 0) {throw 'Baseline scroll failed'}
}
& (Join-Path $PSScriptRoot 'Compare-Rendering.ps1') -Output (Join-Path $root 'before-navigation') -Scenarios T7 -Repetitions 3 -DriverExecutable 'artifacts/review-baseline/driver/PaneCommander.Performance.exe'
if($LASTEXITCODE -ne 0) {throw 'Baseline navigation failed'}
& (Join-Path $PSScriptRoot 'Measure-M3Product.ps1') -Output (Join-Path $root 'after-product') -Repetitions 3
if($LASTEXITCODE -ne 0) {throw 'Review product failed'}
& (Join-Path $PSScriptRoot 'Compare-Rendering.ps1') -Output (Join-Path $root 'after-repeat') -Scenarios T6,T7,T8,T9,R1,R2,R3 -Repetitions 3
if($LASTEXITCODE -ne 0) {throw 'Review repeat failed'}
& (Join-Path $PSScriptRoot 'Compare-Rendering.ps1') -Output (Join-Path $root 'before-repeat') -Scenarios T6,T8 -Repetitions 3 -DriverExecutable 'artifacts/review-baseline/driver/PaneCommander.Performance.exe'
if($LASTEXITCODE -ne 0) {throw 'Fresh baseline repeat failed'}
@{Finished=(Get-Date -Format o);Completed=$true} | ConvertTo-Json | Set-Content (Join-Path $root 'completed.json') -Encoding utf8

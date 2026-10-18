param([string]$Output='artifacts/link-final')
$ErrorActionPreference='Stop'
# A short product regression probe, not a replacement for the 3-repeat rendering study.
& (Join-Path $PSScriptRoot 'Verify-ReviewRestart.ps1') -Output (Join-Path $Output 'restart')
if($LASTEXITCODE -ne 0) {throw 'Restart regression failed'}
& (Join-Path $PSScriptRoot 'Measure-M3Product.ps1') -Output (Join-Path $Output 'smoke') -Scenarios @('two','six-visited') -Repetitions 1 -ObservationSeconds 10
if($LASTEXITCODE -ne 0) {throw 'Product smoke measurement failed'}

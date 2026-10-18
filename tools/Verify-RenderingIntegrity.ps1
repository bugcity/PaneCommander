param([string]$Output = 'artifacts/render-comparison-controlled')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$outputPath = [IO.Path]::GetFullPath($Output, $repo)
$environment = Get-Content -LiteralPath (Join-Path $outputPath 'environment.json') -Raw | ConvertFrom-Json
$binaries = Get-Content -LiteralPath (Join-Path $outputPath 'binary-manifest-during.json') -Raw | ConvertFrom-Json
$approved = Get-Content -LiteralPath (Join-Path $repo 'docs/verification/design-approved/source-sha256.json') -Raw | ConvertFrom-Json
$sourceResults = @($approved | Where-Object { $_.path -like 'src\*' -or $_.path -like 'tests\*' } | ForEach-Object {
    $filePath = Join-Path $repo $_.path
    $current = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
    [pscustomobject]@{Path=$_.path;Expected=$_.sha256;Actual=$current;Unchanged=($current -eq $_.sha256)}
})
$binaryResults = @($binaries | ForEach-Object {
    $current = (Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash
    [pscustomobject]@{Path=$_.Path;Expected=$_.SHA256;Actual=$current;Unchanged=($current -eq $_.SHA256)}
})
$powerPlan = (& powercfg /getactivescheme) -join ''
$result = [pscustomobject]@{
    Date=(Get-Date -Format o)
    ProductAndTestsUnchanged=(@($sourceResults | Where-Object { !$_.Unchanged }).Count -eq 0)
    FrozenBinariesUnchanged=(@($binaryResults | Where-Object { !$_.Unchanged }).Count -eq 0)
    PowerPlanUnchanged=($powerPlan.Trim() -eq $environment.PowerPlan.Trim())
    PowerPlan=$powerPlan
    GPU=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)
    SourceResults=$sourceResults
    BinaryResults=$binaryResults
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'final-integrity.json') -Encoding utf8
if (!$result.ProductAndTestsUnchanged -or !$result.FrozenBinariesUnchanged -or !$result.PowerPlanUnchanged) { throw 'Rendering comparison integrity check failed' }
Write-Output "Integrity OK: $($sourceResults.Count) product/test files, $($binaryResults.Count) binary/config files; power plan unchanged."

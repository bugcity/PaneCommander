param(
    [string]$Output = 'artifacts/render-comparison-controlled',
    [string[]]$Scenarios = @('T1','T2','T3','T4','T5','T6','T7','T8','T9'),
    [int]$Repetitions = 3,
    [string]$DriverExecutable = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$outputPath = [IO.Path]::GetFullPath($Output, $repo)
$fixturePath = Join-Path $repo 'artifacts/render-comparison-fixture'
foreach ($folder in @('normal/child','large')) { [IO.Directory]::CreateDirectory((Join-Path $fixturePath $folder)) | Out-Null }
foreach ($index in 1..255) { $path=Join-Path $fixturePath ('normal/item-{0:D5}.txt' -f $index); if (!(Test-Path -LiteralPath $path)) {[IO.File]::WriteAllText($path,'')} }
foreach ($index in 1..256) { $path=Join-Path $fixturePath ('normal/child/child-{0:D5}.txt' -f $index); if (!(Test-Path -LiteralPath $path)) {[IO.File]::WriteAllText($path,'')} }
foreach ($index in 1..10000) { $path=Join-Path $fixturePath ('large/item-{0:D5}.txt' -f $index); if (!(Test-Path -LiteralPath $path)) {[IO.File]::WriteAllText($path,'')} }
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$monitor = Join-Path $repo 'tools/PaneCommander.RenderMonitor/bin/Release/net10.0-windows/PaneCommander.RenderMonitor.exe'
$driver = Join-Path $repo 'tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe'
if ($DriverExecutable) { $driver = [IO.Path]::GetFullPath($DriverExecutable, $repo) }
$binaryManifest = @(Get-ChildItem -LiteralPath (Split-Path $driver),(Split-Path $monitor) -File | Where-Object Extension -in '.dll','.exe','.json' | ForEach-Object {
    [pscustomobject]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$binaryManifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $outputPath 'binary-manifest.json') -Encoding utf8
@{Started=(Get-Date -Format o);PowerPlan=((& powercfg /getactivescheme) -join '');GPU=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath 'environment.json') -Encoding utf8
$manifest = @()
foreach ($scenario in $Scenarios) {
    foreach ($repeat in 1..$Repetitions) {
        foreach ($mode in @('A','B')) {
            $runOutput = Join-Path $outputPath "$scenario-$mode-$repeat"
            if (Test-Path -LiteralPath (Join-Path $runOutput 'monitor-summary.json')) { Write-Output "Already complete: $scenario $mode $repeat"; continue }
            & $monitor $driver $scenario $fixturePath $runOutput $mode
            if ($LASTEXITCODE -ne 0) { throw "Comparison failed: $scenario $mode $repeat" }
            $manifest += [pscustomobject]@{Scenario=$scenario;Mode=$mode;Repeat=$repeat;Finished=(Get-Date -Format o);Directory=$runOutput}
            $manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $outputPath 'run-order.json') -Encoding utf8
            Write-Output "Completed: $scenario $mode $repeat $(Get-Date -Format HH:mm:ss)"
        }
    }
}
$changed = @($binaryManifest | Where-Object {(Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256})
if ($changed.Count) { throw 'Driver or monitor binaries changed during measurements.' }
@{FrozenBinariesUnchanged=$true;Finished=(Get-Date -Format o);PowerPlan=((& powercfg /getactivescheme) -join '');GPU=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath 'integrity.json') -Encoding utf8

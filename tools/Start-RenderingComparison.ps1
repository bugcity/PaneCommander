param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$outputPath = [IO.Path]::GetFullPath($Output, $repo)
if (Test-Path -LiteralPath $outputPath) { throw 'Use a new output directory to keep trial groups independent.' }
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$driver = Join-Path $repo 'tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe'
$monitor = Join-Path $repo 'tools/PaneCommander.RenderMonitor/bin/Release/net10.0-windows/PaneCommander.RenderMonitor.exe'
$approved = Get-Content -LiteralPath (Join-Path $repo 'docs/verification/design-approved/source-sha256.json') -Raw | ConvertFrom-Json
$changed = @($approved | Where-Object { $_.path -like 'src\*' -or $_.path -like 'tests\*' } | Where-Object {
    (Get-FileHash -LiteralPath (Join-Path $repo $_.path) -Algorithm SHA256).Hash -ne $_.sha256
})
if ($changed.Count -ne 0) { throw 'Approved product/test source has changed; use a separately documented baseline.' }
$environment = [pscustomobject]@{
    Date=(Get-Date -Format o)
    OS=[Environment]::OSVersion.VersionString
    SDK=(& dotnet --version)
    CPU=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
    RAM=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    GPU=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)
    PowerPlan=((& powercfg /getactivescheme) -join '')
    ProductChanged=$changed.Count
    DriverHash=(Get-FileHash -LiteralPath $driver -Algorithm SHA256).Hash
    MonitorHash=(Get-FileHash -LiteralPath $monitor -Algorithm SHA256).Hash
    Schedule='T1..T9 each A1,B1,A2,B2,A3,B3; no overlap; new process per run'
    ForcedGc=$false
    WarmupSeconds=10
    DisplayControl='Monitor thread ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED; released on exit; power plan unchanged'
}
$environment | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'environment.json') -Encoding utf8
Get-ChildItem -LiteralPath (Split-Path $driver),(Split-Path $monitor) -File | Where-Object Extension -in '.dll','.exe','.json' | ForEach-Object {
    [pscustomobject]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;LastWriteTimeUtc=$_.LastWriteTimeUtc.ToString('o')}
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $outputPath 'binary-manifest-during.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Compare-Rendering.ps1') -Output $outputPath
& (Join-Path $PSScriptRoot 'Verify-RenderingIntegrity.ps1') -Output $outputPath

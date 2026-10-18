param([string]$Output='artifacts/filter-comparison',[int]$Repetitions=3)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$root=[IO.Path]::GetFullPath($Output,$repo)
if(Test-Path -LiteralPath $root) {throw 'Use a new output directory'}
[IO.Directory]::CreateDirectory($root) | Out-Null
$builds=Join-Path $root 'builds'
[IO.Directory]::CreateDirectory($builds) | Out-Null
foreach($era in @('before','after')) {
    $target=Join-Path $builds $era
    Copy-Item (Join-Path $repo 'tools/PaneCommander.Performance/bin/Release/net10.0-windows') $target -Recurse
    if($era -eq 'before') {Copy-Item (Join-Path $repo 'artifacts/filter-before/app/*') $target -Force}
}
$manifest=@(Get-ChildItem $builds -Recurse -File | Where-Object Extension -in '.dll','.exe','.json' | ForEach-Object {@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'binary-manifest.json')
$sources=@(Get-ChildItem (Join-Path $repo 'src'),(Join-Path $repo 'tests') -Recurse -File | Where-Object {$_.Extension -in '.cs','.xaml','.csproj' -and $_.FullName -notmatch '\\(bin|obj)\\'} | ForEach-Object {@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$sources | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'source-manifest.json')
@{CapturedBeforeRuns=(Get-Date -Format o);CPU=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors);GPU=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate);RAM=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory;PowerPlan=(& powercfg /getactivescheme | Out-String).Trim()} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'environment.json')
@{OS=[Environment]::OSVersion.VersionString;Runtime=(& dotnet --info | Out-String);LogicalProcessors=[Environment]::ProcessorCount;WarmupSeconds=5;PhaseHoldSeconds=3;RepeatedEdits=100;ForcedGc=$false;Baseline='Old product UI plus driver-injected equivalent CollectionView filter (feature absent in old product)';Input='Dedicated driver, 10000 synthetic entries per folder';Repetitions=$Repetitions} | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'protocol.json')
$monitor=Join-Path $repo 'tools/PaneCommander.RenderMonitor/bin/Release/net10.0-windows/PaneCommander.RenderMonitor.exe'
foreach($repeat in 1..$Repetitions) {
    foreach($mode in @('A','B')) {
        foreach($era in @('before','after')) {
            $run=Join-Path $root "$era-$mode-$repeat"
            & $monitor (Join-Path $builds "$era/PaneCommander.Performance.exe") FILTER $repo $run $mode
            if($LASTEXITCODE -ne 0) {throw "Filter measurement failed: $era $mode $repeat"}
            Write-Output ("Completed filter: $era $mode $repeat "+(Get-Date -Format HH:mm:ss))
        }
    }
}
foreach($entry in $manifest) {if((Get-FileHash -LiteralPath $entry.Path).Hash -ne $entry.SHA256) {throw 'Frozen build changed'}}
foreach($entry in $sources) {if((Get-FileHash -LiteralPath $entry.Path).Hash -ne $entry.SHA256) {throw 'Measured source changed'}}
@{Completed=$true;FrozenBinariesUnchanged=$true;SourcesUnchanged=$true;Finished=(Get-Date -Format o)} | ConvertTo-Json | Set-Content (Join-Path $root 'integrity.json')

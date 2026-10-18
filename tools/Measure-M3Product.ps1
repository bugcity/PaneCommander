param(
    [string]$Output = 'artifacts/m3-product',
    [string[]]$Scenarios = @('two','six-restored','six-visited','large','idle'),
    [int]$Repetitions = 3,
    [int]$ObservationSeconds = 10,
    [int]$IdleSeconds = 300,
    [string]$ProductExecutable = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$outputPath = [IO.Path]::GetFullPath($Output, $repo)
if (Test-Path -LiteralPath $outputPath) { throw 'Use a new output directory.' }
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$fixture = Join-Path $repo 'artifacts/render-comparison-fixture'
foreach ($folder in @('normal/child','large')) { [IO.Directory]::CreateDirectory((Join-Path $fixture $folder)) | Out-Null }
foreach ($index in 1..255) { $path=Join-Path $fixture ('normal/item-{0:D5}.txt' -f $index); if (!(Test-Path -LiteralPath $path)) {[IO.File]::WriteAllText($path,'')} }
foreach ($index in 1..10000) { $path=Join-Path $fixture ('large/item-{0:D5}.txt' -f $index); if (!(Test-Path -LiteralPath $path)) {[IO.File]::WriteAllText($path,'')} }
$monitor = Join-Path $repo 'tools/PaneCommander.RenderMonitor/bin/Release/net10.0-windows/PaneCommander.RenderMonitor.exe'
$product = Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.exe'
if ($ProductExecutable) { $product = [IO.Path]::GetFullPath($ProductExecutable, $repo) }
rg --files (Join-Path $repo 'src') (Join-Path $repo 'tests') | ForEach-Object {
    $sourceFile=Get-Item -LiteralPath $_
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($repo,$sourceFile.FullName);SHA256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash;LastWriteTimeUtc=$sourceFile.LastWriteTimeUtc.ToString('o')}
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $outputPath 'source-manifest-during.json') -Encoding utf8
Get-ChildItem -LiteralPath (Split-Path $product),(Split-Path $monitor) -File | Where-Object Extension -in '.dll','.exe','.json' | ForEach-Object {
    [pscustomobject]@{Path=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $outputPath 'binary-manifest.json') -Encoding utf8
[pscustomobject]@{Date=(Get-Date -Format o);OS=[Environment]::OSVersion.VersionString;SDK=(& dotnet --version);
    CPU=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors);
    RAM=(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory;
    GPU=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate);
    PowerPlan=((& powercfg /getactivescheme) -join '');WarmupSeconds=10;ForcedGc=$false;ProductExecutable=$product
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'environment.json') -Encoding utf8
$manifest = @()
foreach ($scenario in $Scenarios) {
    if ($scenario -notin @('two','six-restored','six-visited','large','idle')) { throw 'Unknown scenario' }
    foreach ($repeat in 1..$Repetitions) {
        foreach ($mode in @('Default','SoftwareOnly')) {
            $run = Join-Path $outputPath "$scenario-$mode-$repeat"
            $profile = Join-Path $run 'profile'
            [IO.Directory]::CreateDirectory($profile) | Out-Null
            @{RenderingMode=$mode;ShowFavorites=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json') -Encoding utf8
            $tabs = if ($scenario -in @('six-restored','six-visited')) {3} else {1}
            $leftPath = Join-Path $fixture $(if ($scenario -eq 'large') {'large'} else {'normal'})
            $rightPath = Join-Path $fixture 'normal'
            $layout = @{Left=60;Top=60;Width=1280;Height=800;Maximized=$false;LeftPaneFraction=0.5;SidebarWidth=180}
            @{Version=1;Left=@{Tabs=@(1..$tabs | ForEach-Object {@{Path=$leftPath;SortProperty='Name';SortDescending=$false}});SelectedIndex=0};
                Right=@{Tabs=@(1..$tabs | ForEach-Object {@{Path=$rightPath;SortProperty='Name';SortDescending=$false}});SelectedIndex=0};
                ActiveSide=0;Layout=$layout} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $profile 'session.json') -Encoding utf8
            @{Version=1;Items=@(@{Id='11111111-1111-1111-1111-111111111111';Name='通常フォルダ';Path=$rightPath},@{Id='22222222-2222-2222-2222-222222222222';Name='大量ファイル';Path=(Join-Path $fixture 'large')})} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $profile 'favorites.json') -Encoding utf8
            $duration = if ($scenario -eq 'idle') {$IdleSeconds} else {$ObservationSeconds}
            $workload = if ($scenario -eq 'six-visited') {'visit-tabs'} else {'none'}
            & $monitor product $product $profile $run $duration $mode $workload
            if ($LASTEXITCODE -ne 0) { throw "Product measurement failed: $scenario $mode $repeat" }
            $manifest += [pscustomobject]@{Scenario=$scenario;Mode=$mode;Repeat=$repeat;Finished=(Get-Date -Format o);Directory=$run}
            $manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $outputPath 'run-order.json') -Encoding utf8
            Write-Output "Completed product: $scenario $mode $repeat $(Get-Date -Format HH:mm:ss)"
        }
    }
}
$frozen = Get-Content -LiteralPath (Join-Path $outputPath 'binary-manifest.json') -Raw | ConvertFrom-Json
$changed = @($frozen | Where-Object {(Get-FileHash -LiteralPath $_.Path -Algorithm SHA256).Hash -ne $_.SHA256})
if ($changed.Count) { throw 'Product binaries changed during measurements.' }
@{FrozenBinariesUnchanged=$true;Finished=(Get-Date -Format o);PowerPlan=((& powercfg /getactivescheme) -join '')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'integrity.json') -Encoding utf8
@{Date=(Get-Date -Format o);GPU=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath 'environment-after.json') -Encoding utf8

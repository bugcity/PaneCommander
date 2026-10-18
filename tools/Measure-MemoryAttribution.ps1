param(
    [string]$Fixture = 'artifacts/performance-design-approved/fixture',
    [string]$Output = 'artifacts/memory-attribution/reproduce',
    [int]$Repetitions = 2,
    [string[]]$Modes = @('runtime', 'empty', 'styled', 'model2', 'model6', 'ui2', 'ui6', 'stages'),
    [switch]$Software
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$diagnosticExe = Join-Path $repo 'tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe'
$fixturePath = [IO.Path]::GetFullPath($Fixture, $repo)
$outputPath = [IO.Path]::GetFullPath($Output, $repo)
if (!(Test-Path -LiteralPath (Join-Path $fixturePath 'normal'))) { throw 'Existing 256-entry fixture required. Run Measure-Performance.ps1 first.' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$previousSoftware = $env:PANECOMMANDER_DIAGNOSTIC_SOFTWARE
try {
    $env:PANECOMMANDER_DIAGNOSTIC_SOFTWARE = $(if ($Software) { '1' } else { '0' })
    foreach ($round in 1..$Repetitions) {
        foreach ($mode in $Modes) {
            $runOutput = Join-Path $outputPath "run-$round-$mode"
            # Quote each path because a checkout/fixture can contain spaces.
            $arguments = 'memory "{0}" "{1}" {2}' -f $fixturePath, $runOutput, $mode
            $diagnosticProcess = Start-Process -FilePath $diagnosticExe -ArgumentList $arguments -WindowStyle Hidden -PassThru
            $diagnosticProcess.WaitForExit()
            if ($diagnosticProcess.ExitCode -ne 0) { throw "Diagnostic failed: $mode ($runOutput)" }
            Write-Output "$round $mode completed"
        }
    }
} finally { $env:PANECOMMANDER_DIAGNOSTIC_SOFTWARE = $previousSoftware }
$rows = foreach ($file in Get-ChildItem -LiteralPath $outputPath -File -Recurse | Where-Object Name -in @('memory.json', 'lifecycle.json')) {
    $document = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    foreach ($sample in $document.samples) {
        [pscustomobject]@{
            Run = $file.Directory.Name; Mode = $(if ($document.mode) { $document.mode } else { 'lifecycle' }); Software = [bool]$document.software
            Stage = $sample.stage; ForcedGC = $sample.forcedGc
            PrivateMB = $sample.privateBytes / 1e6; WorkingSetMB = $sample.workingSet / 1e6
            ManagedMB = $sample.managedBytes / 1e6; GCCommittedMB = $sample.gcCommittedBytes / 1e6
            Tabs = $sample.tabs; Entries = $sample.entries
            Views = $(if ($sample.tracking) { ($sample.tracking | Where-Object type -eq 'FolderView').inTree } else { $sample.folderViews })
            Rows = $(if ($sample.tracking) { ($sample.tracking | Where-Object type -eq 'ListViewItem').inTree } else { $sample.realizedRows })
            Dates = $(if ($sample.tracking) { $sample.dateRegistry } else { $sample.dateCount })
        }
    }
}
$rows | Export-Csv -LiteralPath (Join-Path $outputPath 'summary.csv') -NoTypeInformation -Encoding utf8
$rows | Format-Table Run, Stage, ForcedGC, PrivateMB, ManagedMB, GCCommittedMB -AutoSize

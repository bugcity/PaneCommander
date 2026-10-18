param(
    [ValidateSet('initial', 'six', 'large', 'repeat', 'idle', 'soak')][string]$Scenario = 'initial',
    [ValidateRange(5, 86400)][int]$Seconds = 30,
    [string]$OutputDirectory = 'artifacts/performance'
)
$ErrorActionPreference = 'Stop'
$repoDirectory = Split-Path $PSScriptRoot -Parent
$outputRoot = [IO.Path]::GetFullPath((Join-Path $repoDirectory $OutputDirectory))
$runDirectory = Join-Path $outputRoot ($Scenario + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$fixtureDirectory = Join-Path $outputRoot 'fixture'
if ($Scenario -ne 'initial') {
    [IO.Directory]::CreateDirectory((Join-Path $fixtureDirectory 'normal/child')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $fixtureDirectory 'large')) | Out-Null
    foreach ($index in 1..255) { $file = Join-Path $fixtureDirectory ('normal/item-{0:D5}.txt' -f $index); if (-not [IO.File]::Exists($file)) { [IO.File]::WriteAllText($file, '') } }
    foreach ($index in 1..10000) { $file = Join-Path $fixtureDirectory ('large/item-{0:D5}.txt' -f $index); if (-not [IO.File]::Exists($file)) { [IO.File]::WriteAllText($file, '') } }
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PaneCommanderNativeMetrics {
    [StructLayout(LayoutKind.Sequential)]
    public struct Counters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessIoCounters(IntPtr handle, out Counters counters);
    [DllImport("kernel32.dll")]
    public static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);
}
'@
$cpuInfo = Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors
$computerInfo = Get-CimInstance Win32_ComputerSystem
$systemCpuCount = [PaneCommanderNativeMetrics]::GetActiveProcessorCount(0xffff)
if ($systemCpuCount -eq 0) { throw 'System CPU count could not be obtained.' }
@{ date = (Get-Date -Format o); cpu = $cpuInfo; logicalProcessors = $systemCpuCount; ramBytes = $computerInfo.TotalPhysicalMemory;
    os = [Environment]::OSVersion.VersionString; scenario = $Scenario; seconds = $Seconds; fixture = $fixtureDirectory;
    metric = 'PrivateMemorySize64 (Private Bytes), WorkingSet64; CPU normalized by all system logical processors';
    memoryUnit = 'decimal MB (1,000,000 bytes)'; monitorIsSeparateProcess = $true
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDirectory 'environment.json') -Encoding utf8
$target = $null
try {
    if ($Scenario -eq 'initial') {
        $executable = Join-Path $repoDirectory 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.exe'
        $target = Start-Process -FilePath $executable -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 15
        $target.Refresh()
        $dpi = [PaneCommanderNativeMetrics]::GetDpiForWindow($target.MainWindowHandle)
        @{ scenario = 'initial'; pid = $target.Id; warmupSeconds = 15; dpi = $dpi;
            folderCounts = @((Get-ChildItem -LiteralPath $env:USERPROFILE -Force).Count, (Get-ChildItem -LiteralPath $env:USERPROFILE -Force).Count);
            productionExecutable = $true
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'ready.json') -Encoding utf8
    } else {
        $executable = Join-Path $repoDirectory 'tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe'
        $arguments = @($Scenario, ('"' + $fixtureDirectory + '"'), ('"' + $runDirectory + '"'), $Seconds)
        $target = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru -WindowStyle Hidden
        $startupWatch = [Diagnostics.Stopwatch]::StartNew()
        while (-not [IO.File]::Exists((Join-Path $runDirectory 'ready.json'))) {
            if ($target.HasExited -or $startupWatch.Elapsed.TotalSeconds -gt 120) { throw 'Performance driver failed to become ready.' }
            Start-Sleep -Milliseconds 200
        }
    }
    $samples = [Collections.Generic.List[object]]::new()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $target.Refresh()
        $io = [PaneCommanderNativeMetrics+Counters]::new()
        if (-not [PaneCommanderNativeMetrics]::GetProcessIoCounters($target.Handle, [ref]$io)) { throw 'GetProcessIoCounters failed' }
        $samples.Add([pscustomobject]@{
            Seconds = $watch.Elapsed.TotalSeconds; PrivateBytes = $target.PrivateMemorySize64; WorkingSet = $target.WorkingSet64;
            CpuSeconds = $target.TotalProcessorTime.TotalSeconds; Handles = $target.HandleCount;
            ReadOps = $io.ReadOps; WriteOps = $io.WriteOps; OtherOps = $io.OtherOps;
            ReadBytes = $io.ReadBytes; WriteBytes = $io.WriteBytes; OtherBytes = $io.OtherBytes
        })
        if ($watch.Elapsed.TotalSeconds -ge $Seconds) { break }
        Start-Sleep -Seconds 1
    } while ($true)
    $samples | Export-Csv -LiteralPath (Join-Path $runDirectory 'samples.csv') -NoTypeInformation -Encoding utf8
    $first = $samples[0]; $last = $samples[$samples.Count - 1]
    $elapsed = $last.Seconds - $first.Seconds
    $memoryTarget = switch ($Scenario) { 'initial' { 150 }; 'six' { 200 }; 'large' { 300 }; default { $null } }
    $maxPrivateMB = ($samples | Measure-Object PrivateBytes -Maximum).Maximum / 1e6
    $cpuPercent = 100 * ($last.CpuSeconds - $first.CpuSeconds) / $elapsed / $systemCpuCount
    $summary = [ordered]@{
        scenario = $Scenario; pid = $target.Id; durationSeconds = $elapsed;
        maxPrivateMB = $maxPrivateMB; memoryTargetMB = $memoryTarget;
        memoryUnderTarget = $(if ($null -ne $memoryTarget) { $maxPrivateMB -le $memoryTarget } else { $null });
        finalPrivateMB = $last.PrivateBytes / 1e6; maxWorkingSetMB = ($samples | Measure-Object WorkingSet -Maximum).Maximum / 1e6;
        cpuPercentAllSystem = $cpuPercent;
        idleCpuUnderTarget = $(if ($Scenario -eq 'idle' -and $elapsed -ge 300) { $cpuPercent -lt 1 } else { $null });
        firstHandles = $first.Handles; finalHandles = $last.Handles;
        readOpsDelta = $last.ReadOps - $first.ReadOps; writeOpsDelta = $last.WriteOps - $first.WriteOps;
        otherOpsDelta = $last.OtherOps - $first.OtherOps;
        readBytesDelta = $last.ReadBytes - $first.ReadBytes; writeBytesDelta = $last.WriteBytes - $first.WriteBytes;
        otherBytesDelta = $last.OtherBytes - $first.OtherBytes
    }
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'summary.json') -Encoding utf8
    if ($Scenario -ne 'initial') {
        if (-not $target.WaitForExit(30000)) { throw 'Performance driver did not complete' }
        $driverExitCode = $target.ExitCode
        @{ exitCode = $driverExitCode; hasExited = $target.HasExited } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'process-exit.json') -Encoding utf8
        if ($driverExitCode -ne 0) { throw ('Performance driver reported failure; exitCode=' + $driverExitCode) }
    }
    $summary | ConvertTo-Json
    Write-Output ('Results: ' + $runDirectory)
    if ($summary.memoryUnderTarget -eq $false -or $summary.idleCpuUnderTarget -eq $false) { throw '暫定性能目標は未達です。summary.jsonを確認してください。' }
} catch {
    @{ error = $_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runDirectory 'failure.json') -Encoding utf8
    if ($null -ne $samples -and $samples.Count -gt 0) { $samples | Export-Csv -LiteralPath (Join-Path $runDirectory 'samples.csv') -NoTypeInformation -Encoding utf8 }
    throw
} finally {
    if ($null -ne $target -and -not $target.HasExited) {
        $target.CloseMainWindow() | Out-Null
        if (-not $target.WaitForExit(5000)) { throw ('測定プロセスが終了していません。PID: ' + $target.Id) }
    }
    if ($null -ne $target) { $target.Dispose() }
}

param([Parameter(Mandatory)][int]$TargetProcessId,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Output))|Out-Null
$watch=[Diagnostics.Stopwatch]::StartNew()
while($watch.Elapsed.TotalMinutes -lt 30 -and -not(Test-Path (Join-Path $Output 'stop'))){
    $process=Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue
    if(-not $process){break}
    $process.Refresh()
    $stagePath=Join-Path $Output 'stage.txt'
    $stage=if(Test-Path $stagePath){[IO.File]::ReadAllText($stagePath).Trim()}else{'unmarked'}
    [pscustomobject]@{Timestamp=[DateTimeOffset]::UtcNow.ToString('o');ElapsedMs=$watch.Elapsed.TotalMilliseconds;Stage=$stage;PrivateBytes=$process.PrivateMemorySize64;WorkingSet=$process.WorkingSet64;Handles=$process.HandleCount;CpuSeconds=$process.TotalProcessorTime.TotalSeconds;Processors=[Environment]::ProcessorCount}|Export-Csv (Join-Path $Output 'samples.csv') -Append -NoTypeInformation
    Start-Sleep -Milliseconds 500
}

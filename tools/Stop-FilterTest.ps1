param([string]$Output='docs/verification/filter/preliminary',[switch]$Measurement)
$ErrorActionPreference='Stop'
$all=Get-CimInstance Win32_Process
$scriptName=if($Measurement){'tools/Measure-Filter.ps1'}else{'tools/Verify-LinkOperations.ps1'}
$candidates=@($all | Where-Object {$_.Name -eq 'pwsh.exe' -and $_.CommandLine.Contains($scriptName) -and $_.CommandLine.Contains($Output) -and $_.ProcessId -ne $PID})
$roots=@($candidates | Where-Object {$candidates.ProcessId -notcontains $_.ParentProcessId})
if($roots.Count -ne 1) {throw 'Expected exactly one owned preliminary test root'}
$owned=@([int]$roots[0].ProcessId)
do {$children=@($all | Where-Object {$owned -contains [int]$_.ParentProcessId -and $owned -notcontains [int]$_.ProcessId});$owned+=@($children | ForEach-Object {[int]$_.ProcessId})}while($children.Count -gt 0)
foreach($id in ($owned | Sort-Object -Descending)) {Stop-Process -Id $id -ErrorAction SilentlyContinue}
$owned

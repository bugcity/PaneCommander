param([Parameter(Mandatory)][string]$FixtureRoot,[Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
$scope=[IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\')+'\'
if($scope -notlike '*\panecommander\artifacts\shell-gui-resume\*'){throw 'Dedicated fixture scope required'}
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$bin=Join-Path ([IO.Path]::GetPathRoot($scope)) ('$Recycle.Bin\'+$sid)
$receipts=@(foreach($metadata in Get-ChildItem -LiteralPath $bin -Force -ErrorAction Stop | Where-Object Name -like '$I*'){
    $bytes=[IO.File]::ReadAllBytes($metadata.FullName)
    if($bytes.Length -lt 28){continue}
    $version=[BitConverter]::ToInt64($bytes,0)
    $offset=if($version -eq 2){28}elseif($version -eq 1){24}else{continue}
    $original=[Text.Encoding]::Unicode.GetString($bytes,$offset,$bytes.Length-$offset).TrimEnd([char]0)
    if(-not $original.StartsWith($scope,[StringComparison]::OrdinalIgnoreCase)){continue}
    $receipt=Join-Path $bin ('$R'+$metadata.Name.Substring(2))
    @{Original=$original;Receipt=$receipt;Exists=(Test-Path -LiteralPath $receipt);OriginalAbsent=(-not(Test-Path -LiteralPath $original));Hash=$(if(Test-Path -LiteralPath $receipt -PathType Leaf){(Get-FileHash -LiteralPath $receipt).Hash}else{$null})}
})
@{Scope=$scope;Receipts=$receipts;ReadOnly=$true;Time=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 5|Set-Content $Output

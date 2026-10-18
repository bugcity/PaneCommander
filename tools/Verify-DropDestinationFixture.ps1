param([Parameter(Mandatory)][string]$Root,[Parameter(Mandatory)][string]$Output,[string]$Label='observation',[ValidateSet('left','right','either')][string]$ExpectedSide='either')
$ErrorActionPreference='Stop'
$scope=[IO.Path]::GetFullPath($Root)
if($scope -notlike '*\panecommander\artifacts\dnd-destination\*'){throw 'Dedicated DnD fixture required'}
$before=Get-Content -LiteralPath (Join-Path $scope 'verification.json') -Raw | ConvertFrom-Json
$payload=@($before.Baseline | Where-Object { [IO.Path]::GetFileName($_.Path) -eq '00-payload.txt' })
$guards=@(foreach($entry in $before.Baseline){if([IO.Path]::GetFileName($entry.Path) -eq '00-payload.txt'){continue};@{Path=$entry.Path;Unchanged=([IO.File]::Exists($entry.Path) -and (Get-FileHash -LiteralPath $entry.Path).Hash -eq $entry.Hash)}})
$positions=@(foreach($side in @('left','right')){$path=Join-Path $scope ($side+'/00-payload.txt');if([IO.File]::Exists($path)){@{Side=$side;Path=$path;Hash=(Get-FileHash -LiteralPath $path).Hash}}})
$markers=@(Get-ChildItem -LiteralPath (Join-Path $scope 'left'),(Join-Path $scope 'right') -File -Recurse | Where-Object Name -like '*-launched.marker' | Select-Object -ExpandProperty FullName)
$inside=@(Get-ChildItem -LiteralPath (Join-Path $scope 'left/50-subdirectory'),(Join-Path $scope 'right/50-subdirectory') -File -Recurse | Where-Object Name -eq '00-payload.txt' | Select-Object -ExpandProperty FullName)
@{Label=$Label;Root=$scope;Mode=$before.Mode;Time=[DateTimeOffset]::UtcNow.ToString('o');Positions=$positions;ExactlyOnePayload=($positions.Count -eq 1);PayloadHashMatches=($positions.Count -eq 1 -and $positions[0].Hash -eq $payload[0].Hash);ExpectedSide=$ExpectedSide;ExpectedSideMatches=($positions.Count -eq 1 -and ($ExpectedSide -eq 'either' -or $positions[0].Side -eq $ExpectedSide));Guards=$guards;AllGuardsUnchanged=(@($guards | Where-Object {-not $_.Unchanged}).Count -eq 0);LaunchMarkers=$markers;NoApplicationOrScriptLaunch=($markers.Count -eq 0);DetectorCalibrated=$before.LaunchDetectionCalibrated;PayloadInsideSubdirectory=$inside;NoDirectoryRowRedirection=($inside.Count -eq 0)} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $Output
Get-Content -LiteralPath $Output -Raw | ConvertFrom-Json | Select-Object Label,ExactlyOnePayload,PayloadHashMatches,ExpectedSideMatches,AllGuardsUnchanged,NoApplicationOrScriptLaunch,NoDirectoryRowRedirection

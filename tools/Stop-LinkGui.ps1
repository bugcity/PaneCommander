param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$path=[IO.Path]::GetFullPath($Root,$repo)
$parents=@((Join-Path $repo 'artifacts/link-gui'),(Join-Path $repo 'artifacts/filter-gui'))
if($parents -notcontains [IO.Path]::GetDirectoryName($path) -or ![Guid]::TryParse([IO.Path]::GetFileName($path),[ref]([Guid]::Empty))) {throw 'Not an isolated GUI fixture'}
$record=Get-Content (Join-Path $path 'verification.json') -Raw | ConvertFrom-Json
$owned=Get-CimInstance Win32_Process -Filter ('ProcessId='+$record.ProcessId)
if($owned) {
    if($owned.Name -ne 'PaneCommander.App.exe' -or !$owned.CommandLine.Contains($record.Profile)) {throw 'Owned process identity does not match'}
    Stop-Process -Id $record.ProcessId
}
$record | Add-Member -NotePropertyName OwnedProcessStopped -NotePropertyValue $true -Force
$record | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $path 'verification.json')

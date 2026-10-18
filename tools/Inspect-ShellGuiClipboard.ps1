param([Parameter(Mandatory)][string]$Output)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore
$data=[Windows.Clipboard]::GetDataObject()
$paths=@([Windows.Clipboard]::GetFileDropList())
$safe=@($paths|Where-Object {$_ -like '*\panecommander\artifacts\shell-gui-resume\*'})
@{Time=[DateTimeOffset]::UtcNow.ToString('o');HasFileDrop=$data.GetDataPresent('FileDrop',$false);HasPreferredDropEffect=$data.GetDataPresent('Preferred DropEffect',$false);PathCount=$paths.Count;IsolatedFixturePaths=$safe}|ConvertTo-Json -Depth 4|Set-Content $Output

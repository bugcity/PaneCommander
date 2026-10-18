param([Parameter(Mandatory)][ValidateSet('Apply','Restore')][string]$Action,[Parameter(Mandatory)][string]$StateDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $repo 'tools/PaneCommander.Performance/bin/Release/net10.0-windows/PaneCommander.Performance.exe'
$key='HKCU:/Software/Microsoft/DirectX/UserGpuPreferences'
$directory=[IO.Path]::GetFullPath($StateDirectory,$repo)
[IO.Directory]::CreateDirectory($directory)|Out-Null
$stateFile=Join-Path $directory 'gpu-setting-backup.json'
function ReadPreference {
 $exists=Test-Path -LiteralPath $key
 $item=if($exists){Get-Item -LiteralPath $key}else{$null}
 $hasValue=$exists -and ($item.GetValueNames() -contains $exe)
 return @{KeyExisted=$exists;ValueExisted=$hasValue;Value=$(if($hasValue){$item.GetValue($exe)}else{$null})}
}
if($Action -eq 'Apply'){
 if(Test-Path -LiteralPath $stateFile){throw 'Use a new state directory'}
 $before=ReadPreference
 if($before.ValueExisted){throw 'Diagnostic EXE now has a preference; abort rather than replace a changed baseline'}
 $state=@{Executable=$exe;Key=$key;Before=$before;WrittenValue='GpuPreference=2;';UserAuthorized=$true;AppliedUtc=[DateTimeOffset]::UtcNow.ToString('o')}
 $state|ConvertTo-Json -Depth 5|Set-Content $stateFile
 if(-not $before.KeyExisted){New-Item -Path $key -Force|Out-Null}
 New-ItemProperty -LiteralPath $key -Name $exe -Value $state.WrittenValue -PropertyType String -Force|Out-Null
 $after=ReadPreference
 if(-not $after.ValueExisted -or $after.Value -ne $state.WrittenValue){throw 'Preference verification failed; use the saved backup to restore'}
 @{Applied=$true;Executable=$exe;Value=$after.Value}|ConvertTo-Json|Set-Content (Join-Path $directory 'gpu-setting-applied.json')
}else{
 $state=Get-Content -LiteralPath $stateFile -Raw|ConvertFrom-Json
 if($state.Executable -ne $exe -or $state.Key -ne $key -or $state.WrittenValue -ne 'GpuPreference=2;'){throw 'Backup identity mismatch'}
 $current=ReadPreference
 if($current.ValueExisted -and $current.Value -ne $state.WrittenValue){throw 'Preference changed externally; refusing to overwrite'}
 if($state.Before.ValueExisted){throw 'Unexpected baseline; manual review required'}
 if($current.ValueExisted){Remove-ItemProperty -LiteralPath $key -Name $exe}
 if(-not $state.Before.KeyExisted -and (Test-Path -LiteralPath $key)){
  $item=Get-Item -LiteralPath $key
  if($item.GetValueNames().Count -eq 0 -and $item.GetSubKeyNames().Count -eq 0){Remove-Item -LiteralPath $key}
 }
 $after=ReadPreference
 if($after.ValueExisted){throw 'Restoration verification failed'}
 @{Restored=$true;Executable=$exe;ValueExisted=$after.ValueExisted;KeyExisted=$after.KeyExisted;RestoredUtc=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $directory 'gpu-setting-restored.json')
}

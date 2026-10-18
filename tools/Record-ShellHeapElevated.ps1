# User-approved, temporary IFEO configuration for the diagnostic image only.
# Run with an administrator token; target process is launched separately without elevation.
param([switch]$FreeStacks)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repo ('docs/verification/shell-lifetime-20261016/'+$(if($FreeStacks){'native-heap-free'}else{'native-heap'}))
[IO.Directory]::CreateDirectory($output)|Out-Null
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(!([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator consent is required.'}
$key='SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\PaneCommander.Performance.exe'
$original=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($key,$false)
$existed=$null -ne $original
$hadFlag=$existed -and $original.GetValueNames().Contains('TracingFlags')
$value=if($hadFlag){$original.GetValue('TracingFlags')}else{$null}
$kind=if($hadFlag){$original.GetValueKind('TracingFlags')}else{$null}
$originalValues=@{}
if($original){foreach($name in $original.GetValueNames()){$originalValues[$name]=@{Value=$original.GetValue($name);Kind=$original.GetValueKind($name).ToString()}}}
$originalSubKeys=if($original){@($original.GetSubKeyNames())}else{@()}
if($original){$original.Dispose()}
@{Key=$key;KeyExisted=$existed;FlagExisted=$hadFlag;Value=$value;Kind="$kind";AllValues=$originalValues;SubKeys=$originalSubKeys;Observed=(Get-Date).ToString('o');ExecutionPolicies=@(Get-ExecutionPolicy -List)}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $output 'original-setting.json')
$configured=$false;$recording=$false
try{
 $status=& wpr -status 2>&1|Out-String
 $status|Set-Content (Join-Path $output 'status-before.txt')
 if($status -notmatch 'WPR is not recording'){throw 'Another recording may exist; do not interrupt it.'}
 $configured=$true
 & wpr -heaptracingconfig PaneCommander.Performance.exe enable *> (Join-Path $output 'enable.txt')
 if($LASTEXITCODE -ne 0){throw 'Heap tracing configuration failed.'}
 $profile=if($FreeStacks){(Join-Path $output 'heap-free.wprp')+'!Heap'}else{'Heap'}
 & wpr -start $profile -filemode *> (Join-Path $output 'start.txt')
 if($LASTEXITCODE -ne 0){throw 'Heap recording start failed.'}
 $recording=$true
 $recordWatch=[Diagnostics.Stopwatch]::StartNew()
 @{Ready=(Get-Date).ToString('o');TargetImage='PaneCommander.Performance.exe';ProductNotElevated=$true}|ConvertTo-Json|Set-Content (Join-Path $output 'ready.json')
 # Stop well before the approved 180-second maximum, allowing stop/flush latency.
 $deadline=[DateTime]::UtcNow.AddSeconds(140)
 while(!(Test-Path -LiteralPath (Join-Path $output 'target-complete.json')) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Seconds 1}
 if(!(Test-Path -LiteralPath (Join-Path $output 'target-complete.json'))){throw 'Target did not finish within the bounded recording window.'}
 & wpr -stop (Join-Path $output 'heap.etl') *> (Join-Path $output 'stop.txt')
 if($LASTEXITCODE -ne 0){throw 'Heap recording stop failed.'}
 $recording=$false
 @{Completed=(Get-Date).ToString('o');Trace=(Join-Path $output 'heap.etl');StartToStopSeconds=$recordWatch.Elapsed.TotalSeconds;MaximumApprovedSeconds=180}|ConvertTo-Json|Set-Content (Join-Path $output 'completed.json')
}catch{
 $_|Out-String|Set-Content (Join-Path $output 'failure.txt')
}finally{
 try{if($recording){& wpr -stop (Join-Path $output 'heap-incomplete.etl') *> (Join-Path $output 'stop-incomplete.txt')}}finally{
 if($configured){
  & wpr -heaptracingconfig PaneCommander.Performance.exe disable *> (Join-Path $output 'disable.txt')
  $restore=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($key,$true)
  if($restore){
   if($hadFlag){$restore.SetValue('TracingFlags',$value,$kind)}else{$restore.DeleteValue('TracingFlags',$false)}
   $empty=$restore.ValueCount -eq 0 -and $restore.SubKeyCount -eq 0
   $restore.Dispose()
   if(!$existed -and $empty){[Microsoft.Win32.Registry]::LocalMachine.DeleteSubKey($key,$false)}
  }
 }
 $verify=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($key,$false)
 $flagPresent=$null -ne $verify -and $verify.GetValueNames().Contains('TracingFlags')
 $restored=if($hadFlag){$flagPresent -and $verify.GetValue('TracingFlags') -eq $value -and $verify.GetValueKind('TracingFlags') -eq $kind}else{!$flagPresent}
 $keyPresent=$null -ne $verify
 if(!$existed -and $keyPresent){$restored=$false}
 if($existed -and !$keyPresent){$restored=$false}
 if($verify -and $existed){
  if($verify.ValueCount -ne $originalValues.Count -or $verify.SubKeyCount -ne $originalSubKeys.Count){$restored=$false}
  foreach($name in $originalValues.Keys){
   if($verify.GetValueKind($name).ToString() -ne $originalValues[$name].Kind -or (ConvertTo-Json -InputObject $verify.GetValue($name) -Compress) -ne (ConvertTo-Json -InputObject $originalValues[$name].Value -Compress)){$restored=$false}
  }
 }
 if($verify){$verify.Dispose()}
 @{Restored=$restored;KeyPresent=$keyPresent;OriginalKeyPresent=$existed;Observed=(Get-Date).ToString('o');RecorderStatus=(& wpr -status 2>&1|Out-String)}|ConvertTo-Json|Set-Content (Join-Path $output 'restoration.json')
 }
}

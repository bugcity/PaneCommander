param([string]$Output='docs/verification/session-lock/20261014')
$ErrorActionPreference='Stop'
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Output))|Out-Null
$start=[DateTimeOffset]::Parse('2026-10-14T19:55:00+09:00').LocalDateTime
$end=[DateTimeOffset]::Parse('2026-10-14T21:10:00+09:00').LocalDateTime
$errors=[Collections.Generic.List[object]]::new()
$events=[Collections.Generic.List[object]]::new()
$queries=@(
    @{LogName='Security';Id=@(4800,4801,4802,4803,4778,4779)},
    @{LogName='System';ProviderName='Microsoft-Windows-Kernel-Power'},
    @{LogName='System';ProviderName='Microsoft-Windows-Power-Troubleshooter'},
    @{LogName='System';ProviderName='Microsoft-Windows-Kernel-General';Id=@(1,12,13)},
    @{LogName='System';ProviderName='User32';Id=@(1074)},
    @{LogName='Microsoft-Windows-TerminalServices-LocalSessionManager/Operational'},
    @{LogName='Microsoft-Windows-Winlogon/Operational'},
    @{LogName='Microsoft-Windows-TaskScheduler/Operational';Id=@(100,102,200,201)}
)
foreach($query in $queries){
    $filter=$query.Clone();$filter.StartTime=$start;$filter.EndTime=$end
    try{
        foreach($event in Get-WinEvent -FilterHashtable $filter -ErrorAction Stop){
            $xml=[xml]$event.ToXml();$data=@{}
            foreach($entry in $xml.Event.EventData.Data){
                $name=[string]$entry.Name
                if($name -match '(?i)user|domain|sid|address|clientname|logonid'){continue}
                if($name){$data[$name]=[string]$entry.'#text'}
            }
            $events.Add(@{Time=$event.TimeCreated.ToUniversalTime().ToString('o');Id=$event.Id;Provider=$event.ProviderName;Log=$event.LogName;RecordId=$event.RecordId;Data=$data;Message=$(if($event.LogName -eq 'Security'){$null}else{$event.Message})})
        }
    }catch{$errors.Add(@{Query=$query;Error=$_.Exception.Message})}
}
$events|Sort-Object Time|ConvertTo-Json -Depth 8|Set-Content (Join-Path $Output 'events.json')
$settings=@()
foreach($path in @('HKCU:\Control Panel\Desktop','HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon','HKCU:\Software\Policies\Microsoft\Windows\Control Panel\Desktop','HKLM:\Software\Policies\Microsoft\Windows\Control Panel\Desktop','HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System','HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\DeviceLock','HKLM:\SOFTWARE\Policies\Microsoft\Windows\System')){
    $values=@{}
    try{
        if(Test-Path -LiteralPath $path){
            $item=Get-ItemProperty -LiteralPath $path
            foreach($property in $item.PSObject.Properties){if($property.Name -match '^(ScreenSaveActive|ScreenSaverIsSecure|ScreenSaveTimeOut|SCRNSAVE.EXE|EnableGoodbye|InactivityTimeoutSecs|MaxInactivityTimeDeviceLock|AllowDomainDelayLock|DisableLockWorkstation)$'){$values[$property.Name]=$property.Value}}
        }
        $settings+=@{Path=$path;Values=$values}
    }catch{$errors.Add(@{Registry=$path;Error=$_.Exception.Message})}
}
$settings|ConvertTo-Json -Depth 6|Set-Content (Join-Path $Output 'lock-settings.json')
& powercfg /getactivescheme 2>&1|Set-Content (Join-Path $Output 'power-active.txt')
& powercfg /qh SCHEME_CURRENT 2>&1|Set-Content (Join-Path $Output 'power-settings.txt')
try{& auditpol /get /subcategory:'{0CCE921C-69AE-11D9-BED3-505054503030}' 2>&1|Set-Content (Join-Path $Output 'audit-other-logon.txt')}catch{$errors.Add(@{AuditPolicy=$_.Exception.Message})}
try{
    @(Get-ScheduledTask|Where-Object{($_.Actions.Arguments -join ' ') -match 'LockWorkStation'}|ForEach-Object{@{TaskName=$_.TaskName;TaskPath=$_.TaskPath;State=[string]$_.State;Actions=@($_.Actions|ForEach-Object{@{Execute=$_.Execute;Arguments=$_.Arguments}})}})|ConvertTo-Json -Depth 6|Set-Content (Join-Path $Output 'lock-tasks.json')
}catch{$errors.Add(@{Tasks=$_.Exception.Message})}
$errors|ConvertTo-Json -Depth 6|Set-Content (Join-Path $Output 'errors.json')
@{Collected=[DateTimeOffset]::UtcNow.ToString('o');IntervalStart=[DateTimeOffset]::Parse('2026-10-14T19:55:00+09:00').ToString('o');IntervalEnd=[DateTimeOffset]::Parse('2026-10-14T21:10:00+09:00').ToString('o');ReadOnly=$true;Events=$events.Count;Errors=$errors.Count;ProcessSession=[Diagnostics.Process]::GetCurrentProcess().SessionId}|ConvertTo-Json|Set-Content (Join-Path $Output 'collection.json')
Get-Content (Join-Path $Output 'collection.json')

$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$root=Join-Path $repo ('artifacts/filter-gui/'+[Guid]::NewGuid().ToString('N'))
$left=Join-Path $root 'filter-real-left';$right=Join-Path $root 'filter-real-right';$profile=Join-Path $root 'profile'
foreach($path in @($left,$right,$profile)) {[IO.Directory]::CreateDirectory($path) | Out-Null}
foreach($name in @('report.xlsx','monthly_report.pdf','2026_REPORT.csv','report_old_report.txt','other.txt')) {[IO.File]::WriteAllText((Join-Path $left $name),'fixture')}
[IO.File]::WriteAllText((Join-Path $right 'right_report.txt'),'fixture')
@{Version=1;Left=@{Tabs=@(@{Path=$left;SortProperty='Name';SortDescending=$false});SelectedIndex=0};Right=@{Tabs=@(@{Path=$right;SortProperty='Name';SortDescending=$false});SelectedIndex=0};ActiveSide=0;Layout=@{Left=80;Top=50;Width=1280;Height=800;Maximized=$false;LeftPaneFraction=.5;SidebarWidth=180}} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $profile 'session.json')
$process=Start-Process -FilePath (Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.exe') -ArgumentList @('--profile-directory',('"'+$profile+'"')) -PassThru
@{ProcessId=$process.Id;Root=$root;Profile=$profile;NativeGuiVerified=$false;InputsSent=$false} | ConvertTo-Json | Set-Content (Join-Path $root 'verification.json')
$root

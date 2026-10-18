$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$root=Join-Path $repo 'docs/verification/intel-driver-retest-20261015/product-Default'
if(Test-Path $root){throw 'Fresh product fixture required'}
foreach($name in @('left','right','profile','diagnostics')){[IO.Directory]::CreateDirectory((Join-Path $root $name))|Out-Null}
$left=Join-Path $root 'left';$right=Join-Path $root 'right';$profile=Join-Path $root 'profile';$diag=Join-Path $root 'diagnostics'
[IO.File]::WriteAllText((Join-Path $left 'render-check.txt'),'Isolated post-driver-update rendering fixture')
[IO.Directory]::CreateDirectory((Join-Path $left 'folder'))|Out-Null
[IO.File]::WriteAllText((Join-Path $left 'folder/inside.txt'),'Isolated nested fixture')
@{RenderingMode=1}|ConvertTo-Json|Set-Content (Join-Path $profile 'settings.json')
@{Version=1;Left=@{Tabs=@(@{Path=$left;SortProperty='Name';SortDescending=$false});SelectedIndex=0};Right=@{Tabs=@(@{Path=$right;SortProperty='Name';SortDescending=$false});SelectedIndex=0};ActiveSide=0;Layout=@{Left=80;Top=50;Width=1280;Height=800;Maximized=$false;LeftPaneFraction=.5;SidebarWidth=180}}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $profile 'session.json')
$exe=Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.exe'
$p=Start-Process $exe -ArgumentList @('--profile-directory',('"'+$profile+'"'),'--diagnostics-directory',('"'+$diag+'"')) -WindowStyle Hidden -PassThru
@{ProcessId=$p.Id;Mode='Default';Root=$root;ExeHash=(Get-FileHash $exe).Hash;DllHash=(Get-FileHash (Join-Path (Split-Path $exe) 'PaneCommander.App.dll')).Hash;LaunchUtc=[DateTimeOffset]::UtcNow.ToString('o');NormalUserProfileTouched=$false;ProductCodeChanged=$false}|ConvertTo-Json|Set-Content (Join-Path $root 'launch.json')
$p.Id

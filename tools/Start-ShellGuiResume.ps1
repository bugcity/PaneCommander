param([ValidateSet('Default','SoftwareOnly')][string]$Mode='SoftwareOnly',[switch]$FunctionalOnly)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$root=Join-Path $repo ('artifacts/shell-gui-resume/'+$Mode+'-'+[Guid]::NewGuid().ToString('N'))
$left=Join-Path $root 'left';$right=Join-Path $root 'right';$profile=Join-Path $root 'profile';$target=Join-Path $root 'protected-target';$diag=Join-Path $root 'diagnostics'
foreach($path in @($left,$right,$profile,$target,$diag)){[IO.Directory]::CreateDirectory($path)|Out-Null}
foreach($name in @('alpha.txt','bravo.txt','copy-f5.txt','move-f6.txt','rename.txt','trash-f8.txt','trash-delete.txt','permanent.txt','drag.txt')){[IO.File]::WriteAllText((Join-Path $left $name),('PaneCommander isolated GUI fixture: '+$name))}
[IO.Directory]::CreateDirectory((Join-Path $left 'folder'))|Out-Null
[IO.File]::WriteAllText((Join-Path $left 'folder/inside.txt'),'isolated nested fixture')
[IO.File]::WriteAllText((Join-Path $target 'sentinel.txt'),'protected target contents')
New-Item -ItemType Junction -Path (Join-Path $left 'junction') -Target $target|Out-Null
@{RenderingMode=$(if($Mode -eq 'SoftwareOnly'){0}else{1})}|ConvertTo-Json|Set-Content (Join-Path $profile 'settings.json')
@{Version=1;Left=@{Tabs=@(@{Path=$left;SortProperty='Name';SortDescending=$false});SelectedIndex=0};Right=@{Tabs=@(@{Path=$right;SortProperty='Name';SortDescending=$false});SelectedIndex=0};ActiveSide=0;Layout=@{Left=80;Top=50;Width=1280;Height=800;Maximized=$false;LeftPaneFraction=.5;SidebarWidth=180}}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $profile 'session.json')
$product=Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.exe'
$launchArguments=@('--profile-directory',('"'+$profile+'"'))
if(-not $FunctionalOnly){$launchArguments+=@('--diagnostics-directory',('"'+$diag+'"'))}
$process=Start-Process -FilePath $product -ArgumentList $launchArguments -PassThru -WindowStyle Hidden
@{ProcessId=$process.Id;Root=$root;Mode=$Mode;ProductHash=(Get-FileHash $product).Hash;TargetHash=(Get-FileHash (Join-Path $target 'sentinel.txt')).Hash;Started=(Get-Date).ToString('o');ForcedGC=$false}|ConvertTo-Json|Set-Content (Join-Path $root 'verification.json')
$root

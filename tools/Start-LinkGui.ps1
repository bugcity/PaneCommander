$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$root=Join-Path $repo ('artifacts/link-gui/'+[Guid]::NewGuid().ToString('N'))
$left=Join-Path $root 'links-left'; $right=Join-Path $root 'links-right'; $target=Join-Path $root 'protected-target'; $profile=Join-Path $root 'profile'
foreach($path in @($left,$right,$target,$profile)) {[IO.Directory]::CreateDirectory($path) | Out-Null}
[IO.File]::WriteAllText((Join-Path $target 'sentinel.txt'),'protected target contents')
New-Item -ItemType Junction -Path (Join-Path $left 'junction') -Target $target | Out-Null
[IO.File]::WriteAllText((Join-Path $left 'ordinary.txt'),'ordinary contents')
@{Version=1;Left=@{Tabs=@(@{Path=$left;SortProperty='Name';SortDescending=$false});SelectedIndex=0};Right=@{Tabs=@(@{Path=$right;SortProperty='Name';SortDescending=$false});SelectedIndex=0};ActiveSide=0;Layout=@{Left=80;Top=50;Width=1280;Height=800;Maximized=$false;LeftPaneFraction=.5;SidebarWidth=180}} | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $profile 'session.json')
$product=Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.exe'
$process=Start-Process -FilePath $product -ArgumentList @('--profile-directory',('"'+$profile+'"')) -PassThru
@{ProcessId=$process.Id;Root=$root;Product=$product;Profile=$profile;TargetHash=(Get-FileHash (Join-Path $target 'sentinel.txt')).Hash;NativeGuiVerified=$false} | ConvertTo-Json | Set-Content (Join-Path $root 'verification.json')
Write-Output $root
Write-Output ('OwnedPid: '+$process.Id)

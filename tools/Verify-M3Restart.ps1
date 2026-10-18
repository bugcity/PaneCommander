param([string]$Output = 'artifacts/m3-restart')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$root = [IO.Path]::GetFullPath($Output, $repo)
if (Test-Path -LiteralPath $root) {throw 'Use a new output directory.'}
$profile = Join-Path $root 'profile'; [IO.Directory]::CreateDirectory($profile) | Out-Null
$monitor = Join-Path $repo 'tools/PaneCommander.RenderMonitor/bin/Release/net10.0-windows/PaneCommander.RenderMonitor.exe'
$product = Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.exe'
$normal = Join-Path $repo 'artifacts/render-comparison-fixture/normal'
$child = Join-Path $normal 'child'
$session = @{Version=1;Left=@{Tabs=@(@{Path=$normal},@{Path=$child});SelectedIndex=1};
    Right=@{Tabs=@(@{Path=$child},@{Path=$normal});SelectedIndex=1};ActiveSide=1;
    Layout=@{Left=75;Top=65;Width=1240;Height=760;LeftPaneFraction=0.58;SidebarWidth=205;Maximized=$false}}
$session | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $profile 'session.json') -Encoding utf8
@{Version=1;Items=@(@{Id='11111111-1111-1111-1111-111111111111';Name='日本語 資料';Path=$normal})} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $profile 'favorites.json') -Encoding utf8
$results=@()
foreach ($step in @(@{Name='initial-software';Mode='SoftwareOnly'},@{Name='restart-hardware';Mode='Default'},@{Name='restart-software';Mode='SoftwareOnly'},@{Name='invalid-paths';Mode='SoftwareOnly'},@{Name='corrupt-session';Mode='SoftwareOnly'})) {
    if ($step.Name -eq 'invalid-paths') {
        $invalid = Get-Content -LiteralPath (Join-Path $profile 'session.json') -Raw | ConvertFrom-Json
        $invalid.Left.Tabs[$invalid.Left.SelectedIndex].Path = Join-Path $profile 'does-not-exist-left'
        $invalid.Right.Tabs[$invalid.Right.SelectedIndex].Path = Join-Path $profile 'does-not-exist-right'
        $invalid | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $profile 'session.json') -Encoding utf8
    }
    if ($step.Name -eq 'corrupt-session') { [IO.File]::WriteAllText((Join-Path $profile 'session.json'),'{') }
    @{RenderingMode=$step.Mode;FontSize=15;ShowFavorites=$false;FileAgeSaturation=0.5} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profile 'settings.json') -Encoding utf8
    $run = Join-Path $root $step.Name
    & $monitor product $product $profile $run 1 $step.Mode none
    if ($LASTEXITCODE -ne 0) {throw "Restart verification failed: $($step.Name)"}
    $ready = Get-Content -LiteralPath (Join-Path $run 'ready.json') -Raw | ConvertFrom-Json
    if ($ready.modeBeforeWindow -ne $step.Mode -or $ready.settings.FontSize -ne 15 -or $ready.settings.ShowFavorites -ne $false -or $ready.favorites[0].Name -ne '日本語 資料') {throw 'Rendering/settings/favorites mismatch'}
    if ($step.Name -in @('initial-software','restart-hardware','restart-software')) {
        if ($ready.session.Left.Tabs.Count -ne 2 -or $ready.session.Left.SelectedIndex -ne 1 -or $ready.session.ActiveSide -ne 1) {throw 'Tab restoration mismatch'}
        if ($ready.session.Left.Tabs[1].PSObject.Properties.Name -contains 'ColumnsMode') {throw 'Obsolete view mode was saved'}
        if ([Math]::Abs($ready.session.Layout.Width-1240) -gt 1 -or [Math]::Abs($ready.session.Layout.Height-760) -gt 1 -or [Math]::Abs($ready.session.Layout.LeftPaneFraction-.58) -gt .01 -or $ready.session.Layout.SidebarWidth -ne 205) {throw 'Layout restoration mismatch'}
    }
    if ($step.Name -eq 'invalid-paths') {
        if ($ready.statuses[1].NavigationSucceeded -or $ready.statuses[3].NavigationSucceeded -or $ready.session.Left.Tabs[1].Path -ne $invalid.Left.Tabs[1].Path) {throw 'Invalid paths were silently replaced'}
    }
    if ($step.Name -eq 'corrupt-session' -and !$ready.startupWarning) {throw 'Corrupt session did not produce a notification'}
    $results += [pscustomobject]@{Step=$step.Name;Mode=$step.Mode;Passed=$true;Finished=(Get-Date -Format o)}
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'verification.json') -Encoding utf8
    Write-Output "Restart verified: $($step.Name) $(Get-Date -Format HH:mm:ss)"
}

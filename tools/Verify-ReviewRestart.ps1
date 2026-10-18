param([string]$Output='artifacts/review-restart')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$root=[IO.Path]::GetFullPath($Output,$repo)
& (Join-Path $PSScriptRoot 'Verify-M3Restart.ps1') -Output (Join-Path $root 'm3-regression')
if ($LASTEXITCODE -ne 0) {throw 'M3 restart regression failed'}
$profile=Join-Path $root 'review-profile'
[IO.Directory]::CreateDirectory($profile) | Out-Null
$original=Join-Path $root 'm3-regression/initial-software/ready.json'
$ready=Get-Content -LiteralPath $original -Raw | ConvertFrom-Json
$settings=$ready.settings
$settings.TitleDisplay='FullPath'
$settings.FontFamily='Meiryo'
$settings.LeftColumns=@{NameFillsRemaining=$false;Columns=@(@{Key='Size';Visible=$true;Width=135},@{Key='Name';Visible=$true;Width=250},@{Key='Kind';Visible=$false;Width=70},@{Key='FileAge';Visible=$true;Width=90},@{Key='LastWriteTime';Visible=$true;Width=180})}
$colors=@{}
foreach($p in $settings.PSObject.Properties) {if($p.Value -is [string] -and $p.Value -match '^#[0-9A-Fa-f]{6}$') {$colors[$p.Name]=$p.Value}}
$colors.Background='#232A31'
$settings.Background='#232A31'
$settings.ColorSchemeName='再起動確認'
$settings.CustomSchemes=@(@{Name='再起動確認';Colors=$colors;Saturation=.45;Lightness=.65})
Copy-Item (Join-Path $root 'm3-regression/profile/favorites.json') $profile
$monitor=Join-Path $repo 'tools/PaneCommander.RenderMonitor/bin/Release/net10.0-windows/PaneCommander.RenderMonitor.exe'
$product=Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.exe'
$results=@()
foreach($step in @(@{Name='review-hardware';Mode='Default';Restore=$true},@{Name='review-software';Mode='SoftwareOnly';Restore=$true},@{Name='restore-disabled';Mode='SoftwareOnly';Restore=$false})) {
    $ready.session | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $profile 'session.json') -Encoding utf8
    $settings.RenderingMode=$step.Mode
    $settings.RestoreSession=$step.Restore
    $settings | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $profile 'settings.json') -Encoding utf8
    $run=Join-Path $root $step.Name
    & $monitor product $product $profile $run 1 $step.Mode none
    if($LASTEXITCODE -ne 0) {throw "Review restart failed: $($step.Name)"}
    $actual=Get-Content (Join-Path $run 'ready.json') -Raw | ConvertFrom-Json
    if($actual.settings.LeftColumns.Columns[0].Key -ne 'Size' -or $actual.settings.LeftColumns.NameFillsRemaining -or $actual.settings.FontFamily -ne 'Meiryo' -or $actual.settings.TitleDisplay -ne 'FullPath' -or $actual.settings.Background -ne '#232A31' -or $actual.settings.CustomSchemes[0].Name -ne '再起動確認') {throw 'Review settings restoration mismatch'}
    if($step.Restore -and $actual.session.Left.Tabs.Count -ne 2) {throw 'Review tabs not restored'}
    if(!$step.Restore -and ($actual.session.Left.Tabs.Count -ne 1 -or $actual.session.Right.Tabs.Count -ne 1)) {throw 'Restore-disabled did not use initial tabs'}
    $results += @{Step=$step.Name;Passed=$true;Mode=$step.Mode;Restore=$step.Restore}
    $results | ConvertTo-Json | Set-Content (Join-Path $root 'verification.json') -Encoding utf8
    Write-Output "Review restart verified: $($step.Name)"
}

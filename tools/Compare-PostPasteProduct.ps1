param([int]$Seconds=10)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$output=Join-Path $repo 'docs/verification/paste-authorized-20261015/product-comparison'
if(Test-Path -LiteralPath $output){throw 'Use an unused output directory; preserve prior evidence.'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$fixture=Join-Path $repo 'artifacts/render-comparison-fixture'
$product=Join-Path $repo 'src/PaneCommander.App/bin/Release/net10.0-windows/PaneCommander.App.exe'
$monitor=Join-Path $repo 'tools/PaneCommander.RenderMonitor/bin/Release/net10.0-windows/PaneCommander.RenderMonitor.exe'
$manifest=@(Get-ChildItem -LiteralPath (Split-Path $product),(Split-Path $monitor) -File | Where-Object Extension -in '.dll','.exe','.json' | ForEach-Object {@{Path=$_.FullName;Hash=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$manifest|ConvertTo-Json|Set-Content (Join-Path $output 'binary-manifest.json')
@{Started=(Get-Date).ToString('o');Seconds=$Seconds;WarmupSeconds=10;ForcedGC=$false;ActualProduct=$true;GuiInputDuringObservation=$false;GPU=@(Get-CimInstance Win32_VideoController|Select-Object Name,DriverVersion,CurrentHorizontalResolution,CurrentVerticalResolution);PowerPlan=((& powercfg /getactivescheme)-join '')}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output 'conditions.json')
$order=@()
foreach($scenario in @('two-tabs','six-tabs','large')){
    foreach($repeat in 1..3){
        foreach($mode in @('Default','SoftwareOnly')){
            $run=Join-Path $output "$scenario-$mode-$repeat"
            $profile=Join-Path $run 'profile'
            [IO.Directory]::CreateDirectory($profile)|Out-Null
            @{RenderingMode=$(if($mode -eq 'SoftwareOnly'){0}else{1})}|ConvertTo-Json|Set-Content (Join-Path $profile 'settings.json')
            $normal=Join-Path $fixture 'normal'
            $large=Join-Path $fixture 'large'
            $leftTabs=@(@{Path=$(if($scenario -eq 'large'){$large}else{$normal});SortProperty='Name';SortDescending=$false})
            $rightTabs=@(@{Path=$normal;SortProperty='Name';SortDescending=$false})
            if($scenario -eq 'six-tabs'){
                foreach($i in 1..2){$leftTabs+=@{Path=$normal;SortProperty='Name';SortDescending=$false};$rightTabs+=@{Path=$normal;SortProperty='Name';SortDescending=$false}}
            }
            @{Version=1;Left=@{Tabs=$leftTabs;SelectedIndex=0};Right=@{Tabs=$rightTabs;SelectedIndex=0};ActiveSide=0;Layout=@{Left=60;Top=60;Width=1280;Height=800;Maximized=$false;LeftPaneFraction=.5;SidebarWidth=180}}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $profile 'session.json')
            & $monitor product $product $profile $run $Seconds $mode visit-tabs
            if($LASTEXITCODE -ne 0){throw "Product comparison failed: $scenario $mode $repeat"}
            $order+=@{Scenario=$scenario;Mode=$mode;Repeat=$repeat;Finished=(Get-Date).ToString('o')}
            $order|ConvertTo-Json|Set-Content (Join-Path $output 'run-order.json')
            Write-Output "Completed product: $scenario $mode $repeat"
        }
    }
}
$changed=@($manifest|Where-Object {(Get-FileHash -LiteralPath $_.Path).Hash -ne $_.Hash})
if($changed.Count){throw 'Product/monitor binaries changed during comparison.'}
@{FrozenBinariesUnchanged=$true;Finished=(Get-Date).ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $output 'integrity.json')

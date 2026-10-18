param([string]$Output='docs/verification/link-objects')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$prior=$env:PANECOMMANDER_TEST_RENDER_MODE
try {
    foreach($mode in @('SoftwareOnly','Default')) {
        $env:PANECOMMANDER_TEST_RENDER_MODE=$mode
        $directory=Join-Path $Output $mode
        [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($directory,$repo)) | Out-Null
        $log=& dotnet test PaneCommander.sln -c Release --no-build --logger "trx;LogFileName=links-regression.trx" --results-directory $directory 2>&1
        $code=$LASTEXITCODE
        $log | Set-Content (Join-Path $directory 'test-output.txt')
        $log
        if($code -ne 0) {throw "Regression failed: $mode (exit $code)"}
    }
} finally {$env:PANECOMMANDER_TEST_RENDER_MODE=$prior}

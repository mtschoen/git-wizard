$ErrorActionPreference = 'Stop'
# Behavioral contract: both scripts/run-coverage.ps1 collection runs extend vstest's testhost
# shutdown wait, so Coverlet hits flushed at process exit are not cut short (docs/testing.md,
# Coverage collection). A fake dotnet records what each run receives; a stub elevation helper
# starts the elevated runner unelevated with the 100 ms default in its environment, because UAC
# does not hand the caller's environment to the elevated process.
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$root = Join-Path ([IO.Path]::GetTempPath()) ("run-coverage-tests-" + [Guid]::NewGuid().ToString('N'))
$scripts = Join-Path $root 'scripts'
$executables = Join-Path $root 'executables'
$null = New-Item -ItemType Directory -Path $scripts, $executables
try {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'scripts/run-coverage.ps1') -Destination $scripts
    Set-Content -LiteralPath (Join-Path $scripts 'elevation-heads-up.ps1') -Value @'
function Start-ElevatedProcess {
    param([string]$ScriptName, [string]$Reason, [string[]]$ArgumentList)
    $callerValue = $env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT
    $env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT = '100'
    try {
        Start-Process -FilePath (Join-Path $PSHOME 'pwsh') -ArgumentList (@('-NoProfile') + $ArgumentList) -NoNewWindow -Wait -PassThru
    } finally {
        $env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT = $callerValue
    }
}
'@
    Set-Content -LiteralPath (Join-Path $executables 'dotnet.ps1') -Value @'
Add-Content -LiteralPath $env:RUN_COVERAGE_RECORD -Value ("$env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT|" + ($args -join ' '))
$index = [Array]::IndexOf([string[]]$args, '--results-directory')
$report = Join-Path $args[$index + 1] ([Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $report -Force
Set-Content -LiteralPath (Join-Path $report 'coverage.cobertura.xml') -Value '<coverage/>'
exit 0
'@
    Set-Content -LiteralPath (Join-Path $executables 'python.ps1') -Value 'exit 0'

    $record = Join-Path $root 'dotnet-calls.txt'
    $env:RUN_COVERAGE_RECORD = $record
    $env:GITHUB_WORKSPACE = $root
    $env:VSTEST_TESTHOST_SHUTDOWN_TIMEOUT = '100'
    $env:PATH = $executables + [IO.Path]::PathSeparator + $env:PATH
    & (Join-Path $PSHOME 'pwsh') -NoProfile -NonInteractive -File (Join-Path $scripts 'run-coverage.ps1') -NoBuild
    if ($LASTEXITCODE -ne 0) { throw "run-coverage.ps1 exited $LASTEXITCODE" }

    $calls = @(Get-Content -LiteralPath $record)
    if ($calls.Count -ne 2) { throw "Expected the non-admin and elevated dotnet runs, got: $($calls -join ' / ')" }
    foreach ($filter in 'Category!=RequiresAdmin', 'Category=RequiresAdmin') {
        $call = @($calls | Where-Object { $_.Contains("--filter $filter ") })
        if ($call.Count -ne 1) { throw "Expected one run with filter $filter in: $($calls -join ' / ')" }
        if (-not $call[0].StartsWith('120000|test ')) { throw "Run with filter $filter did not extend the shutdown wait: $($call[0])" }
        if (-not $call[0].Contains('--collect:XPlat Code Coverage')) { throw "Run with filter $filter does not collect coverage: $($call[0])" }
    }
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'run-coverage.ps1 extends the testhost shutdown wait on both collection runs.'

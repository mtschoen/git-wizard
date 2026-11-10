$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
function Start-Process { throw 'FunctionsOnly attempted process startup' }
function New-Item { throw 'FunctionsOnly attempted filesystem mutation' }
function Get-Date { throw 'FunctionsOnly entered the attended driver' }
. (Join-Path $repositoryRoot 'scripts/cli-live-smoke.ps1') -FunctionsOnly
$lines = @(
    'ready: mode=journal caught-up=C',
    '2026-10-19T12:00:00.000Z changed: C:\scratch[1]\repo-a',
    'changed: C:\other\repo-a',
    'changed: C:\scratch[1]\repo-b'
)
$result = Find-SmokeLogMatch -Lines $lines -Pattern 'changed: ' -StartLine 1 -PathFilter 'C:\scratch[1]'
if ($result.Line -cne $lines[1] -or $result.NextStartLine -ne 2) { throw 'First match/cursor incorrect' }
if ($result.ClientExited -or $null -ne $result.LastStartupLine) { throw 'Match changed process diagnostics' }
$next = Find-SmokeLogMatch -Lines $lines -Pattern 'changed: ' -StartLine $result.NextStartLine -PathFilter 'C:\scratch[1]'
if ($next.Line -cne $lines[3] -or $next.NextStartLine -ne 4) { throw 'Cursor or literal path filtering incorrect' }
if ($null -ne (Find-SmokeLogMatch -Lines $lines -Pattern 'changed: ' -StartLine 4)) { throw 'Replayed consumed match' }
if ($null -ne (Find-SmokeLogMatch -Lines @() -Pattern 'ready: ')) { throw 'Empty log matched' }
if ($null -ne (Find-SmokeLogMatch -Lines $lines -Pattern '^missing: ')) { throw 'Unmatched log matched' }
$ready = Find-SmokeLogMatch -Lines $lines -Pattern "${script:Stamp}ready: "
if ($ready.Line -cne $lines[0]) { throw 'Untimestamped readiness not recognized' }
$stamped = Find-SmokeLogMatch -Lines @('2026-10-19T12:00:00.000Z ready: mode=journal caught-up=C') -Pattern "${script:Stamp}ready: "
if ($null -eq $stamped) { throw 'Timestamped readiness not recognized' }
Remove-Item Function:Get-Date

# Fake CLI fixture: the banner goes to stderr; startup, ready and event lines go to stdout.
$fixtureDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "cli-live-smoke-test-$([guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($fixtureDirectory) | Out-Null
try {
    $standardOutputLog = Join-Path $fixtureDirectory 'watch-stdout.log'
    $standardErrorLog = Join-Path $fixtureDirectory 'watch-stderr.log'
    [System.IO.File]::WriteAllLines($standardErrorLog, @('[2026-11-10|12:00:00.0000] - Info: git-wizard Session Started'))
    [System.IO.File]::WriteAllLines($standardOutputLog, @(
            '2026-11-10T12:00:01.000Z startup: opening',
            '2026-11-10T12:00:05.000Z ready: mode=journal caught-up=C excluded=-',
            '2026-11-10T12:00:06.000Z changed: C:\scratch\repo-a'))
    $liveProcessId = $PID
    $ready = Wait-ForWatchReady -StandardOutputLog $standardOutputLog -StandardErrorLog $standardErrorLog `
        -ClientProcessId $liveProcessId -BannerTimeoutSeconds 5 -ReadyTimeoutSeconds 5
    if ($ready.ReadyResult.Line -cne '2026-11-10T12:00:05.000Z ready: mode=journal caught-up=C excluded=-') { throw 'stderr banner with stdout ready was not accepted' }
    if ($ready.ReadyResult.NextStartLine -ne 2) { throw 'Ready cursor not positioned after the ready: line in stdout' }

    # Banner only on stdout (the wrong stream) must not satisfy the banner gate.
    [System.IO.File]::WriteAllLines($standardErrorLog, @('unrelated stderr line'))
    [System.IO.File]::WriteAllLines($standardOutputLog, @('[2026-11-10|12:00:00.0000] - Info: git-wizard Session Started'))
    $wrongStreamMessage = $null
    try {
        Wait-ForWatchReady -StandardOutputLog $standardOutputLog -StandardErrorLog $standardErrorLog `
            -ClientProcessId $liveProcessId -BannerTimeoutSeconds 1 -ReadyTimeoutSeconds 1 | Out-Null
    } catch { $wrongStreamMessage = $_.Exception.Message }
    if ($null -eq $wrongStreamMessage -or $wrongStreamMessage -notmatch [regex]::Escape($standardErrorLog) -or $wrongStreamMessage -notmatch 'client still running') { throw "Timeout message did not report the stderr log and the running client: $wrongStreamMessage" }

    # A client that already exited reports the exit, never an empty state.
    $exitedProcess = [System.Diagnostics.Process]::Start((Get-Process -Id $PID).Path, @('-NoProfile', '-Command', 'exit 0'))
    $exitedProcess.WaitForExit()
    $exitedMessage = $null
    try {
        Wait-ForWatchReady -StandardOutputLog $standardOutputLog -StandardErrorLog $standardErrorLog `
            -ClientProcessId $exitedProcess.Id -BannerTimeoutSeconds 5 -ReadyTimeoutSeconds 1 | Out-Null
    } catch { $exitedMessage = $_.Exception.Message }
    if ($null -eq $exitedMessage -or $exitedMessage -notmatch 'client exited before the session-started banner') { throw "Exited-client message not reported: $exitedMessage" }
} finally {
    [System.IO.Directory]::Delete($fixtureDirectory, $true)
}
Write-Host 'CLI smoke helper tests passed.'

#Requires -Version 7
<#
.SYNOPSIS
    Drives the CLI live smoke test for git-wizard --watch against a real, elevated broker.

.DESCRIPTION
    This is the scripted form of the manual CLI pass documented in
    docs/manual-test-plan.md ("CLI live smoke (run first)"). It exercises repository
    discovery and USN-journal-backed --watch change detection end to end: preflight,
    starting the watch, editing a pre-existing repository, creating/editing a repository
    during the watch, renaming a tracked repository, deleting it, and stopping the client
    cleanly. Each step prints PASS or FAIL with an evidence line and is also recorded to
    the evidence directory.

    This needs an INTERACTIVE session. --watch self-elevates one broker child process,
    which means exactly one UAC prompt per run; the script prints a reminder right before
    it expects that prompt and waits up to -UacTimeoutSeconds for the broker to appear.

    Nothing is written into the repository working tree. Scratch repositories live under
    <ScratchDrive>:\gw-cli-smoke-<timestamp> and are removed at the end of the run. Logs and
    a summary are written under -EvidenceDirectory (default: a timestamped folder under the
    current user's profile).

    This script never edits the real ~/.GitWizard configuration. It filters the watch
    output to lines mentioning the scratch root instead of adding ignoredPaths entries, so
    other accounts' repositories (act_runner CI caches, etc.) never need to be excluded by
    editing config.json.

.PARAMETER CliPath
    Path to the built git-wizard CLI executable. Defaults to the Release build output
    relative to the repository root: git-wizard/bin/Release/net10.0/git-wizard.exe. Build it
    first with .\init.ps1 -Build if it does not exist yet.

.PARAMETER ScratchDrive
    Drive letter (no colon) under which the scratch repositories are created. Defaults to C.

.PARAMETER UacTimeoutSeconds
    How long to wait for the interactive UAC prompt to be approved and the elevated broker
    child process to appear. Defaults to 300 seconds.

.PARAMETER EvidenceDirectory
    Directory to write watch stdout/stderr logs and the run summary. Defaults to
    $env:USERPROFILE\git-wizard-cli-smoke\<yyyyMMdd-HHmmss>.

.PARAMETER DryRun
    Print the resolved parameters and the planned steps, then exit without touching the
    filesystem or starting any process.

.EXAMPLE
    .\scripts\cli-live-smoke.ps1 -DryRun

.EXAMPLE
    .\scripts\cli-live-smoke.ps1
#>

[CmdletBinding()]
param(
    [string]$CliPath,
    [string]$ScratchDrive = "C",
    [int]$UacTimeoutSeconds = 300,
    [int]$StartupTimeoutSeconds = 600,
    [string]$EvidenceDirectory,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$script:GitIdentityArguments = @(
    "-c", "user.name=git-wizard-cli-smoke",
    "-c", "user.email=git-wizard-cli-smoke@example.invalid"
)
$script:StepResults = @()
$script:Stamp = '^(?:\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z )?'

function New-SmokeRepository {
    param([Parameter(Mandatory)][string]$RepositoryPath)

    New-Item -ItemType Directory -Path $RepositoryPath -Force | Out-Null
    & git init --quiet $RepositoryPath
    if ($LASTEXITCODE -ne 0) { throw "git init failed for $RepositoryPath (exit $LASTEXITCODE)" }

    $trackedFile = Join-Path $RepositoryPath "tracked.txt"
    Set-Content -LiteralPath $trackedFile -Value "initial content" -NoNewline

    & git -C $RepositoryPath add tracked.txt
    if ($LASTEXITCODE -ne 0) { throw "git add failed for $RepositoryPath (exit $LASTEXITCODE)" }

    & git -C $RepositoryPath @script:GitIdentityArguments commit --quiet -m "initial commit"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed for $RepositoryPath (exit $LASTEXITCODE)" }
}

function Update-SmokeRepository {
    param([Parameter(Mandatory)][string]$RepositoryPath)

    $trackedFile = Join-Path $RepositoryPath "tracked.txt"
    Add-Content -LiteralPath $trackedFile -Value "edit $(Get-Date -Format o)"

    & git -C $RepositoryPath add tracked.txt
    if ($LASTEXITCODE -ne 0) { throw "git add failed for $RepositoryPath (exit $LASTEXITCODE)" }

    & git -C $RepositoryPath @script:GitIdentityArguments commit --quiet -m "smoke edit"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed for $RepositoryPath (exit $LASTEXITCODE)" }
}

function Wait-ForBrokerProcess {
    param(
        [Parameter(Mandatory)][int]$ClientProcessId,
        [int]$TimeoutSeconds = 0
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $broker = Get-CimInstance -ClassName Win32_Process `
            -Filter "Name='git-wizard.exe' AND ParentProcessId=$ClientProcessId" `
            -ErrorAction SilentlyContinue
        if ($broker) { return $broker }
        if ($TimeoutSeconds -gt 0) { Start-Sleep -Milliseconds 500 }
    } while ((Get-Date) -lt $deadline)

    return $null
}

function Get-LastStartupLine {
    param([Parameter(Mandatory)][string]$LogPath)

    if (Test-Path -LiteralPath $LogPath) {
        $lines = @(Get-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue)
        for ($i = $lines.Count - 1; $i -ge 0; $i--) {
            if ($lines[$i] -match "${script:Stamp}(startup|drive): ") {
                return $lines[$i]
            }
        }
    }
    return "<none>"
}

# Polls LogPath for a line matching Pattern, starting at line index StartLine (0-based) so
# repeated waits over the same growing log never re-match an earlier, already-consumed line.
# When PathFilter is set, a line is only considered if it also contains that substring - this
# is how the scratch root is isolated from other accounts' repositories in the same stream,
# without editing the real ~/.GitWizard/config.json ignoredPaths.
function Wait-ForLogPattern {
    param(
        [Parameter(Mandatory)][string]$LogPath,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][int]$TimeoutSeconds,
        [int]$StartLine = 0,
        [string]$PathFilter,
        [int]$ClientProcessId = 0
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $LogPath) {
            $lines = @(Get-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue)
            for ($index = $StartLine; $index -lt $lines.Count; $index++) {
                $line = $lines[$index]
                $content = $line -replace $script:Stamp, ''
                if ($PathFilter -and $content -notmatch [regex]::Escape($PathFilter)) { continue }
                if ($line -match $Pattern) {
                    return [pscustomobject]@{
                        Line = $line
                        NextStartLine = $index + 1
                        ClientExited = $false
                        LastStartupLine = $null
                    }
                }
            }
        }
        if ($ClientProcessId -gt 0 -and -not (Get-Process -Id $ClientProcessId -ErrorAction SilentlyContinue)) {
            return [pscustomobject]@{
                Line = $null
                NextStartLine = $StartLine
                ClientExited = $true
                LastStartupLine = (Get-LastStartupLine -LogPath $LogPath)
            }
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    return $null
}

function Add-StepResult {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][ValidateSet("PASS", "FAIL")][string]$Status,
        [Parameter(Mandatory)][string]$Evidence,
        [string]$Notes
    )

    $script:StepResults += [pscustomobject]@{
        Step     = $Step
        Status   = $Status
        Evidence = $Evidence
        Notes    = $Notes
    }

    $color = if ($Status -eq "PASS") { "Green" } else { "Red" }
    Write-Host "[$Status] $Step" -ForegroundColor $color
    Write-Host "    $Evidence"
    if ($Notes) { Write-Host "    $Notes" }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $CliPath) {
    $CliPath = Join-Path -Path $repositoryRoot -ChildPath "git-wizard" `
        -AdditionalChildPath "bin", "Release", "net10.0", "git-wizard.exe"
}
if (-not $EvidenceDirectory) {
    $EvidenceDirectory = Join-Path $env:USERPROFILE "git-wizard-cli-smoke\$(Get-Date -Format 'yyyyMMdd-HHmmss')"
}
$scratchRoot = "${ScratchDrive}:\gw-cli-smoke-$(Get-Date -Format 'yyyyMMdd-HHmmss')"

if ($DryRun) {
    Write-Host "DRY RUN - no processes will be started and nothing will be written to disk."
    Write-Host "CliPath:               $CliPath"
    Write-Host "ScratchDrive:          $ScratchDrive"
    Write-Host "ScratchRoot:           $scratchRoot"
    Write-Host "UacTimeoutSeconds:     $UacTimeoutSeconds"
    Write-Host "StartupTimeoutSeconds: $StartupTimeoutSeconds"
    Write-Host "EvidenceDirectory:     $EvidenceDirectory"
    Write-Host ""
    Write-Host "Planned steps:"
    Write-Host "  0. Preflight: create the scratch root and repo-a (git init + one commit) before starting the watch."
    Write-Host "  1. Start 'git-wizard --watch -v' in the background; wait for the session-started banner and"
    Write-Host "     wait for 'ready:' with -StartupTimeoutSeconds; the broker child is reported as evidence only"
    Write-Host "     (one interactive UAC prompt)."
    Write-Host "  2. Edit repo-a (created before the watch) and commit; expect 'changed: ...repo-a'."
    Write-Host "     Regression gw#219: fresh startup discovery must track repo-a despite the older cached list."
    Write-Host "  3. Create repo-b during the watch, then edit it; expect 'created: ...repo-b' then 'changed: ...repo-b' (gw#205)."
    Write-Host "  4. Rename repo-b to repo-b2, then edit it; expect 'deleted: ...repo-b', 'created: ...repo-b2', then"
    Write-Host "     'changed: ...repo-b2' (gw#206, gw#209)."
    Write-Host "  5. Delete repo-b2; expect 'deleted: ...repo-b2'."
    Write-Host "  6. Stop the client; expect the broker to exit within 10s and no new Application Error/Critical"
    Write-Host "     event log entry mentioning git-wizard since the watch started."
    Write-Host "  7. Remove the scratch root; print a PASS/FAIL summary with the evidence directory path."
    exit 0
}

if (-not (Test-Path -LiteralPath $CliPath)) {
    throw "CLI executable not found at '$CliPath'. Build it first: .\init.ps1 -Build (Release|x64)."
}

New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$stdOutLog = Join-Path $EvidenceDirectory "watch-stdout.log"
$stdErrLog = Join-Path $EvidenceDirectory "watch-stderr.log"

$client = $null
$broker = $null
$watchStartTime = $null
$logCursor = 0
$repoA = Join-Path $scratchRoot "repo-a"
$repoB = Join-Path $scratchRoot "repo-b"
$repoB2 = Join-Path $scratchRoot "repo-b2"

try {
    try {
        New-Item -ItemType Directory -Path $scratchRoot -Force | Out-Null
        New-SmokeRepository -RepositoryPath $repoA
        Add-StepResult -Step "0. Preflight" -Status "PASS" `
            -Evidence "scratch root: $scratchRoot; repo-a created and committed at $repoA"
    } catch {
        Add-StepResult -Step "0. Preflight" -Status "FAIL" -Evidence $_.Exception.Message
        throw
    }

    Write-Host "`nApprove the UAC prompt now: git-wizard --watch self-elevates one broker process." -ForegroundColor Yellow
    $watchStartTime = Get-Date
    $client = Start-Process -FilePath $CliPath -ArgumentList @("--watch", "-v") `
        -RedirectStandardOutput $stdOutLog -RedirectStandardError $stdErrLog -PassThru -NoNewWindow
    try {
        $bannerResult = Wait-ForLogPattern -LogPath $stdOutLog -Pattern "git-wizard Session Started" `
            -TimeoutSeconds $UacTimeoutSeconds -ClientProcessId $client.Id
        if (-not $bannerResult -or $bannerResult.ClientExited) {
            throw "session-started banner did not appear in $stdOutLog within $UacTimeoutSeconds s (client exited: $($bannerResult.ClientExited))"
        }
        $logCursor = $bannerResult.NextStartLine

        # The readiness wait below performs NO broker lookup: it only checks the ready: log
        # line and the client's exit, so a slow Get-CimInstance query can never land on a poll
        # and delay either gate. Broker existence is diagnostic only (gw#247), and identifying
        # it is deferred until after the outcome is already determined and printed, below.
        $readyResult = Wait-ForLogPattern -LogPath $stdOutLog -Pattern "${script:Stamp}ready: " `
            -TimeoutSeconds $StartupTimeoutSeconds -StartLine $logCursor -ClientProcessId $client.Id
        $readyOutcomeTime = Get-Date

        if (-not $readyResult) {
            $lastPhase = Get-LastStartupLine -LogPath $stdOutLog
            throw "no 'ready:' line within $StartupTimeoutSeconds s; last startup phase: $lastPhase"
        }
        if ($readyResult.ClientExited) {
            throw "client exited before 'ready:'; last startup phase: $($readyResult.LastStartupLine); stderr: $stdErrLog"
        }
        $logCursor = $readyResult.NextStartLine
        $readySeconds = [int]($readyOutcomeTime - $watchStartTime).TotalSeconds

        Add-StepResult -Step "1. Start watch (ready)" -Status "PASS" `
            -Evidence "client pid $($client.Id); $($readyResult.Line); start-to-ready ${readySeconds}s"
    } catch {
        Add-StepResult -Step "1. Start watch (ready)" -Status "FAIL" -Evidence $_.Exception.Message
        throw
    }

    try {
        Update-SmokeRepository -RepositoryPath $repoA
        $result = Wait-ForLogPattern -LogPath $stdOutLog -Pattern "${script:Stamp}changed: .*repo-a$" `
            -TimeoutSeconds 30 -StartLine $logCursor -PathFilter $scratchRoot
        if ($result) {
            $logCursor = $result.NextStartLine
            Add-StepResult -Step "2. Edit repo-a (existed before the watch)" -Status "PASS" -Evidence $result.Line `
                -Notes "Regression gw#219: a repository created after the cache write was tracked at watch startup."
        } else {
            Add-StepResult -Step "2. Edit repo-a (existed before the watch)" -Status "FAIL" `
                -Evidence "no 'changed: ...repo-a' line within 30s" `
                -Notes "Event not observed within 30s after ready:; inspect $stdOutLog for a late 'changed:' line before deciding between a discovery regression (gw#219) and a delivery-latency problem."
        }
    } catch {
        Add-StepResult -Step "2. Edit repo-a (existed before the watch)" -Status "FAIL" -Evidence $_.Exception.Message
    }

    # Broker identification is diagnostic only (gw#247) and runs exactly once, here, after the
    # readiness-gated step-2 edit has been made and observed, so a slow Get-CimInstance query can
    # delay neither the ready/exit report nor the edit that follows the marker. It lands inside
    # the 8-second spacing before step 3, and the PID it finds feeds the step-6 shutdown check.
    $broker = Wait-ForBrokerProcess -ClientProcessId $client.Id
    $brokerNote = if ($broker) { "broker pid $($broker.ProcessId)" } else { "no broker child observed" }
    Write-Host "    $brokerNote"
    Start-Sleep -Seconds 8

    try {
        New-SmokeRepository -RepositoryPath $repoB
        $createdResult = Wait-ForLogPattern -LogPath $stdOutLog -Pattern "${script:Stamp}created: .*repo-b$" `
            -TimeoutSeconds 60 -StartLine $logCursor -PathFilter $scratchRoot
        if (-not $createdResult) { throw "no 'created: ...repo-b' line within 60s (gw#205 regression?)" }
        $logCursor = $createdResult.NextStartLine

        Start-Sleep -Seconds 8
        Update-SmokeRepository -RepositoryPath $repoB
        $changedResult = Wait-ForLogPattern -LogPath $stdOutLog -Pattern "${script:Stamp}changed: .*repo-b$" `
            -TimeoutSeconds 30 -StartLine $logCursor -PathFilter $scratchRoot
        if (-not $changedResult) { throw "no 'changed: ...repo-b' line within 30s" }
        $logCursor = $changedResult.NextStartLine

        Add-StepResult -Step "3. Create + edit repo-b during the watch" -Status "PASS" `
            -Evidence "$($createdResult.Line); $($changedResult.Line)"
    } catch {
        Add-StepResult -Step "3. Create + edit repo-b during the watch" -Status "FAIL" -Evidence $_.Exception.Message `
            -Notes "Related: gw#205"
    }
    Start-Sleep -Seconds 8

    try {
        Rename-Item -LiteralPath $repoB -NewName "repo-b2"

        $deletedResult = Wait-ForLogPattern -LogPath $stdOutLog -Pattern "${script:Stamp}deleted: .*repo-b$" `
            -TimeoutSeconds 60 -StartLine $logCursor -PathFilter $scratchRoot
        if (-not $deletedResult) { throw "no 'deleted: ...repo-b' line within 60s after rename (gw#206/#209 regression?)" }
        $logCursor = $deletedResult.NextStartLine

        $createdResult = Wait-ForLogPattern -LogPath $stdOutLog -Pattern "${script:Stamp}created: .*repo-b2$" `
            -TimeoutSeconds 30 -StartLine $logCursor -PathFilter $scratchRoot
        if (-not $createdResult) { throw "no 'created: ...repo-b2' line within 30s after rename" }
        $logCursor = $createdResult.NextStartLine

        Start-Sleep -Seconds 8
        Update-SmokeRepository -RepositoryPath $repoB2
        $changedResult = Wait-ForLogPattern -LogPath $stdOutLog -Pattern "${script:Stamp}changed: .*repo-b2$" `
            -TimeoutSeconds 30 -StartLine $logCursor -PathFilter $scratchRoot
        if (-not $changedResult) { throw "no 'changed: ...repo-b2' line within 30s" }
        $logCursor = $changedResult.NextStartLine

        Add-StepResult -Step "4. Rename repo-b to repo-b2, then edit" -Status "PASS" `
            -Evidence "$($deletedResult.Line); $($createdResult.Line); $($changedResult.Line)"
    } catch {
        Add-StepResult -Step "4. Rename repo-b to repo-b2, then edit" -Status "FAIL" -Evidence $_.Exception.Message `
            -Notes "Related: gw#206, gw#209"
    }
    Start-Sleep -Seconds 8

    try {
        Remove-Item -LiteralPath $repoB2 -Recurse -Force
        $deletedResult = Wait-ForLogPattern -LogPath $stdOutLog -Pattern "${script:Stamp}deleted: .*repo-b2$" `
            -TimeoutSeconds 60 -StartLine $logCursor -PathFilter $scratchRoot
        if (-not $deletedResult) { throw "no 'deleted: ...repo-b2' line within 60s" }
        $logCursor = $deletedResult.NextStartLine

        Add-StepResult -Step "5. Delete repo-b2" -Status "PASS" -Evidence $deletedResult.Line
    } catch {
        Add-StepResult -Step "5. Delete repo-b2" -Status "FAIL" -Evidence $_.Exception.Message
    }

    try {
        Stop-Process -Id $client.Id -Force -ErrorAction SilentlyContinue

        $brokerExited = $false
        $deadline = (Get-Date).AddSeconds(10)
        do {
            if (-not (Get-Process -Id $broker.ProcessId -ErrorAction SilentlyContinue)) { $brokerExited = $true; break }
            Start-Sleep -Milliseconds 250
        } while ((Get-Date) -lt $deadline)
        if (-not $brokerExited) { throw "broker pid $($broker.ProcessId) did not exit within 10s of client termination" }

        $badEvents = @(Get-WinEvent -FilterHashtable @{
                LogName   = "Application"
                Level     = 1, 2
                StartTime = $watchStartTime
            } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match "git-wizard" })
        if ($badEvents.Count -gt 0) {
            throw "$($badEvents.Count) Application Error/Critical event(s) mentioning git-wizard since the watch started"
        }

        Add-StepResult -Step "6. Stop client" -Status "PASS" `
            -Evidence "broker exited within 10s; 0 Application Error/Critical events mentioning git-wizard"
    } catch {
        Add-StepResult -Step "6. Stop client" -Status "FAIL" -Evidence $_.Exception.Message
    }
} catch {
    # A preflight or watch-start failure was already recorded above via Add-StepResult; stop
    # running further steps that depend on a scratch root or a live watch, and fall through
    # to cleanup instead of letting the exception propagate.
    Write-Verbose "Aborting remaining steps after: $($_.Exception.Message)"
} finally {
    if ($client -and (Get-Process -Id $client.Id -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $client.Id -Force -ErrorAction SilentlyContinue
    }
    if ($broker -and (Get-Process -Id $broker.ProcessId -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $broker.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $scratchRoot) {
        Remove-Item -LiteralPath $scratchRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "`n=== Summary ===" -ForegroundColor Cyan
    $script:StepResults | Format-Table -Property Step, Status, Evidence, Notes -AutoSize | Out-String | Write-Host
    Write-Host "Evidence directory: $EvidenceDirectory"
}

$failureCount = @($script:StepResults | Where-Object { $_.Status -eq "FAIL" }).Count
exit ($(if ($failureCount -gt 0) { 1 } else { 0 }))

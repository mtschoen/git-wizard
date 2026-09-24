#Requires -Version 7

<#
.SYNOPSIS
    Drives the GitWizardUI Live button through UI Automation and times the
    cold/warm start-to-ready transitions recorded in the manual test plan.

.DESCRIPTION
    This is the scripted driver behind the UI rows in
    docs/manual-test-plan.md ("Start-to-caught-up timing"). It launches
    GitWizardUI, presses the Live button through UI Automation (no mouse
    movement, no focus steal), polls the toolbar's readiness and progress
    text, and prints a wall-clock timestamp for every phase transition it
    observes. It also checks that the readiness text and each per-drive
    entry stay inside the window's bounding rectangle, first at the
    window's current width and again after resizing to 1100 physical
    pixels.

    A cold run's index cache clear must fully succeed; a partial clear
    throws instead of measuring a run that started from an unknown cache
    state. The script also throws, after Live reaches Ready, if the
    layout check finds an element outside the window at either width,
    or if either width inspects no matching elements at all; the two
    widths are checked independently, so one width inspecting nothing
    is never papered over by the other width finding elements.
    GitWizardUI is closed with a WM_CLOSE
    message, waited on, and stopped with Stop-Process as a fallback, from
    a finally block that runs whether the run succeeded or threw, so a
    failed run does not leave the process open and blocking the next
    invocation's single-instance guard.

    This needs an INTERACTIVE session with a real desktop: Live
    self-elevates one broker child process, which means exactly one UAC
    prompt per run. The script prints a reminder right before it expects
    that prompt.

.PARAMETER Kind
    Whether this run measures a cold start or a warm start. 'cold' clears
    the MFT index cache (via -CliExe --clear-index-cache --silent)
    immediately before pressing Live, and throws if that clear does not
    fully succeed. 'warm' presses Live with the cache left in place from
    a prior run.

.PARAMETER UiExe
    Path to the built GitWizardUI executable.

.PARAMETER CliExe
    Path to the built git-wizard CLI executable. Used only for the 'cold'
    kind, to clear the index cache before the timed run.

.PARAMETER StartupTimeoutSeconds
    How long to wait, after pressing Live, for the toolbar readiness text
    to read "Ready (...". Defaults to 600 seconds.

.EXAMPLE
    .\scripts\ui-live-timing.ps1 -Kind cold -UiExe .\GitWizardUI\bin\Release\net10.0\GitWizardUI.exe -CliExe .\git-wizard\bin\Release\net10.0\git-wizard.exe

.EXAMPLE
    .\scripts\ui-live-timing.ps1 -Kind warm -UiExe .\GitWizardUI\bin\Release\net10.0\GitWizardUI.exe -CliExe .\git-wizard\bin\Release\net10.0\git-wizard.exe
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('cold', 'warm')] [string] $Kind,
    [Parameter(Mandatory)] [string] $UiExe,
    [Parameter(Mandatory)] [string] $CliExe,
    [int] $StartupTimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$native = Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
'@ -Name Native -Namespace UiTiming -PassThru
$null = $native::SetProcessDPIAware()

function Stamp { (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffZ') }
function Find-ById($root, [string] $id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty), $id
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-ByName($root, [string] $name, $controlType) {
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $name
    $typeCondition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), $controlType
    $condition = New-Object System.Windows.Automation.AndCondition @($nameCondition, $typeCondition)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

# Closes the UI process with WM_CLOSE, waits for it to exit, and falls back to Stop-Process if it
# does not. Called from the finally block below so a failure anywhere after the process starts
# still leaves it closed, instead of stuck open and blocking the next run's single-instance guard.
function Close-UiProcess([System.Diagnostics.Process] $targetProcess) {
    $targetProcess.Refresh()
    if ($targetProcess.HasExited) { return }
    if ($targetProcess.MainWindowHandle -eq 0) {
        Stop-Process -Id $targetProcess.Id -Force -ErrorAction SilentlyContinue
        return
    }
    $null = $native::PostMessage($targetProcess.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if ($targetProcess.WaitForExit(30000)) {
        "$(Stamp) window closed"
        return
    }
    "window did not close within 30 s of WM_CLOSE; stopping the process"
    Stop-Process -Id $targetProcess.Id -Force -ErrorAction SilentlyContinue
}

$process = Start-Process -FilePath $UiExe -PassThru
try {
    $deadline = (Get-Date).AddSeconds(60)
    while ($process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250; $process.Refresh() }
    if ($process.MainWindowHandle -eq 0) { throw 'main window never appeared' }
    "$(Stamp) window up, pid $($process.Id)"
    Start-Sleep -Seconds 3
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)

    $button = Find-ById $root 'LiveButton'
    if (-not $button) { $button = Find-ByName $root 'Live' ([System.Windows.Automation.ControlType]::Button) }
    if (-not $button) { throw 'Live button not found through UI Automation' }
    $invoke = $null
    if (-not $button.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref] $invoke)) { throw 'Live button has no Invoke pattern' }

    # The startup refresh disables Live until it finishes; wait for the button to enable.
    $deadline = (Get-Date).AddSeconds(300)
    while (-not $button.Current.IsEnabled -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (-not $button.Current.IsEnabled) { throw 'Live button never enabled (startup refresh still running after 300 s)' }
    "$(Stamp) Live button enabled (startup refresh finished)"

    if ($Kind -eq 'cold') {
        $clearOutput = & $CliExe --clear-index-cache --silent 2>&1
        $clearExitCode = $LASTEXITCODE
        $clearSummary = ($clearOutput | Out-String).Trim() -replace '\s+', ' '
        if ($clearExitCode -ne 0) {
            throw "index cache clear did not fully succeed (exit $clearExitCode): $clearSummary; a cold run needs a clean clear, not a partial one"
        }
        "$(Stamp) cache cleared (exit $clearExitCode): $clearSummary"
    }

    "Approve the UAC prompt now: Live self-elevates one broker process."
    $startStamp = Stamp
    $startTime = Get-Date
    $invoke.Invoke()
    "$startStamp LIVE PRESSED"

    $lastProgress = ''
    $lastReadiness = ''
    $readyStamp = $null
    $deadline = $startTime.AddSeconds($StartupTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw 'UI process exited during Live startup' }
        $progressElement = Find-ById $root 'LiveScanProgressText'
        $progress = if ($progressElement) { $progressElement.Current.Name } else { '' }
        if ($progress -ne $lastProgress -and $progress) { "$(Stamp) progress: $progress"; $lastProgress = $progress }
        $readinessElement = Find-ById $root 'LiveReadinessText'
        $readiness = if ($readinessElement) { $readinessElement.Current.Name } else { '' }
        if ($readiness -ne $lastReadiness -and $readiness) { "$(Stamp) readiness: $readiness"; $lastReadiness = $readiness }
        if ($readiness -like 'Ready (*') { $readyStamp = Stamp; $readyTime = Get-Date; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $readyStamp) { throw "no Ready text within $StartupTimeoutSeconds s; last progress '$lastProgress', last readiness '$lastReadiness'" }
    $elapsed = [math]::Round(($readyTime - $startTime).TotalSeconds, 1)
    "RESULT kind=$Kind start=$startStamp ready=$readyStamp elapsed=${elapsed}s text='$lastReadiness'"

    # Layout check at the window's current width and again at 1100 physical pixels (narrower than the
    # declared 1100 logical default on a scaled display): every readiness or per-drive Text element
    # must lie inside the window's bounding rectangle. Test-Layout resets its inspected/failed counts
    # at the start of every call, and the caller snapshots them right after each call, so the two
    # widths are tracked independently: a width that inspects nothing must fail on its own, not be
    # papered over by a passing count carried in from the other width.
    $textCondition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty), ([System.Windows.Automation.ControlType]::Text)
    $layoutInspected = 0
    $layoutFailures = 0
    function Test-Layout([string] $label) {
        $script:layoutInspected = 0
        $script:layoutFailures = 0
        $windowRect = $root.Current.BoundingRectangle
        "$label window rect: x=$([int]$windowRect.X) right=$([int]$windowRect.Right) width=$([int]$windowRect.Width)"
        foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)) {
            $name = $element.Current.Name
            if ($name -notmatch '^(Ready \(|[A-Z]: (catching up|caught up))') { continue }
            $script:layoutInspected++
            $rect = $element.Current.BoundingRectangle
            $inside = ($rect.Right -le $windowRect.Right) -and ($rect.X -ge $windowRect.X)
            if (-not $inside) { $script:layoutFailures++ }
            "$label layout: '$name' x=$([int]$rect.X) y=$([int]$rect.Y) right=$([int]$rect.Right) width=$([int]$rect.Width) result=$(if ($inside) { 'PASS' } else { 'FAIL' })"
        }
    }

    Test-Layout 'wide'
    $wideInspected = $layoutInspected
    $wideFailures = $layoutFailures

    $setWindowPos = Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);' -Name Pos -Namespace UiTiming -PassThru
    $null = $setWindowPos::SetWindowPos($process.MainWindowHandle, [IntPtr]::Zero, 0, 0, 1100, 800, 0x0016)
    Start-Sleep -Seconds 2
    Test-Layout 'narrow'
    $narrowInspected = $layoutInspected
    $narrowFailures = $layoutFailures

    "layout elements inspected: wide=$wideInspected narrow=$narrowInspected; layout failures: wide=$wideFailures narrow=$narrowFailures"

    if ($wideInspected -eq 0) {
        throw "layout check at width 'wide' inspected no matching readiness/per-drive text elements; the check did not run"
    }
    if ($narrowInspected -eq 0) {
        throw "layout check at width 'narrow' inspected no matching readiness/per-drive text elements; the check did not run"
    }
    if ($wideFailures -gt 0 -or $narrowFailures -gt 0) {
        throw "$($wideFailures + $narrowFailures) layout check(s) failed (wide=$wideFailures narrow=$narrowFailures); see the 'layout: ... result=FAIL' line(s) above"
    }
}
finally {
    Start-Sleep -Seconds 2
    Close-UiProcess $process
}

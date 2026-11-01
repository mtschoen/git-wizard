# Heads-up dialog and the single elevated launch for the repository scripts. Dot-source this file;
# every script that needs administrator rights starts its elevated process through
# Start-ElevatedProcess so no UAC prompt appears without the dialog first (docs/testing.md).

# Popup return codes (WScript.Shell): 1 OK, 2 Cancel, -1 timeout.
function Invoke-ElevationPopup {
    param([string]$Message, [string]$Title, [int]$TimeoutSeconds)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $shell = New-Object -ComObject WScript.Shell
    # 1 OK/Cancel + 64 information icon (plays its sound) + 4096 system modal (takes foreground).
    $result = $shell.Popup($Message, $TimeoutSeconds, $Title, 1 + 64 + 4096)
    return [pscustomobject]@{ Result = [int]$result; ElapsedSeconds = $clock.Elapsed.TotalSeconds }
}

# System-modal heads-up before the UAC prompt: a dismissal within 0.75 seconds is shown again;
# five minutes unanswered counts as Cancel. Returns $true only for OK.
function Confirm-ElevationHeadsUp {
    param([string]$ScriptName, [string]$Reason, [string]$Command)
    $message = "$ScriptName needs administrator rights to $Reason.`n`n" +
        "A Windows UAC prompt follows this message.`n`nElevated command:`n$Command`n`n" +
        'OK continues to the UAC prompt. Cancel stops without elevating.'
    while ($true) {
        $answer = Invoke-ElevationPopup $message "$ScriptName : UAC prompt next" 300
        if ($answer.ElapsedSeconds -lt 0.75 -and $answer.Result -in 1, 2) { continue }
        return $answer.Result -eq 1
    }
}

# The only elevated launch under scripts/. Throws (so callers exit nonzero) when the dialog is
# cancelled or times out; the UAC prompt is never raised in that case.
function Start-ElevatedProcess {
    param([string]$ScriptName, [string]$Reason, [string[]]$ArgumentList, [string]$FilePath = 'powershell')
    if ($env:MFTLIB_SAMPLE_UNATTENDED -eq '1') {
        throw 'Running unattended (MFTLIB_SAMPLE_UNATTENDED=1): elevation skipped; no heads-up dialog and no UAC prompt was raised. Run attended, or use -NonInteractive.'
    }
    $command = (@($FilePath) + $ArgumentList) -join ' '
    if (-not (Confirm-ElevationHeadsUp $ScriptName $Reason $command)) {
        throw 'Elevation cancelled at the heads-up dialog (Cancel or five minutes unanswered); no UAC prompt was raised.'
    }
    return Start-Process $FilePath -Verb 'runas' -ArgumentList $ArgumentList -PassThru
}

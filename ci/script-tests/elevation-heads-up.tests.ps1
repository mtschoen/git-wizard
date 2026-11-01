$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$scriptsRoot = Join-Path $repositoryRoot 'scripts'
. (Join-Path $scriptsRoot 'elevation-heads-up.ps1')
function Assert-Equal($Actual, $Expected, [string]$Label) {
    if ($Actual -ne $Expected) { throw "$Label : expected '$Expected' but got '$Actual'" }
}
function Assert-Contains([string]$Text, [string]$Expected) {
    if (-not $Text.Contains($Expected)) { throw "Missing '$Expected' in: $Text" }
}

# Popup seam: no UI is shown. Each answer is (result, elapsed seconds).
foreach ($case in @(
    @{ Name = 'OK'; Answers = @(,@(1, 5.0)); Calls = 1; Expected = $true },
    @{ Name = 'Cancel'; Answers = @(,@(2, 5.0)); Calls = 1; Expected = $false },
    @{ Name = 'quick OK reshown'; Answers = @(@(1, 0.2), @(1, 4.0)); Calls = 2; Expected = $true },
    @{ Name = 'quick Cancel reshown'; Answers = @(@(2, 0.2), @(2, 4.0)); Calls = 2; Expected = $false },
    @{ Name = 'timeout is Cancel'; Answers = @(,@(-1, 300.0)); Calls = 1; Expected = $false }
)) {
    & {
        $script:popupAnswers = [Collections.Generic.Queue[object]]::new($case.Answers)
        $script:popupMessages = [Collections.Generic.List[string]]::new()
        function Invoke-ElevationPopup {
            param([string]$Message, [string]$Title, [int]$TimeoutSeconds)
            $script:popupMessages.Add("$Title|$TimeoutSeconds|$Message")
            $answer = $script:popupAnswers.Dequeue()
            [pscustomobject]@{ Result = $answer[0]; ElapsedSeconds = $answer[1] }
        }
        Assert-Equal (Confirm-ElevationHeadsUp 'fixture.ps1' 'run tests' 'tool one two') $case.Expected "heads-up $($case.Name)"
        Assert-Equal $script:popupMessages.Count $case.Calls "heads-up $($case.Name) shown count"
        Assert-Contains $script:popupMessages[0] 'fixture.ps1'
        Assert-Contains $script:popupMessages[0] 'UAC prompt'
        Assert-Contains $script:popupMessages[0] 'tool one two'
        Assert-Contains $script:popupMessages[0] '|300|'
    }
}

# Cancel and timeout stop before any elevated start.
foreach ($result in -1, 2) {
    & {
        function Invoke-ElevationPopup { [pscustomobject]@{ Result = $result; ElapsedSeconds = 5.0 } }
        $message = $null
        try { Start-ElevatedProcess -ScriptName 'fixture.ps1' -Reason 'test' -ArgumentList @('-File', 'x.ps1') -FilePath 'no-such-launcher' } catch { $message = $_.ToString() }
        Assert-Contains $message 'Elevation cancelled'
    }
}

# Unattended mode (MFTLIB_SAMPLE_UNATTENDED=1) fails closed: neither the popup nor the launch happens.
& {
    $script:popupCalls = 0
    $script:launchCalls = 0
    function Invoke-ElevationPopup { $script:popupCalls++; [pscustomobject]@{ Result = 1; ElapsedSeconds = 5.0 } }
    function Start-Process { $script:launchCalls++ }
    $previous = $env:MFTLIB_SAMPLE_UNATTENDED
    $env:MFTLIB_SAMPLE_UNATTENDED = '1'
    $message = $null
    try { Start-ElevatedProcess -ScriptName 'fixture.ps1' -Reason 'test' -ArgumentList @('-File', 'x.ps1') } catch { $message = $_.ToString() }
    $env:MFTLIB_SAMPLE_UNATTENDED = $previous
    Assert-Contains $message 'MFTLIB_SAMPLE_UNATTENDED=1'
    Assert-Equal $script:popupCalls 0 'unattended popup calls'
    Assert-Equal $script:launchCalls 0 'unattended launch calls'
}

# Fixed spelling: the elevated verb occurs only in the helper, once, and run-coverage.ps1 uses it.
$verb = 'run' + 'as'
$helperName = 'elevation-heads-up.ps1'
foreach ($file in Get-ChildItem -LiteralPath $scriptsRoot -Recurse -File -Include *.ps1, *.psm1, *.sh, *.bat, *.cmd) {
    $text = Get-Content -LiteralPath $file.FullName -Raw
    $count = [regex]::Matches($text, $verb, 'IgnoreCase').Count
    Assert-Equal $count ($file.Name -eq $helperName ? 1 : 0) "elevated verb spelling count in $($file.Name)"
}
Assert-Contains (Get-Content -LiteralPath (Join-Path $scriptsRoot 'run-coverage.ps1') -Raw) 'Start-ElevatedProcess'
Write-Host 'elevation-heads-up tests passed'

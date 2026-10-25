$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$generator = Join-Path $repositoryRoot 'scripts/generate-test-report.ps1'
. $generator
$directory = Join-Path ([IO.Path]::GetTempPath()) ("git-wizard-report-tests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $directory | Out-Null
$trx = Join-Path $directory 'TestResults.trx'
$coverage = Join-Path $directory 'coverage.cobertura.xml'
$output = Join-Path $directory 'TEST-REPORT.md'
function New-TrxText([int]$Passed, [int]$Failed, [int]$NotExecuted, [int]$NotExecutedCounter) {
    $total = $Passed + $Failed + $NotExecuted
    $outcomes = @(@('Passed') * $Passed) + @(@('Failed') * $Failed) + @(@('NotExecuted') * $NotExecuted)
    $results = ($outcomes | ForEach-Object { "<UnitTestResult outcome=`"$_`"/>" }) -join ''
    '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions><UnitTest storage="/build/gitwizardtests.dll"><TestMethod codeBase="/build/GitWizardTests.dll"/></UnitTest></TestDefinitions>' +
        "<Results>$results</Results><ResultSummary><Counters total=`"$total`" executed=`"$($total - $NotExecuted)`" passed=`"$Passed`" failed=`"$Failed`" notExecuted=`"$NotExecutedCounter`"/></ResultSummary></TestRun>"
}
New-TrxText 9 1 2 2 | Set-Content $trx
'<coverage line-rate="0.899" branch-rate="0.8504"/>' | Set-Content $coverage
$arguments = @{
    WindowsTrx = @($trx)
    LinuxTrx = @($trx); LinuxCobertura = @($coverage)
    Commit = '0123456789abcdef0123456789abcdef01234567'
    RunId = '42'
    GeneratedAt = '2026-10-24T12:34:56Z'; OutputPath = $output
}
function Assert-Contains([string]$Text, [string]$Expected) {
    if (-not $Text.Contains($Expected)) { throw "Missing report content: $Expected" }
}
function Assert-Rejected([scriptblock]$Action) {
    $caught = $false
    try { & $Action } catch { $caught = $true }
    if (-not $caught) { throw 'Invalid report input was accepted' }
}
$privateDomain = ('fleet', 'sticktoitive', 'net') -join '.'
$privatePatterns = @(
    '(?i)[a-z]:[\\/]+Users(?:[\\/]|\b)',
    '(?i)/mnt/[a-z]/Users(?:/|\b)',
    ('(?i)' + [regex]::Escape($privateDomain)),
    ('(?i)\b' + 'chon' + 'kers\b'),
    ('(?i)\b' + 'llama' + 'box\b'),
    ([regex]::Escape('~/' + 'schoen-lab/'))
)
$culture = [Threading.Thread]::CurrentThread.CurrentCulture
try {
    [Threading.Thread]::CurrentThread.CurrentCulture = 'de-DE'
    & $generator @arguments
    $first = [IO.File]::ReadAllText($output)
    foreach ($expected in @('generated on main by CI', 'must not be edited in a pull request',
        '## Windows', '## Linux', '| GitWizardTests | 12 | 9 | 1 | 2 |',
        '| coverage.cobertura.xml | 89.90% | 85.04% |',
        'Source commit: 0123456789abcdef0123456789abcdef01234567',
        '2026-10-24T12:34:56Z', 'Workflow run: 42')) {
        Assert-Contains $first $expected
    }
    & $generator @arguments
    if ($first -cne [IO.File]::ReadAllText($output)) { throw 'Report is not deterministic' }
    if ([IO.File]::ReadAllBytes($output).Length -ge 4096) { throw 'Report exceeds size budget' }
    if ($first.Contains([char]0x2014)) { throw 'Report contains an em dash' }
    foreach ($pattern in $privatePatterns) {
        if ($first -match $pattern) { throw "Report matches a private-content pattern: $pattern" }
    }
    if ($first -match 'https?://') { throw 'Report embeds a link' }
    foreach ($negative in @(('C:' + '\Users\fixture'), "https://$privateDomain/team/repo", ('llama' + 'box'))) {
        if (-not ($privatePatterns | Where-Object { $negative -match $_ })) { throw 'Private-content negative control not detected' }
    }
    foreach ($invalid in @('<broken', '<coverage/>', '<coverage line-rate="1.01" branch-rate="0.5"/>',
        '<coverage line-rate="NaN" branch-rate="0.5"/>')) {
        $invalid | Set-Content $coverage
        Assert-Rejected { & $generator @arguments }
    }
    '<coverage line-rate="0.899" branch-rate="0.8504"/>' | Set-Content $coverage
    $validTrx = Get-Content $trx -Raw
    # Observed CI shapes: the notExecuted counter is zero while results carry NotExecuted outcomes.
    foreach ($shape in @(@(1725, 0, 80, 1805), @(1778, 0, 27, 1805))) {
        New-TrxText $shape[0] $shape[1] $shape[2] 0 | Set-Content $trx
        & $generator @arguments
        Assert-Contains ([IO.File]::ReadAllText($output)) "| GitWizardTests | $($shape[3]) | $($shape[0]) | $($shape[1]) | $($shape[2]) |"
    }
    foreach ($invalid in @('<TestRun/>', $validTrx.Replace('total="12"', 'total="bad"'),
        $validTrx.Replace('total="12"', 'total="13"'), $validTrx.Replace('passed="9"', 'passed="8"'),
        $validTrx.Replace('failed="1"', 'failed="0"'), $validTrx.Replace('outcome="Failed"', 'outcome="Timeout"'))) {
        $invalid | Set-Content $trx
        Assert-Rejected { & $generator @arguments }
    }
    $validTrx | Set-Content $trx
    $arguments.WindowsTrx = @()
    Assert-Rejected { & $generator @arguments }
    $arguments.WindowsTrx = @($trx, $trx)
    Assert-Rejected { & $generator @arguments }
    $arguments.WindowsTrx = @($trx)
    $arguments.LinuxCobertura = @()
    Assert-Rejected { & $generator @arguments }
    $arguments.LinuxCobertura = @($coverage)
    $arguments.RunId = 'run-42'
    Assert-Rejected { & $generator @arguments }
    $arguments.RunId = '42'
    $arguments.Commit = 'invalid'
    Assert-Rejected { & $generator @arguments }
} finally {
    [Threading.Thread]::CurrentThread.CurrentCulture = $culture
    Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Test report generator self-tests passed.'

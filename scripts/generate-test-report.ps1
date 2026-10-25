# With no arguments, run the offline self-tests. CI supplies explicit run provenance.
param(
    [string[]]$WindowsTrx = @(),
    [string[]]$LinuxTrx = @(),
    [string[]]$LinuxCobertura = @(),
    [string]$Commit,
    [string]$RunId,
    [string]$GeneratedAt,
    [string]$OutputPath = 'TEST-REPORT.md'
)
$ErrorActionPreference = 'Stop'

function ConvertFrom-CoverageRate([string]$Text) {
    if ($Text -notmatch '^\d+(?:\.\d+)?$') { throw 'Invalid Cobertura rate' }
    $rate = [decimal]::Parse($Text, [cultureinfo]::InvariantCulture)
    if ($rate -lt 0 -or $rate -gt 1) { throw 'Invalid Cobertura rate' }
    return ($rate * 100).ToString('F2', [cultureinfo]::InvariantCulture) + '%'
}

function Get-TestReportCounts([string]$Path) {
    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $counters = @($document.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="ResultSummary"]/*[local-name()="Counters"]'))
    if ($counters.Count -ne 1) { throw 'Missing or duplicate TRX Counters' }
    $counts = @{}
    foreach ($name in @('total', 'passed', 'failed')) {
        $text = $counters[0].GetAttribute($name)
        if ($text -notmatch '^\d+$') { throw "Invalid TRX counter: $name" }
        $counts[$name] = [long]::Parse($text, [cultureinfo]::InvariantCulture)
    }
    # The producer leaves the notExecuted counter at zero, so skipped tests come from the per-result outcomes.
    $outcomes = @{ Passed = 0L; Failed = 0L; NotExecuted = 0L }
    foreach ($result in @($document.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="Results"]/*[local-name()="UnitTestResult"]'))) {
        $outcome = $result.GetAttribute('outcome')
        if (-not $outcomes.ContainsKey($outcome)) { throw "Unsupported TRX result outcome: $outcome" }
        $outcomes[$outcome]++
    }
    if ($outcomes.Passed -ne $counts.passed -or $outcomes.Failed -ne $counts.failed -or
        $outcomes.Passed + $outcomes.Failed + $outcomes.NotExecuted -ne $counts.total) {
        throw 'TRX counters do not reconcile with result outcomes'
    }
    $counts.notExecuted = $outcomes.NotExecuted
    $methods = @($document.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="TestDefinitions"]/*[local-name()="UnitTest"]/*[local-name()="TestMethod"]'))
    $sources = if ($methods.Count -gt 0) {
        @($methods | ForEach-Object { $_.GetAttribute('codeBase') })
    } else {
        @($document.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="TestDefinitions"]/*[local-name()="UnitTest"]') | ForEach-Object { $_.GetAttribute('storage') })
    }
    $projects = @($sources | ForEach-Object {
        [IO.Path]::GetFileNameWithoutExtension($_.Replace('\', '/'))
    } | Sort-Object -Unique)
    if ($projects.Count -ne 1 -or [string]::IsNullOrWhiteSpace($projects[0])) {
        throw 'TRX must identify exactly one test project in UnitTest definitions'
    }
    $counts.Project = $projects[0]
    return $counts
}

function Get-TestReportCoverage([string]$Path) {
    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $root = $document.SelectSingleNode('/coverage')
    if ($null -eq $root) { throw 'Missing Cobertura coverage root' }
    return @(
        (ConvertFrom-CoverageRate $root.GetAttribute('line-rate')),
        (ConvertFrom-CoverageRate $root.GetAttribute('branch-rate'))
    )
}

function ConvertTo-TestReportCell([string]$Text) {
    return $Text.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;').Replace('|', '&#124;').Replace("`r", ' ').Replace("`n", ' ')
}

function Add-TestReportPlatform($Lines, [string]$Name, [string[]]$TrxPaths, [string[]]$CoveragePaths, [switch]$RequireCoverage) {
    if ($TrxPaths.Count -eq 0 -or ($RequireCoverage -and $CoveragePaths.Count -eq 0)) { throw "Missing inputs for $Name" }
    $Lines.Add("## $Name")
    $Lines.Add('')
    $Lines.Add('| Test project | Total | Passed | Failed | Skipped |')
    $Lines.Add('| --- | ---: | ---: | ---: | ---: |')
    foreach ($path in ($TrxPaths | Sort-Object)) {
        $counts = Get-TestReportCounts $path
        $project = ConvertTo-TestReportCell $counts.Project
        $Lines.Add("| $project | $($counts.total) | $($counts.passed) | $($counts.failed) | $($counts.notExecuted) |")
    }
    $Lines.Add('')
    if ($CoveragePaths.Count -gt 0) {
        $Lines.Add('| Managed coverage | Line | Branch |')
        $Lines.Add('| --- | ---: | ---: |')
        foreach ($path in ($CoveragePaths | Sort-Object)) {
            $percentages = Get-TestReportCoverage $path
            $label = ConvertTo-TestReportCell ([IO.Path]::GetFileName($path))
            $Lines.Add("| $label | $($percentages[0]) | $($percentages[1]) |")
        }
        $Lines.Add('')
    }
}

if ($MyInvocation.InvocationName -eq '.') { return }
if ($PSBoundParameters.Count -eq 0) {
    & "$PSScriptRoot/../ci/script-tests/generate-test-report.tests.ps1"
    return
}
if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Expected a full source commit SHA' }
if ($RunId -notmatch '^\d+$') { throw 'Expected a numeric workflow run id' }
if ($GeneratedAt -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$') {
    throw 'An ISO 8601 run timestamp with an explicit offset is required'
}
$timestamp = [DateTimeOffset]::Parse($GeneratedAt, [cultureinfo]::InvariantCulture).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [cultureinfo]::InvariantCulture)
foreach ($paths in @(@{ Values = $WindowsTrx }, @{ Values = $LinuxTrx }, @{ Values = $LinuxCobertura })) {
    if (@($paths.Values | Sort-Object -Unique).Count -ne $paths.Values.Count) { throw 'Duplicate report input' }
}
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Test report: generated on main by CI; must not be edited in a pull request')
$lines.Add('')
$lines.Add("Source commit: $Commit")
$lines.Add("Workflow run: $RunId")
$lines.Add("Generated at: $timestamp (workflow run creation time).")
$lines.Add('')
Add-TestReportPlatform $lines 'Windows' $WindowsTrx @()
Add-TestReportPlatform $lines 'Linux' $LinuxTrx $LinuxCobertura -RequireCoverage
$lines.Add('Full TRX and coverage reports are retained as CI artifacts.')
$content = ($lines -join "`n") + "`n"
if ([Text.Encoding]::UTF8.GetByteCount($content) -ge 4096) { throw 'Test report exceeds the 4 KB summary budget' }
if ($content.Contains([char]0x2014)) { throw 'Test report contains an em dash' }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $content, [Text.UTF8Encoding]::new($false))

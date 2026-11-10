# CLI verb matrix: runs every git-wizard CLI command and option combination that needs neither
# administrator rights nor a live NTFS volume, against throwaway git repositories and a throwaway
# state folder, and checks each one's exit code and output. It never shows a UAC prompt, never
# touches the real ~/.GitWizard (GITWIZARD_HOME points at a temporary folder that is deleted at
# the end, including on failure) and never reads the real repositories (every discovering row carries
# --no-mft and uses explicit --paths or the isolated home's seeded search path). It owns
# every process it starts: each launch runs in a job object terminated on every exit path, so a
# timeout, an early parent exit or a failure cannot leave a descendant behind.
#
# It builds nothing. Build a Release git-wizard.exe first (dotnet build git-wizard/git-wizard.csproj -c Release,
# with no -p:Platform, which would move the output), then:
#   pwsh scripts/cli-verb-matrix.ps1
#   pwsh scripts/cli-verb-matrix.ps1 -ExecutablePath <path to git-wizard.exe> -TimeoutSeconds 300
#
# The script fails when the CLI's own help text lists a command or option that the rows in
# scripts/cli-verb-matrix.rows.ps1 neither run nor explicitly exclude, so a new verb cannot ship
# without a matrix row. Exit codes: 0 all rows passed, 1 a row failed or timed out or coverage is
# incomplete, 2 the executable is missing.
[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [ValidateRange(1, 86400)][int]$TimeoutSeconds = 120,
    [string]$TemporaryRoot
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'cli-verb-matrix.helpers.ps1')
. (Join-Path $PSScriptRoot 'cli-verb-matrix.rows.ps1')

if (-not $ExecutablePath) {
    $repositoryRoot = [string](Resolve-Path (Join-Path $PSScriptRoot '..'))
    $executableName = if ($IsWindows) { 'git-wizard.exe' } else { 'git-wizard' }
    $ExecutablePath = Join-Path $repositoryRoot "git-wizard/bin/Release/net10.0/$executableName"
}
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    Write-Host "git-wizard executable not found: $ExecutablePath (build Release first: dotnet build git-wizard/git-wizard.csproj -c Release, or pass -ExecutablePath)" -ForegroundColor Red
    exit 2
}
$ExecutablePath = [string](Resolve-Path -LiteralPath $ExecutablePath)
if (-not $TemporaryRoot) { $TemporaryRoot = [System.IO.Path]::GetTempPath() }

$workName = 'gw-verb-matrix-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$workRoot = Join-Path $TemporaryRoot $workName
$context = @{
    workname = $workName
    home = Join-Path $workRoot 'home'
    out = Join-Path $workRoot 'out'
}
$environment = Get-MatrixGitEnvironment
$environment['GITWIZARD_HOME'] = $context.home
$passed = 0
$failed = 0
$exitCode = 0

try {
    foreach ($directory in @($context.home, $context.out)) { $null = New-Item -ItemType Directory -Path $directory -Force }
    $git = (Get-Command git -CommandType Application | Select-Object -First 1).Source
    $fixture = New-MatrixFixture -Root $workRoot -GitPath $git
    $context.repos = $fixture.Repositories
    $context.clean = $fixture.Clean
    $context.dirty = $fixture.Dirty
    $context.prunable = $fixture.Prunable
    # The seeded configuration confines discovery to the fixture repositories; temporary folders are
    # excluded from discovery unless the configuration includes them.
    $settings = [ordered]@{ SearchPaths = @($context.repos.Replace('\', '/')); includeTempDirectories = $true }
    [System.IO.File]::WriteAllText((Join-Path $context.home 'config.json'), ($settings | ConvertTo-Json))

    $rows = Get-MatrixRows
    $exclusions = Get-MatrixExclusions
    $help = Invoke-MatrixProcess -ExecutablePath $ExecutablePath -Arguments @('--help') -Environment $environment -TimeoutSeconds $TimeoutSeconds
    $helpTexts = @{ '' = $help.Output }
    $usage = $null
    if (-not $help.TimedOut -and $help.ExitCode -eq 0) {
        $commandNames = @((Get-MatrixUsage -HelpTexts $helpTexts).Tokens | Where-Object { $_ -match '^[a-z][a-z-]*$' })
        foreach ($commandName in $commandNames) {
            $commandHelp = Invoke-MatrixProcess -ExecutablePath $ExecutablePath -Arguments @($commandName, '--help') -Environment $environment -TimeoutSeconds $TimeoutSeconds
            $helpTexts[$commandName] = $commandHelp.Output
        }
        $usage = Get-MatrixUsage -HelpTexts $helpTexts
    }
    if ($null -eq $usage -or $usage.Tokens.Count -eq 0) {
        Write-Host "FAIL     usage discovery: --help exit code $($help.ExitCode), no tokens found" -ForegroundColor Red
        $exitCode = 1
    }
    else {
        $coverage = Test-MatrixCoverage -UsageTokens $usage.Tokens -Rows $rows -Exclusions $exclusions -Aliases $usage.Aliases
        if (-not $coverage.Ok) {
            foreach ($problem in $coverage.Problems) { Write-Host "FAIL     coverage: $problem" -ForegroundColor Red }
            $exitCode = 1
        }
    }

    if ($exitCode -eq 0) {
        Write-Host "Executable: $ExecutablePath"
        Write-Host "State folder (GITWIZARD_HOME): $($context.home)"
        Write-Host "Fixture repositories: $($context.repos)"
        Write-Host "Per-command timeout: $TimeoutSeconds s"
        foreach ($row in $rows) {
            $result = Invoke-MatrixRow -Row $row -ExecutablePath $ExecutablePath -Context $context -Environment $environment -TimeoutSeconds $TimeoutSeconds
            Write-MatrixResult $result
            if ($result.Status -eq 'PASS') { $passed++ } else { $failed++ }
            if ($result.Status -eq 'FAIL' -and $result.Fatal) {
                Write-Host 'ABORTED  a fatal row failed; the remaining rows were not run' -ForegroundColor Red
                break
            }
        }
        foreach ($exclusion in $exclusions) {
            Write-Host "EXCLUDED $($exclusion.Token) : $($exclusion.Reason)" -ForegroundColor DarkYellow
        }
        Write-Host "Summary: $passed passed, $failed failed, $($exclusions.Count) excluded, $($rows.Count) rows."
        if ($failed -gt 0 -or ($passed + $failed) -lt $rows.Count) { $exitCode = 1 }
    }
}
catch {
    Write-Host "FAIL     matrix error: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    Stop-MatrixStartedProcesses
    Remove-MatrixDirectory $workRoot
}
exit $exitCode

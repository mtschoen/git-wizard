# Run the NUnit suite with coverage (coverlet collector -> Cobertura). By default this self-elevates
# (one UAC prompt) so the RequiresAdmin tests -- the real elevation / MFT-scan code that only runs
# with admin rights -- also contribute coverage. Use -NonInteractive to skip them (CI / headless).
#
# Coverage is computed by reusing ci/post-coverage-status.py, which merges line hits across every
# coverage.cobertura.xml under TestResults (so the non-admin and elevated runs add up). That is the
# same parser the CI gate uses, so local and CI numbers stay comparable.
#
# Build note: while MFTLib is referenced as a local ProjectReference (pre-0.3.0-publish; see
# AGENTS.md -> "MFTLib Local Development"), `dotnet build` cannot build MFTLib's native vcxproj.
# In that mode, build first with VS MSBuild (Platform=x64) and pass -NoBuild here. Once MFTLib
# 0.3.0 is a PackageReference again, plain `dotnet` works and -NoBuild is unnecessary.
#
# Usage:
#   .\scripts\run-coverage.ps1                   # full run, self-elevates for RequiresAdmin tests
#   .\scripts\run-coverage.ps1 -NonInteractive   # skip RequiresAdmin tests (CI / headless)
#   .\scripts\run-coverage.ps1 -NoBuild          # tests already built (e.g. via VS MSBuild)

param(
    [string]$Configuration = "Release",
    [switch]$NonInteractive,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = if ($env:GITHUB_WORKSPACE) { [string]$env:GITHUB_WORKSPACE } else { [string](Resolve-Path "$PSScriptRoot\..") }
Push-Location $repoRoot
try {
    $testProject = Join-Path $repoRoot "GitWizardTests\GitWizardTests.csproj"
    $resultsDir  = Join-Path $repoRoot "TestResults"
    # coverlet collector wants the data-collector name as a single token; build it as a variable so
    # PowerShell does not split on the embedded space when passing it to dotnet.
    $collectArg  = "--collect:XPlat Code Coverage"

    if (-not $NoBuild) {
        Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
        & dotnet build $testProject -c $Configuration
        if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }
    }

    # Fresh results dir so the Cobertura glob only sees this run's reports.
    Remove-Item $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "`nRunning non-admin tests with coverage..." -ForegroundColor Cyan
    & dotnet test $testProject --no-build -c $Configuration `
        --filter "Category!=RequiresAdmin" `
        --results-directory $resultsDir `
        $collectArg
    if ($LASTEXITCODE -ne 0) { Write-Host "Non-admin tests failed." -ForegroundColor Red; exit 1 }
    Write-Host "Non-admin coverage saved." -ForegroundColor Green

    if (-not $NonInteractive) {
        # Run RequiresAdmin tests elevated. Write a temp script with literal paths so there are no
        # nested-quoting issues, mirroring MFTLib's run-coverage.ps1.
        Write-Host "`nLaunching elevated runner for RequiresAdmin tests (UAC prompt)..." -ForegroundColor Yellow

        $adminLog = Join-Path $repoRoot "admin-test-output.log"
        $adminScript = Join-Path $repoRoot "admin-test-runner.ps1"
        Remove-Item $adminLog -ErrorAction SilentlyContinue
        Remove-Item $adminScript -ErrorAction SilentlyContinue

        $template = @'
$ErrorActionPreference = "Stop"
Set-Location "REPO_ROOT"
try {
    dotnet test "TEST_PROJECT" --no-build -c CONFIGURATION `
        --filter "Category=RequiresAdmin" `
        --results-directory "RESULTS_DIR" `
        "--collect:XPlat Code Coverage" *>&1 | Tee-Object -FilePath "LOG_FILE"
    exit $LASTEXITCODE
} catch {
    $_ | Out-File "LOG_FILE" -Append
    exit 1
}
'@
        $template.Replace("REPO_ROOT", $repoRoot).
                  Replace("TEST_PROJECT", $testProject).
                  Replace("CONFIGURATION", $Configuration).
                  Replace("RESULTS_DIR", $resultsDir).
                  Replace("LOG_FILE", $adminLog) |
            Set-Content $adminScript

        Start-Process powershell -Verb RunAs `
            -ArgumentList "-ExecutionPolicy", "Bypass", "-File", $adminScript `
            -Wait

        Remove-Item $adminScript -ErrorAction SilentlyContinue

        if (Test-Path $adminLog) {
            Write-Host (Get-Content $adminLog -Raw)
            Remove-Item $adminLog -ErrorAction SilentlyContinue
        }

        if (-not (Get-ChildItem -Path $resultsDir -Recurse -Filter "coverage.cobertura.xml" -ErrorAction SilentlyContinue)) {
            Write-Host "No coverage file found. UAC prompt may have been declined." -ForegroundColor Red
            exit 1
        }
    }

    # Summarize via the same Cobertura parser the CI gate uses; it merges hits across every report
    # under TestResults (non-admin + elevated). Forward-slash glob for Python's recursive glob.
    Write-Host "`n--- Coverage ---" -ForegroundColor Cyan
    $summaryFile = Join-Path $repoRoot "coverage-summary.md"
    Remove-Item $summaryFile -ErrorAction SilentlyContinue
    $env:GITHUB_STEP_SUMMARY = $summaryFile
    try {
        & python (Join-Path $repoRoot "ci\post-coverage-status.py") `
            --cobertura "TestResults/**/coverage.cobertura.xml" --summary --skip-post
    } finally {
        $env:GITHUB_STEP_SUMMARY = $null
    }
    if (Test-Path $summaryFile) {
        Get-Content $summaryFile
        Remove-Item $summaryFile -ErrorAction SilentlyContinue
    }
}
finally {
    Pop-Location
}

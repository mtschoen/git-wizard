#!/usr/bin/env pwsh
# Regenerates Screenshots/GitWizardUI.png with the headless Avalonia capture tool.
# Needs no display, no elevation and no WinAppDriver.
#
# The capture runs against a small synthetic data set, never the machine's real
# repositories: a live refresh of hundreds of real repositories does not settle
# within the tool's capture window and can hang. GITWIZARD_HOME is redirected to a
# fixture directory holding three tiny repositories, and the run is bounded by a
# hard timeout that kills the whole process tree. GITWIZARD_NO_MFT=1 keeps discovery
# on the plain filesystem walk so no elevated broker or UAC prompt is launched.
param(
    [string]$Configuration = 'Release',
    # Relative paths resolve against the repository root. CI passes an untracked path so the
    # tracked documentation image is not modified.
    [string]$OutputPath = 'Screenshots/GitWizardUI.png',
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    $screenshotPath = $OutputPath
    if (-not [System.IO.Path]::IsPathRooted($screenshotPath)) { $screenshotPath = Join-Path $repositoryRoot $screenshotPath }
    $screenshotPath = [System.IO.Path]::GetFullPath($screenshotPath)
    Remove-Item -LiteralPath $screenshotPath -ErrorAction SilentlyContinue

    # Fixture lives under the untracked artifacts directory (never %TEMP%: discovery skips it).
    $fixtureRoot = Join-Path $repositoryRoot 'artifacts/screenshot-fixture'
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
    $homeDirectory = Join-Path $fixtureRoot 'home'
    $repositoriesDirectory = Join-Path $fixtureRoot 'repos'
    New-Item -ItemType Directory -Force -Path $homeDirectory, $repositoriesDirectory | Out-Null
    foreach ($name in 'alpha-service', 'beta-client', 'gamma-docs') {
        $repositoryPath = Join-Path $repositoriesDirectory $name
        New-Item -ItemType Directory -Force -Path $repositoryPath | Out-Null
        Set-Content -LiteralPath (Join-Path $repositoryPath 'README.md') -Value $name
        git -C $repositoryPath init -q
        git -C $repositoryPath add README.md
        git -C $repositoryPath -c user.name=fixture -c user.email=fixture@example.invalid commit -q -m 'Initial commit'
        if ($LASTEXITCODE -ne 0) { throw "Could not create fixture repository $name" }
    }
    $fixtureConfiguration = @{ SearchPaths = @($repositoriesDirectory) } | ConvertTo-Json
    Set-Content -LiteralPath (Join-Path $homeDirectory 'config.json') -Value $fixtureConfiguration

    dotnet build GitWizardUI.Screenshot -c $Configuration --nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw 'GitWizardUI.Screenshot build failed' }

    $env:GITWIZARD_HOME = $homeDirectory
    # Walk the filesystem: MFT discovery would launch an elevated broker and raise a UAC prompt.
    $env:GITWIZARD_NO_MFT = '1'
    $process = Start-Process -FilePath dotnet -PassThru -NoNewWindow -ArgumentList @(
        'run', '--no-build', '--project', 'GitWizardUI.Screenshot', '-c', $Configuration, '--', "`"$screenshotPath`"")
    # Touching Handle caches it; Windows PowerShell 5.1 otherwise reports a null ExitCode.
    $null = $process.Handle
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        taskkill /T /F /PID $process.Id 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
        throw "GitWizardUI.Screenshot did not finish within $TimeoutSeconds seconds"
    }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "GitWizardUI.Screenshot exited $($process.ExitCode)" }
    if (-not (Test-Path -LiteralPath $screenshotPath)) { throw "Expected screenshot was not written: $screenshotPath" }

    $file = Get-Item -LiteralPath $screenshotPath
    if ($file.Length -eq 0) { throw "Screenshot is empty: $screenshotPath" }
    Write-Host ("Screenshot written: {0} ({1} KB)" -f $screenshotPath, [math]::Round($file.Length / 1KB))
}
finally {
    Remove-Item Env:GITWIZARD_HOME, Env:GITWIZARD_NO_MFT -ErrorAction SilentlyContinue
    Pop-Location
}

<#
.SYNOPSIS
Restores this checkout to a working state after `git clean -ffxd`.

.DESCRIPTION
A deep clean removes everything not tracked, which is safe by design - but several
things do not come back on their own: the git submodule checkout, the NuGet
package restore, and the agent instruction files that the aislop installer
generates. This script does all three, and reports any prerequisite it cannot
install for you.

Intended use:

    git clean -ffxd && .\init.ps1

Safe to run at any time; every step is idempotent.

.PARAMETER Build
Also build the native MFTLib submodule and the full solution (Release|x64) after
restoring. Off by default to keep the script fast - it requires Visual Studio
with the MSVC C++ workload, which the default path does not.

.EXAMPLE
.\init.ps1

.EXAMPLE
git clean -ffxd && .\init.ps1 -Build
#>
[CmdletBinding()]
param(
    [switch]$Build
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = $PSScriptRoot
$missingPrerequisites = [System.Collections.Generic.List[string]]::new()
$completedSteps = [System.Collections.Generic.List[string]]::new()

function Test-CommandAvailable {
    param([Parameter(Mandatory)][string]$Name)
    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

Write-Host 'Checking prerequisites...' -ForegroundColor Cyan

if (Test-CommandAvailable 'dotnet') {
    $sdkVersion = (& dotnet --version 2>$null)
    Write-Host "  dotnet SDK          $sdkVersion"
}
else {
    $missingPrerequisites.Add('dotnet SDK 10 - https://dotnet.microsoft.com/download')
    Write-Host '  dotnet SDK          MISSING' -ForegroundColor Red
}

$vswhere = if (${env:ProgramFiles(x86)}) {
    Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
}
else { $null }
$visualStudioPath = if ($vswhere -and (Test-Path $vswhere)) {
    & $vswhere -products '*' -requires Microsoft.Component.MSBuild -property installationPath -latest 2>$null
}
else { $null }

if ($visualStudioPath) {
    Write-Host "  MSBuild (MSVC)      $visualStudioPath"
}
else {
    $missingPrerequisites.Add('Visual Studio with the MSVC C++ workload - required to build external/MFTLib/MFTLibNative.vcxproj')
    Write-Host '  MSBuild (MSVC)      MISSING' -ForegroundColor Red
}

$aislopAvailable = Test-CommandAvailable 'aislop'
if ($aislopAvailable) {
    $aislopVersion = (& aislop --version 2>$null)
    Write-Host "  aislop              $aislopVersion"
}
else {
    $missingPrerequisites.Add('aislop - see the quality gate section of AGENTS.md for the pinned install command')
    Write-Host '  aislop              MISSING' -ForegroundColor Yellow
}

Write-Host ''

if (-not (Test-CommandAvailable 'dotnet')) {
    Write-Host 'Cannot continue without the dotnet SDK.' -ForegroundColor Red
    if ($missingPrerequisites.Count -gt 0) {
        Write-Host ''
        Write-Host 'Missing prerequisites (install manually):' -ForegroundColor Yellow
        foreach ($prerequisite in $missingPrerequisites) {
            Write-Host "  - $prerequisite"
        }
    }
    exit 1
}

# external/MFTLib is a git submodule that a fresh clone or a post-clean checkout
# leaves empty. Nothing here builds until it is checked out, so restore it
# unconditionally rather than only under -Build.
$mftlibMarker = Join-Path $repositoryRoot 'external/MFTLib/MFTLib/MFTLib.csproj'
if (-not (Test-Path $mftlibMarker)) {
    Write-Host 'Initializing external/MFTLib submodule...' -ForegroundColor Cyan
    & git -C $repositoryRoot submodule update --init --recursive
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Submodule checkout failed.' -ForegroundColor Red
        exit 1
    }
    $completedSteps.Add('external/MFTLib submodule checked out')
}

Write-Host ''
Write-Host 'Restoring NuGet packages...' -ForegroundColor Cyan
# git-wizard.slnx lists only the four managed projects (MFTLib is consumed as a
# built assembly via HintPath, not a ProjectReference), so restoring the .slnx
# directly is equivalent to restoring each project and matches what the Windows
# CI job does.
& dotnet restore (Join-Path $repositoryRoot 'git-wizard.slnx')
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Restore failed.' -ForegroundColor Red
    exit 1
}
$completedSteps.Add('NuGet packages restored')

if ($aislopAvailable) {
    Write-Host ''
    Write-Host 'Restoring agent instruction files...' -ForegroundColor Cyan
    Push-Location $repositoryRoot
    try {
        & aislop hook install claude --project
        if ($LASTEXITCODE -ne 0) {
            Write-Host 'aislop hook install failed.' -ForegroundColor Red
            exit 1
        }
    }
    finally {
        Pop-Location
    }
    $completedSteps.Add('.claude/AISLOP.md and .claude/CLAUDE.md restored')

    # The installer also rewrites the tracked .claude/settings.json with LF endings.
    # The content is unchanged, so `git diff` sees nothing while `git status` reports
    # the file as modified. Restore it only in that exact case - a real edit to
    # settings.json produces a content diff and is left alone.
    $settingsPath = '.claude/settings.json'
    & git -C $repositoryRoot diff --quiet -- $settingsPath
    $hasContentChange = $LASTEXITCODE -ne 0
    $status = & git -C $repositoryRoot status --porcelain -- $settingsPath
    if (-not $hasContentChange -and $status) {
        & git -C $repositoryRoot restore -- $settingsPath
        Write-Host 'Normalized line endings the installer left in .claude/settings.json.'
    }
}
else {
    Write-Host ''
    Write-Host 'Skipping agent instruction files: aislop is not installed.' -ForegroundColor Yellow
    Write-Host 'Once installed, run: aislop hook install claude --project'
}

# Optional. A clean removes .claude/settings.local.json, which can hold project-scope
# settings written by external provisioning tooling rather than by hand. If that
# tooling is on PATH, let it re-apply its own settings; otherwise this is skipped and
# nothing here depends on it. The feature argument is required, not incidental: a bare
# `onboard apply` runs the full host pipeline, which is far outside a repository's remit.
# The same tool ships under several console-script names; use whichever is exposed.
# The bare name `onboard` is deliberately not among them: it is also the GNOME
# on-screen keyboard binary at /usr/bin/onboard on Debian and Ubuntu, so probing it
# would run a GUI keyboard with our arguments on any Linux host that has the package
# and not this tool. The namespaced names cost nothing - they are entry points of the
# same installation.
$provisioner = @('schoen-lab-onboard', 'schoen-lab') |
    Where-Object { Test-CommandAvailable $_ } |
    Select-Object -First 1

if ($provisioner) {
    Write-Host ''
    Write-Host 'Re-applying provisioned project settings...' -ForegroundColor Cyan
    Push-Location $repositoryRoot
    try {
        & $provisioner apply auto-memory
        if ($LASTEXITCODE -eq 0) {
            $completedSteps.Add('Project-scope provisioned settings re-applied')
        }
        else {
            Write-Host "$provisioner apply auto-memory exited with $LASTEXITCODE; continuing." -ForegroundColor Yellow
        }
    }
    finally {
        Pop-Location
    }
}

if ($Build) {
    if (-not $visualStudioPath) {
        Write-Host ''
        Write-Host 'Cannot build: MSBuild with the MSVC workload was not found.' -ForegroundColor Red
        exit 1
    }

    # Use the amd64 binary: a 32-bit MSBuild is WOW64-redirected away from the checkout.
    $msbuild = Join-Path $visualStudioPath 'MSBuild\Current\Bin\amd64\MSBuild.exe'
    if (-not (Test-Path $msbuild)) {
        Write-Host "Cannot build: MSBuild not found at $msbuild" -ForegroundColor Red
        exit 1
    }

    Write-Host ''
    Write-Host 'Building MFTLib (native + managed)...' -ForegroundColor Cyan
    & $msbuild (Join-Path $repositoryRoot 'external\MFTLib\MFTLibNative\MFTLibNative.vcxproj') -t:Build -p:Configuration=Release -p:Platform=x64 -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'MFTLibNative build failed.' -ForegroundColor Red
        exit 1
    }
    & dotnet build (Join-Path $repositoryRoot 'external\MFTLib\MFTLib\MFTLib.csproj') -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'MFTLib build failed.' -ForegroundColor Red
        exit 1
    }
    & dotnet build (Join-Path $repositoryRoot 'external\MFTLib\MFTLibTestExtensions\MFTLibTestExtensions.csproj') -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'MFTLibTestExtensions build failed.' -ForegroundColor Red
        exit 1
    }
    $completedSteps.Add('MFTLib built (native + managed, Release|x64)')

    Write-Host ''
    Write-Host 'Building solution (Release)...' -ForegroundColor Cyan
    & dotnet build (Join-Path $repositoryRoot 'git-wizard.slnx') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Solution build failed.' -ForegroundColor Red
        exit 1
    }
    $completedSteps.Add('Solution built (Release)')
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
foreach ($step in $completedSteps) {
    Write-Host "  $step"
}

if ($missingPrerequisites.Count -gt 0) {
    Write-Host ''
    Write-Host 'Missing prerequisites (install manually):' -ForegroundColor Yellow
    foreach ($prerequisite in $missingPrerequisites) {
        Write-Host "  - $prerequisite"
    }
}

if (-not $Build) {
    Write-Host ''
    Write-Host 'Next: .\init.ps1 -Build (.\init.bat -Build from cmd.exe),'
    Write-Host '      or .\scripts\run-coverage.ps1 for the full test run.'
}

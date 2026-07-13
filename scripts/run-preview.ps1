#!/usr/bin/env pwsh
# Download and launch a git-wizard PR preview artifact for the current OS.
#
# Usage:
#   scripts/run-preview.ps1          # newest git-wizard preview package
#   scripts/run-preview.ps1 42       # newest artifact for PR #42
#
# The preview provider publishes the app to the gitea generic package registry;
# this fetches the current-OS zip, unpacks it to a temp dir, and launches
# GitWizardUI. Set $env:GITEA_TOKEN if the registry needs auth; anonymous first.
param([int]$PrNumber)

$ErrorActionPreference = "Stop"
$gitea = "https://gitea.llamabox.sticktoitive.net"
$owner = "schoen"

if ($IsWindows -or $env:OS -eq "Windows_NT") { $platform = "windows" }
elseif ($IsLinux) { $platform = "linux" }
else { Write-Error "no git-wizard preview artifact is published for this OS"; exit 1 }
$file = "GitWizardUI-$platform-x64.zip"

# Auth header only when GITEA_TOKEN is set (anonymous first).
$headers = @{}
if ($env:GITEA_TOKEN) { $headers["Authorization"] = "token $env:GITEA_TOKEN" }

if ($PSBoundParameters.ContainsKey('PrNumber')) {
    $query = "git-wizard-pr-$PrNumber"
    $listUrl = "{0}/api/v1/packages/{1}?type=generic&q={2}" -f $gitea, $owner, $query
    $pkgs = @(Invoke-RestMethod -Uri $listUrl -Headers $headers |
        Where-Object { $_.name -eq $query })
} else {
    $listUrl = "{0}/api/v1/packages/{1}?type=generic&q=git-wizard-pr-" -f $gitea, $owner
    $pkgs = @(Invoke-RestMethod -Uri $listUrl -Headers $headers)
}
$newest = $pkgs | Sort-Object { [datetime]$_.created_at } | Select-Object -Last 1
if (-not $newest) { Write-Error "no git-wizard preview package found"; exit 1 }

$url = "{0}/api/packages/{1}/generic/{2}/{3}/{4}" -f $gitea, $owner, $newest.name, $newest.version, $file
$tmp = New-Item -ItemType Directory -Path (Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName()))
$zip = Join-Path $tmp $file
Write-Host "downloading $file ($($newest.name) @ $($newest.version))"
Invoke-WebRequest -Uri $url -Headers $headers -OutFile $zip
Expand-Archive -Path $zip -DestinationPath $tmp -Force
$exe = if ($platform -eq "windows") { Join-Path $tmp "GitWizardUI.exe" } else { Join-Path $tmp "GitWizardUI" }
if ($platform -ne "windows") { chmod +x $exe }
Write-Host "launching $exe"
& $exe

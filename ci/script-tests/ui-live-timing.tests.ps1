$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
function Add-Type { throw 'FunctionsOnly attempted native/UI assembly loading' }
function Start-Process { throw 'FunctionsOnly attempted process startup' }
. (Join-Path $repositoryRoot 'scripts/ui-live-timing.ps1') -FunctionsOnly
$elements = @(
    [pscustomobject]@{ Name = 'Ready (journal): C'; X = 10; Y = 1; Right = 100; Width = 90 },
    [pscustomobject]@{ Name = 'C: catching up'; X = 9; Y = 2; Right = 30; Width = 21 },
    [pscustomobject]@{ Name = 'D: caught up'; X = 20; Y = 3; Right = 101; Width = 81 },
    [pscustomobject]@{ Name = 'Unrelated'; X = -50; Y = 4; Right = 200; Width = 250 }
)
$observations = @(Get-ReadinessLayout -Elements $elements -Left 10 -Right 100)
if ($observations.Count -ne 3) { throw 'Readiness selection changed' }
if (-not $observations[0].Inside) { throw 'Inclusive edges rejected' }
if ($observations[1].Inside -or $observations[2].Inside) { throw 'Clipping accepted' }
if ($observations[2].Name -cne 'D: caught up' -or $observations[2].Y -ne 3) { throw 'Diagnostic data lost' }
if (@(Get-ReadinessLayout -Elements @() -Left 0 -Right 100).Count -ne 0) { throw 'Counts leaked between widths' }
$wide = @(Get-ReadinessLayout -Elements $elements -Left 0 -Right 200)
if (@($wide | Where-Object { -not $_.Inside }).Count -ne 0) { throw 'Wide bounds retained narrow failures' }
Write-Host 'UI timing helper tests passed.'

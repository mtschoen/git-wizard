$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
function Start-Process { throw 'FunctionsOnly attempted process startup' }
function New-Item { throw 'FunctionsOnly attempted filesystem mutation' }
function Get-Date { throw 'FunctionsOnly entered the attended driver' }
. (Join-Path $repositoryRoot 'scripts/cli-live-smoke.ps1') -FunctionsOnly
$lines = @(
    'ready: mode=journal caught-up=C',
    '2026-10-19T12:00:00.000Z changed: C:\scratch[1]\repo-a',
    'changed: C:\other\repo-a',
    'changed: C:\scratch[1]\repo-b'
)
$result = Find-SmokeLogMatch -Lines $lines -Pattern 'changed: ' -StartLine 1 -PathFilter 'C:\scratch[1]'
if ($result.Line -cne $lines[1] -or $result.NextStartLine -ne 2) { throw 'First match/cursor incorrect' }
if ($result.ClientExited -or $null -ne $result.LastStartupLine) { throw 'Match changed process diagnostics' }
$next = Find-SmokeLogMatch -Lines $lines -Pattern 'changed: ' -StartLine $result.NextStartLine -PathFilter 'C:\scratch[1]'
if ($next.Line -cne $lines[3] -or $next.NextStartLine -ne 4) { throw 'Cursor or literal path filtering incorrect' }
if ($null -ne (Find-SmokeLogMatch -Lines $lines -Pattern 'changed: ' -StartLine 4)) { throw 'Replayed consumed match' }
if ($null -ne (Find-SmokeLogMatch -Lines @() -Pattern 'ready: ')) { throw 'Empty log matched' }
if ($null -ne (Find-SmokeLogMatch -Lines $lines -Pattern '^missing: ')) { throw 'Unmatched log matched' }
$ready = Find-SmokeLogMatch -Lines $lines -Pattern "${script:Stamp}ready: "
if ($ready.Line -cne $lines[0]) { throw 'Untimestamped readiness not recognized' }
$stamped = Find-SmokeLogMatch -Lines @('2026-10-19T12:00:00.000Z ready: mode=journal caught-up=C') -Pattern "${script:Stamp}ready: "
if ($null -eq $stamped) { throw 'Timestamped readiness not recognized' }
Write-Host 'CLI smoke helper tests passed.'

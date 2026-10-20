$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$tools = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'scripts') -File -Recurse -Force |
    Where-Object { $_.Extension -ieq '.ps1' })
$testDirectory = Join-Path $PSScriptRoot 'script-tests'
$tests = @(Get-ChildItem -LiteralPath $testDirectory -File -Recurse -Force -Filter '*.tests.ps1')
if ($tools.Count -eq 0 -or $tests.Count -eq 0) { throw 'PowerShell tools and self-tests must not be empty' }
$failures = @()
foreach ($file in @($tools) + @($tests)) {
    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    foreach ($parseError in $parseErrors) {
        $failures += "$($file.FullName):$($parseError.Extent.StartLineNumber): $($parseError.Message)"
    }
}
if ($failures.Count) { throw ("Parse failed:`n" + ($failures -join "`n")) }
foreach ($test in $tests | Sort-Object FullName) {
    & (Join-Path $PSHOME 'pwsh') -NoProfile -NonInteractive -File $test.FullName
    if ($LASTEXITCODE -ne 0) { throw "Self-test failed: $($test.FullName) (exit $LASTEXITCODE)" }
}
Write-Host "Parsed $($tools.Count) PowerShell tools; passed $($tests.Count) helper self-tests."

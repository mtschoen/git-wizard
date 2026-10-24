param([Parameter(Mandatory)][ValidateSet('native', 'inspect')][string]$Gate, [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($Gate -eq 'native') {
    $msbuild = Get-Command msbuild -ErrorAction SilentlyContinue
    if (-not $msbuild) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
        $msbuild = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild/**/Bin/amd64/MSBuild.exe' | Select-Object -First 1
    }
    if (-not $msbuild) { throw 'Visual Studio amd64 MSBuild is required' }
    # Debug is a documented input too: jb inspectcode's own build pass compiles the
    # solution in Debug, so the aislop quality gate needs x64/Debug/MFTLibNative.dll
    # for CopyMFTLibNativeWindows (Directory.Build.targets).
    & $msbuild external/MFTLib/MFTLibNative/MFTLibNative.vcxproj -t:Build "-p:Configuration=$Configuration" -p:Platform=x64 -nologo -v:minimal
    exit $LASTEXITCODE
}
$directory = $env:CI_RESULTS_DIRECTORY
if (-not $directory) { $directory = $env:RUNNER_TEMP }
if (-not $directory) { throw 'Set CI_RESULTS_DIRECTORY or RUNNER_TEMP for inspection artifacts' }
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$reportPath = Join-Path $directory 'jb-report.json'
Remove-Item -LiteralPath $reportPath -ErrorAction SilentlyContinue
& jb inspectcode git-wizard.slnx "-o=$reportPath" --settings=git-wizard.slnx.DotSettings --severity=WARNING --no-updates --properties:Configuration=Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$report = Get-Content $reportPath -Raw | ConvertFrom-Json
if (-not $report.runs -or $null -eq $report.runs[0].results) { throw 'Invalid InspectCode SARIF report' }
$results = @($report.runs[0].results)
Write-Host "jb inspectcode: $($results.Count) finding(s)"
$results | Group-Object ruleId | Sort-Object Count -Descending | ForEach-Object {
    Write-Host ("  {0,4}  {1}" -f $_.Count, $_.Name)
}
if ($results.Count -gt 0) { exit 1 }

# Self-tests for scripts/cli-verb-matrix.ps1 and its helpers. They run the real helper functions against
# stub executables (pwsh itself and a generated .cmd file), never the real git-wizard.exe, so they need
# no build, no administrator rights and no state folder. Run: pwsh -NoProfile -File ci/script-tests/cli-verb-matrix.tests.ps1
$ErrorActionPreference = 'Stop'
$scriptsRoot = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'scripts'

. (Join-Path $scriptsRoot 'cli-verb-matrix.helpers.ps1')
. (Join-Path $scriptsRoot 'cli-verb-matrix.rows.ps1')

function Assert-True($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Equal($Expected, $Actual, [string]$What) {
    if ($Expected -cne $Actual) { throw "$What : expected '$Expected', got '$Actual'" }
}

function Assert-Contains([string]$Text, [string]$Expected, [string]$What) {
    if (-not $Text.Contains($Expected)) { throw "$What : '$Expected' not found in: $Text" }
}

$pwshPath = (Get-Command pwsh).Source
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('gw-verb-matrix-tests-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$null = New-Item -ItemType Directory -Path $scratch -Force

function Invoke-StubRow([hashtable]$Row, [int]$TimeoutSeconds = 30) {
    $stubRow = $Row.Clone()
    $stubRow.Arguments = @('-NoProfile', '-Command', $Row.Command)
    $stubRow.Remove('Command')
    Invoke-MatrixRow -Row $stubRow -ExecutablePath $pwshPath -Context @{} -Environment @{} -TimeoutSeconds $TimeoutSeconds
}

try {
    # A passing row: exit code and every expected substring match.
    $result = Invoke-StubRow @{ Name = 'pass'; Command = "Write-Output 'alpha beta'; exit 0"; Contains = @('alpha', 'beta') }
    Assert-Equal 'PASS' $result.Status 'passing row'

    $argumentStub = Join-Path $scratch 'arguments.ps1'
    Set-Content $argumentStub '[Console]::OutputEncoding = [Text.Encoding]::UTF8; [Console]::WriteLine((ConvertTo-Json -InputObject @($args) -Compress)); [Console]::Error.WriteLine($env:GW_MATRIX_VALUE); if ([Console]::In.Read() -ne -1) { exit 8 }'
    $values = @('', 'two words', 'embedded"quote', 'trailing slash\', 'before\"quote', ('caf' + [char]0xE9))
    $run = Invoke-MatrixProcess -ExecutablePath $pwshPath -Arguments (@('-NoProfile', '-File', $argumentStub) + $values) -Environment @{ GW_MATRIX_VALUE = $values[-1] }
    Assert-Equal 0 $run.ExitCode 'redirected stdin reaches EOF'
    Assert-Equal (ConvertTo-Json -InputObject $values -Compress) $run.Output.Trim() 'native launch preserves argv'
    Assert-Equal $values[-1] $run.ErrorOutput.Trim() 'native launch preserves Unicode environment and stderr'

    # Wrong exit code is a failure that names both codes; an expected nonzero code passes.
    $result = Invoke-StubRow @{ Name = 'wrong-exit'; Command = 'exit 3' }
    Assert-Equal 'FAIL' $result.Status 'wrong exit code'
    Assert-Contains ($result.Problems -join ';') 'exit code 3, expected 0' 'wrong exit code message'
    $result = Invoke-StubRow @{ Name = 'expected-exit'; Command = 'exit 3'; ExitCode = 3 }
    Assert-Equal 'PASS' $result.Status 'expected nonzero exit code'

    # A missing substring fails, a forbidden substring fails, and stderr counts as output.
    $result = Invoke-StubRow @{ Name = 'missing'; Command = "Write-Output 'alpha'"; Contains = @('gamma') }
    Assert-Equal 'FAIL' $result.Status 'missing substring'
    Assert-Contains ($result.Problems -join ';') "output lacks 'gamma'" 'missing substring message'
    $result = Invoke-StubRow @{ Name = 'forbidden'; Command = "Write-Output 'alpha'"; NotContains = @('alpha') }
    Assert-Equal 'FAIL' $result.Status 'forbidden substring'
    $result = Invoke-StubRow @{ Name = 'stderr'; Command = "[Console]::Error.WriteLine('on-stderr')"; Contains = @('on-stderr') }
    Assert-Equal 'PASS' $result.Status 'stderr is searched'

    # Verify blocks and Setup failures surface as failures; Fatal is carried to the caller.
    $result = Invoke-StubRow @{ Name = 'verify'; Command = 'exit 0'; Verify = { param($context, $run) 'verify said no' } }
    Assert-Contains ($result.Problems -join ';') 'verify said no' 'verify message'
    $result = Invoke-StubRow @{ Name = 'setup'; Command = 'exit 0'; Setup = { param($context) throw 'setup broke' }; Fatal = $true }
    Assert-Equal 'FAIL' $result.Status 'setup failure'
    Assert-True $result.Fatal 'fatal flag is carried'

    # A timeout fails the row and kills the whole process tree, grandchild included.
    $pidFile = Join-Path $scratch 'grandchild.pid'
    $command = "`$child = Start-Process -FilePath '$pwshPath' -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120' -PassThru; " +
        "Set-Content -LiteralPath '$pidFile' -Value `$child.Id; Start-Sleep -Seconds 120"
    $result = Invoke-StubRow @{ Name = 'timeout'; Command = $command } 3
    Assert-Equal 'FAIL' $result.Status 'timed out row'
    Assert-Contains ($result.Problems -join ';') 'timed out after 3 seconds' 'timeout message'
    $grandchildId = [int](Get-Content -LiteralPath $pidFile)
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process -Id $grandchildId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Assert-True ($null -eq (Get-Process -Id $grandchildId -ErrorAction SilentlyContinue)) 'the timed-out grandchild process is still alive'

    # A parent that exits before its own child: ownership must not depend on the parent still being
    # alive, and the child must be gone before the pipes are read, or the launch would be judged on
    # text that never drained. -NoNewWindow is what makes the child inherit those pipes.
    $orphanPidFile = Join-Path $scratch 'orphan.pid'
    $orphanCommand = "`$child = Start-Process -FilePath '$pwshPath' -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 120' -NoNewWindow -PassThru; " +
        "Set-Content -LiteralPath '$orphanPidFile' -Value `$child.Id; exit 0"
    $run = Invoke-MatrixProcess -ExecutablePath $pwshPath -Arguments @('-NoProfile', '-Command', $orphanCommand) -TimeoutSeconds 30
    Assert-Equal 0 $run.ExitCode 'the parent exits on its own'
    Assert-True (-not $run.TimedOut) 'the parent did not time out'
    Assert-True (-not $run.OutputTruncated) 'the orphaned child kept the captured pipes open'
    $orphanId = [int](Get-Content -LiteralPath $orphanPidFile)
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process -Id $orphanId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Assert-True ($null -eq (Get-Process -Id $orphanId -ErrorAction SilentlyContinue)) 'a child of an exited parent survived its launch'

    # The same launch as a row: incomplete capture fails a row on its own, so this passes only
    # because the orphan was torn down and the pipes drained.
    $result = Invoke-StubRow @{ Name = 'exited-parent'; Command = $orphanCommand }
    Assert-Equal 'PASS' $result.Status 'a row whose parent exits before its child'
    $orphanId = [int](Get-Content -LiteralPath $orphanPidFile)
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Process -Id $orphanId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Assert-True ($null -eq (Get-Process -Id $orphanId -ErrorAction SilentlyContinue)) 'a row left a child of an exited parent behind'

    # Force the launch/assignment interleaving from the file-wizard matrix review. A suspended primary thread cannot
    # spawn before assignment; the old running launch is held here until it has spawned a child.
    $readyName = 'Local\gw-matrix-ready-' + [guid]::NewGuid().ToString('N')
    $releaseName = 'Local\gw-matrix-release-' + [guid]::NewGuid().ToString('N')
    $ready = [System.Threading.EventWaitHandle]::new($false, 'ManualReset', $readyName)
    $release = [System.Threading.EventWaitHandle]::new($false, 'ManualReset', $releaseName)
    $racePidFile = Join-Path $scratch 'pre-assignment.pid'
    $raceCommand = $orphanCommand.Replace($orphanPidFile, $racePidFile).Replace('exit 0',
        "`$ready = [System.Threading.EventWaitHandle]::OpenExisting('$readyName'); `$null = `$ready.Set(); " +
        "`$release = [System.Threading.EventWaitHandle]::OpenExisting('$releaseName'); if (-not `$release.WaitOne(30000)) { exit 9 }; exit 0")
    $helperPath = Join-Path $scriptsRoot 'cli-verb-matrix.helpers.ps1'
    $trackingLine = (Select-String -LiteralPath $helperPath -SimpleMatch '$script:MatrixStartedProcesses.Add($process)').LineNumber
    $inputLine = (Select-String -LiteralPath $helperPath -SimpleMatch '$capture.StandardInput.Close()').LineNumber
    $script:launchProbe = @{ Reached = $false; Suspended = $false; Ready = $ready; Release = $release }
    $breakpoints = @(
        Set-PSBreakpoint -Script $helperPath -Line $trackingLine -Action {
            $script:launchProbe.Reached = $true
            $thread = $process.Threads[0]
            $script:launchProbe.Suspended = $thread.ThreadState -eq 'Wait' -and $thread.WaitReason -eq 'Suspended'
            if (-not $script:launchProbe.Suspended) { $null = $script:launchProbe.Ready.WaitOne(30000) }
        }
        Set-PSBreakpoint -Script $helperPath -Line $inputLine -Action { $null = $script:launchProbe.Release.Set() }
    )
    try {
        $run = Invoke-MatrixProcess -ExecutablePath $pwshPath -Arguments @('-NoProfile', '-Command', $raceCommand) -TimeoutSeconds 30
        Stop-MatrixStartedProcesses
        Assert-True $script:launchProbe.Reached 'the pre-assignment scheduling probe did not run'
        Assert-Equal 0 $run.ExitCode 'the race parent exits on its own'
        Assert-True (-not $run.OutputTruncated) 'a pre-assignment child escaped the job and kept the pipes open'
        Assert-True $script:launchProbe.Suspended 'the launch was runnable before job assignment'
        Assert-True $ready.WaitOne(0) 'the resumed parent did not create its child'
        Assert-True ($null -eq (Get-Process -Id ([int](Get-Content $racePidFile)) -ErrorAction SilentlyContinue)) 'the pre-assignment child survived cleanup'
    }
    finally {
        $breakpoints | Remove-PSBreakpoint
        $null = $release.Set()
        if (Test-Path $racePidFile) {
            $child = Get-Process -Id ([int](Get-Content $racePidFile)) -ErrorAction SilentlyContinue
            if ($child) { $child.Kill(); $child.WaitForExit(); $child.Dispose() }
        }
        $ready.Dispose()
        $release.Dispose()
        Remove-Variable launchProbe -Scope Script
    }

    # Refusing job assignment must terminate the still-suspended launch, without executing it.
    $script:refusedProcess = $null
    $script:refusedJob = [System.IntPtr]::Zero
    $breakpoint = Set-PSBreakpoint -Script $helperPath -Line $trackingLine -Action {
        $script:refusedProcess = [System.Diagnostics.Process]::GetProcessById($process.Id)
        $null = $script:refusedProcess.Handle
        $script:refusedJob = $job
        Set-Variable -Name job -Value ([System.IntPtr]::new(-1)) -Scope 1
    }
    $refusedMarker = Join-Path $scratch 'refused-launch.txt'
    try {
        $result = Invoke-StubRow @{ Name = 'assignment-refused'; Command = "Set-Content '$refusedMarker' 'ran'" }
        Assert-Equal 'FAIL' $result.Status 'job assignment refusal fails the row'
        Assert-Contains ($result.Problems -join ';') 'AssignProcessToJobObject failed' 'assignment failure diagnostic'
        Assert-True ($null -ne $script:refusedProcess) 'assignment failure probe did not run'
        Assert-True $script:refusedProcess.WaitForExit(30000) 'assignment failure left a suspended process behind'
        Assert-True (-not (Test-Path $refusedMarker)) 'an unowned process executed after assignment failed'
    }
    finally {
        Remove-PSBreakpoint $breakpoint
        Stop-MatrixJob $script:refusedJob
        if ($script:refusedProcess) {
            if (-not $script:refusedProcess.HasExited) { $script:refusedProcess.Kill(); $script:refusedProcess.WaitForExit() }
            $script:refusedProcess.Dispose()
        }
        Remove-Variable refusedProcess -Scope Script
        Remove-Variable refusedJob -Scope Script
    }

    # Stop-MatrixStartedProcesses kills a process the matrix started and left running.
    $straggler = Start-Process -FilePath $pwshPath -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 120' -PassThru
    $stragglerId = $straggler.Id
    $script:MatrixStartedProcesses.Add($straggler)
    Stop-MatrixStartedProcesses
    Assert-True ($null -eq (Get-Process -Id $stragglerId -ErrorAction SilentlyContinue)) 'a started process survived the cleanup'

    # Usage parsing reads options with aliases, subcommand scopes and the command list; consumer
    # spellings the help does not list map to their long options.
    $rootHelp = @'
0.0.0

Description line.

Usage: git-wizard [command] [options]

Options:
  -?|-h|--help              Show help information.
  --version                 Show version information.
  -v|--verbose              Enable verbose logging.
  --no-mft                  Use uncached directory-walk indexes
                            (WalkOnly).
  -p|--paths <PATHS>        Report on specific repo paths.
  --save-path <PATH>        Path where the report will be saved.
  -s|--silent               Do not print to the console

Commands:
  tags                      Scan all clones for tags.

Run 'git-wizard [command] -?|-h|--help' for more information about a command.
'@
    $tagsHelp = @'
Scan all clones for tags.

Usage: git-wizard tags [options]

Options:
  -m|--match <PATTERN>   Glob pattern to match tag names against.
  -v|--verbose           Enable verbose logging.
  -?|-h|--help           Show help information.
'@
    $usage = Get-MatrixUsage -HelpTexts @{ '' = $rootHelp; 'tags' = $tagsHelp }
    foreach ($expected in @('--help', '--version', '--verbose', '--no-mft', '--paths', '--save-path', '--silent', 'tags', 'tags --match', 'tags --verbose', 'tags --help')) {
        Assert-True ($usage.Tokens -contains $expected) "usage token missing: $expected"
    }
    foreach ($unexpected in @('-v', '-h', '-?', 'Description', 'Run', 'Usage', 'tags -m')) {
        Assert-True ($usage.Tokens -notcontains $unexpected) "usage token must be normalized or ignored: $unexpected"
    }
    Assert-Equal '--verbose' $usage.Aliases['-v'] 'root alias'
    Assert-Equal 'tags --match' $usage.Aliases['tags -m'] 'subcommand alias'
    Assert-Equal '--paths' $usage.Aliases['-paths'] 'consumer alias for --paths'
    Assert-Equal '--silent' $usage.Aliases['-silent'] 'consumer alias for --silent'
    Assert-True (-not $usage.Aliases.ContainsKey('-save-path') -or $usage.Aliases['-save-path'] -eq '--save-path') 'consumer alias for --save-path'

    # Row tokens: the subcommand, then each option in its scope, normalized through the alias map.
    $rowTokens = Get-MatrixRowTokens @{ Name = 'x'; Arguments = @('tags', '--no-mft', '-m', 'a/*', '-v', '-?') } $usage.Aliases
    foreach ($expected in @('tags', 'tags --no-mft', 'tags --match', 'tags --verbose', 'tags --help')) {
        Assert-True ($rowTokens -contains $expected) "subcommand row token missing: $expected"
    }
    $rowTokens = Get-MatrixRowTokens @{ Name = 'x'; Arguments = @('--no-mft', '-paths', 'one', '--save-path=file', '-silent') } $usage.Aliases
    foreach ($expected in @('--no-mft', '--paths', '--save-path', '--silent')) {
        Assert-True ($rowTokens -contains $expected) "root row token missing: $expected"
    }
    $rowTokens = Get-MatrixRowTokens @{ Name = 'mixed'; Arguments = @('--no-mft', '--filter', 'tags', 'prune', '-p', 'x', '--base', 'main', '--minified') } $usage.Aliases
    foreach ($expected in @('--no-mft', '--filter', 'prune', 'prune -p', 'prune --base', 'prune --minified')) {
        Assert-True ($rowTokens -contains $expected) "mixed row token missing: $expected"
    }
    Assert-True ($rowTokens -notcontains 'tags') 'an option value must not become a subcommand token'

    # Coverage: a verb with neither a row nor an exclusion is reported by name; so are a stale row and a stale exclusion.
    $rows = @(@{ Name = 'report'; Arguments = @('--no-mft', '-v') }, @{ Name = 'probe'; Arguments = @('--bogus', '--no-mft'); Probe = $true })
    $exclusions = @(@{ Token = '--watch'; Reason = 'elevates' })
    $tokens = @('--no-mft', '--verbose', '--watch')
    $aliases = @{ '-v' = '--verbose' }
    Assert-True (Test-MatrixCoverage -UsageTokens $tokens -Rows $rows -Exclusions $exclusions -Aliases $aliases).Ok 'complete coverage must pass (probe tokens ignored, aliases normalized)'
    $coverage = Test-MatrixCoverage -UsageTokens ($tokens + '--brand-new-verb') -Rows $rows -Exclusions $exclusions -Aliases $aliases
    Assert-True (-not $coverage.Ok) 'an unlisted verb must fail coverage'
    Assert-Contains ($coverage.Problems -join ';') "lists '--brand-new-verb' but the matrix neither runs nor excludes it" 'unlisted verb message'
    $coverage = Test-MatrixCoverage -UsageTokens @('--no-mft', '--watch') -Rows $rows -Exclusions $exclusions -Aliases $aliases
    Assert-Contains ($coverage.Problems -join ';') "exercises '--verbose'" 'stale row token message'
    $coverage = Test-MatrixCoverage -UsageTokens @('--no-mft', '--verbose') -Rows $rows -Exclusions $exclusions -Aliases $aliases
    Assert-Contains ($coverage.Problems -join ';') 'exclusion for' 'stale exclusion message'

    # Scope guard: rows that could scan the real drives or elevate are refused.
    foreach ($unsafe in @(
            @{ Name = 'no-mft-missing'; Arguments = @('--scan-only') },
            @{ Name = 'subcommand-no-mft-missing'; Arguments = @('tags', '--paths', 'x') },
            @{ Name = 'watch'; Arguments = @('--no-mft', '--watch') },
            @{ Name = 'defender'; Arguments = @('--no-mft', '--setup-defender') })) {
        $threw = $false
        try { Assert-MatrixRowScope $unsafe } catch { $threw = $true }
        Assert-True $threw "the row '$($unsafe.Name)' must be refused"
    }
    Assert-MatrixRowScope @{ Name = 'report'; Arguments = @('--no-mft', '--scan-only') }
    Assert-MatrixRowScope @{ Name = 'subcommand'; Arguments = @('tags', '--no-mft', '-p', 'x') }
    Assert-MatrixRowScope @{ Name = 'root-option-before-subcommand'; Arguments = @('--no-mft', 'tags', '--paths', 'x') }
    Assert-MatrixRowScope @{ Name = 'subcommand-without-paths'; Arguments = @('tags', '--no-mft') }
    Assert-MatrixRowScope @{ Name = 'help'; Arguments = @('--help') }
    Assert-MatrixRowScope @{ Name = 'size'; Arguments = @('--db-size') }

    # The shipped rows are scoped, uniquely named, end with the one terminal row, and the first discovering row is the fatal sentinel check.
    $shipped = Get-MatrixRows
    Assert-Equal $shipped.Count (@($shipped | ForEach-Object { $_.Name } | Select-Object -Unique)).Count 'row names are unique'
    $firstDiscovery = $shipped | Where-Object { $_.Arguments -contains '--no-mft' -and -not $_.ContainsKey('Probe') } | Select-Object -First 1
    Assert-True ($firstDiscovery.ContainsKey('Fatal') -and $firstDiscovery.Fatal) 'the first discovering row is the fatal sentinel check'
    Assert-True ($shipped[-1].ContainsKey('Terminal') -and $shipped[-1].Terminal) 'the last row is terminal'

    # The fixture builds three repositories with the shapes the rows expect, ignoring ambient Git
    # command-scope config overrides, repository/index routing and inherited templates.
    $gitCommand = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    Assert-True ($null -ne $gitCommand) 'git is required for the fixture test'
    $emptyTemplate = Join-Path $scratch 'empty-template'
    $null = New-Item -ItemType Directory -Path $emptyTemplate
    $decoyRemote = Join-Path $scratch 'decoy-remote.git'
    $decoyInit = Invoke-MatrixProcess -ExecutablePath $gitCommand.Source -Arguments @('init', '-q', '--bare', '-b', 'main', "--template=$emptyTemplate", $decoyRemote) -Environment (Get-MatrixGitEnvironment) -TimeoutSeconds 60
    Assert-Equal 0 $decoyInit.ExitCode 'decoy bare remote init'
    $fixtureRoot = Join-Path $scratch 'fixture'
    $inheritedTemplate = Join-Path $scratch 'inherited-template'
    $null = New-Item -ItemType Directory -Path (Join-Path $inheritedTemplate 'hooks')
    $intendedRemote = Join-Path $fixtureRoot 'remotes\clean.git'
    Set-Content -LiteralPath (Join-Path $inheritedTemplate 'config') -Value "[url `"$($decoyRemote.Replace('\', '\\'))`"]`n    pushInsteadOf = $($intendedRemote.Replace('\', '\\'))"
    Set-Content -LiteralPath (Join-Path $inheritedTemplate 'hooks\pre-push') -Value "#!/bin/sh`nexit 0" -NoNewline
    $savedGitOverrides = @{}
    $injectedGitOverrides = @{
        GIT_CONFIG_COUNT = '1'
        GIT_CONFIG_KEY_0 = 'remote.origin.pushurl'
        GIT_CONFIG_VALUE_0 = $decoyRemote
        GIT_CONFIG_PARAMETERS = "'remote.origin.pushurl=$decoyRemote'"
        GIT_DIR = $decoyRemote
        GIT_WORK_TREE = $scratch
        GIT_INDEX_FILE = (Join-Path $scratch 'decoy-index')
        GIT_TEMPLATE_DIR = $inheritedTemplate
    }
    foreach ($name in $injectedGitOverrides.Keys) {
        $savedGitOverrides[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $injectedGitOverrides[$name])
    }
    try {
        $fixture = New-MatrixFixture -Root $fixtureRoot -GitPath $gitCommand.Source
        Assert-Equal 0 @(Get-ChildItem -LiteralPath (Join-Path $decoyRemote 'refs') -Recurse -File).Count 'decoy remote must remain untouched by fixture pushes'
        foreach ($name in @('clean', 'dirty', 'prunable')) {
            Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Repositories "$name\.git")) "fixture repository $name exists"
            Assert-True (Test-Path -LiteralPath (Join-Path $fixtureRoot "remotes\$name.git\refs\heads\main") -PathType Leaf) "fixture remote $name received refs/heads/main"
            foreach ($gitDirectory in @((Join-Path $fixture.Repositories "$name\.git"), (Join-Path $fixtureRoot "remotes\$name.git"))) {
                Assert-True (-not (Get-Content -LiteralPath (Join-Path $gitDirectory 'config') -Raw).Contains('pushInsteadOf')) "inherited template config must not be installed in $gitDirectory"
                Assert-True (-not (Test-Path -LiteralPath (Join-Path $gitDirectory 'hooks\pre-push'))) "inherited template hook must not be installed in $gitDirectory"
            }
        }
        Assert-True (Test-Path -LiteralPath (Join-Path $fixtureRoot 'remotes\clean.git\refs\tags\archive\one') -PathType Leaf) 'fixture remote clean received archive/one tag'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $scratch 'decoy-index'))) 'decoy index must not be created'
        $cleanTags = Invoke-MatrixProcess -ExecutablePath $gitCommand.Source -Arguments @('-C', $fixture.Clean, 'tag') -Environment (Get-MatrixGitEnvironment) -TimeoutSeconds 60
        Assert-Contains ($cleanTags.Output -replace "\r?\n", ';') 'local/two' 'clean has a local-only tag'
        $dirtyStatus = Invoke-MatrixProcess -ExecutablePath $gitCommand.Source -Arguments @('-C', $fixture.Dirty, 'status', '--porcelain') -Environment (Get-MatrixGitEnvironment) -TimeoutSeconds 60
        Assert-Contains ($dirtyStatus.Output -replace "\r?\n", ';') 'a.txt' 'dirty has a modified tracked file'
        $prunableMerged = Invoke-MatrixProcess -ExecutablePath $gitCommand.Source -Arguments @('-C', $fixture.Prunable, 'branch', '--merged', 'main') -Environment (Get-MatrixGitEnvironment) -TimeoutSeconds 60
        Assert-Contains ($prunableMerged.Output -replace "\r?\n", ';') 'done' 'prunable has a merged branch'
    }
    finally {
        foreach ($name in $savedGitOverrides.Keys) {
            [Environment]::SetEnvironmentVariable($name, $savedGitOverrides[$name])
        }
    }

    # The whole script against a stub executable whose help lists an option with no row: it must fail
    # naming the option, run no row, and leave no temporary folder behind.
    $stubHelp = Join-Path $scratch 'help.txt'
    Set-Content -LiteralPath $stubHelp -Value "Usage: git-wizard [command] [options]`n`nOptions:`n  -?|-h|--help   Show help.`n  --brand-new-verb   Not in the matrix.`n"
    $stub = Join-Path $scratch 'stub.cmd'
    Set-Content -LiteralPath $stub -Value "@echo off`r`nif ""%~1""==""--help"" (type ""%~dp0help.txt"" & exit /b 0)`r`nexit /b 2`r`n"
    $temporaryRoot = Join-Path $scratch 'temporary-root'
    $null = New-Item -ItemType Directory -Path $temporaryRoot
    $matrixScript = Join-Path $scriptsRoot 'cli-verb-matrix.ps1'
    $output = & $pwshPath -NoProfile -File $matrixScript -ExecutablePath $stub -TemporaryRoot $temporaryRoot 2>&1 | Out-String
    Assert-Equal 1 $LASTEXITCODE 'unlisted verb exit code'
    Assert-Contains $output "lists '--brand-new-verb'" 'script reports the unlisted verb'
    Assert-True ($output -notmatch 'PASS\s') 'no row may run after a coverage failure'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $temporaryRoot -Force).Count 'the temporary folder is deleted on failure'

    # A missing executable exits 2 and creates nothing.
    $output = & $pwshPath -NoProfile -File $matrixScript -ExecutablePath (Join-Path $scratch 'absent.exe') -TemporaryRoot $temporaryRoot 2>&1 | Out-String
    Assert-Equal 2 $LASTEXITCODE 'missing executable exit code'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $temporaryRoot -Force).Count 'nothing is created for a missing executable'

    Write-Host 'cli-verb-matrix self-tests passed'
}
finally {
    Stop-MatrixStartedProcesses
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

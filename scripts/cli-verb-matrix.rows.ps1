# The rows and exclusions of scripts/cli-verb-matrix.ps1, dot-sourced by it.
#
# A row is a hashtable: Name, Arguments (placeholders {clean}, {dirty}, {prunable}, {repos}, {home}, {out}, {workname}),
# ExitCode (default 0), Contains and NotContains (strings the combined output must or must not hold),
# optional Setup and Verify script blocks, Fatal (a failure ends the run), Terminal (the row destroys state
# the later rows need, so it must be last) and Probe (a deliberately invalid call whose tokens do not count
# toward coverage). Rows run in order and share one state folder.
#
# Every row is scoped (see Assert-MatrixRowScope): a discovering command carries --no-mft, so nothing
# starts MFTLib's elevated journal broker. Shared options work before or after a subcommand; without
# --paths, inventory uses only the isolated home's seeded search paths, never ~/.pr-crew/repos.

function New-MatrixFileCheck {
    # A Verify block asserting the file at the placeholder path exists and is not empty.
    param([Parameter(Mandatory)][string]$Path)
    return {
        param($context, $result)
        $expanded = Expand-MatrixText $Path $context
        if (-not (Test-Path -LiteralPath $expanded) -or (Get-Item -LiteralPath $expanded).Length -eq 0) { "file '$expanded' is missing or empty" }
    }.GetNewClosure()
}

function New-MatrixJsonCheck {
    # A Verify block asserting standard output is one JSON document.
    return {
        param($context, $result)
        try { $null = $result.Output | ConvertFrom-Json -ErrorAction Stop }
        catch { "standard output is not JSON: $($_.Exception.Message)" }
    }
}

function New-MatrixBranchCheck {
    # A Verify block asserting whether a local branch exists in a fixture repository afterwards.
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Branch, [Parameter(Mandatory)][bool]$ShouldExist)
    return {
        param($context, $result)
        $path = Expand-MatrixText $Repository $context
        $listed = Test-Path -LiteralPath (Join-Path $path ".git/refs/heads/$Branch") -PathType Leaf
        if ($listed -ne $ShouldExist) { "branch '$Branch' in $path should " + $(if ($ShouldExist) { 'exist' } else { 'be gone' }) }
    }.GetNewClosure()
}

function Get-MatrixRows {
    $rows = @(
        @{ Name = 'help'; Arguments = @('--help'); Contains = @('Usage: git-wizard', 'Commands:') }
        @{ Name = 'help-short'; Arguments = @('-h'); Contains = @('Usage: git-wizard') }
        @{ Name = 'help-question'; Arguments = @('-?'); Contains = @('Usage: git-wizard') }
        @{ Name = 'version'; Arguments = @('--version'); Verify = { param($context, $result) if ($result.Output.Trim() -notmatch '^\d+\.\d+\.\d+') { "unexpected version text: $($result.Output.Trim())" } } }
        @{ Name = 'help-tags'; Arguments = @('tags', '--help'); Contains = @('Usage: git-wizard tags') }
        @{ Name = 'help-branches'; Arguments = @('branches', '-h'); Contains = @('Usage: git-wizard branches') }
        @{ Name = 'help-prune'; Arguments = @('prune', '-?'); Contains = @('Usage: git-wizard prune') }
        @{ Name = 'unknown-option'; Arguments = @('--no-mft', '--no-such-option'); ExitCode = 2; Contains = @('Specify --help'); Probe = $true }
        @{ Name = 'unknown-subcommand-option'; Arguments = @('tags', '--no-mft', '-p', '{clean}', '--no-such-option'); ExitCode = 2; Contains = @('Specify --help'); Probe = $true }

        # The first discovery row proves GITWIZARD_HOME and the seeded search path are honored: only the
        # fixture repositories are listed, and nothing from the real user profile.
        @{ Name = 'scan-only-sentinel'; Arguments = @('--no-mft', '--scan-only'); Contains = @('{workname}\repos\clean', '{workname}\repos\dirty', '{workname}\repos\prunable'); NotContains = @('.pr-crew'); Fatal = $true }
        @{ Name = 'scan-only-filter'; Arguments = @('--no-mft', '--scan-only', '--filter', 'prunable'); Contains = @('repos\prunable'); NotContains = @('repos\clean', 'repos\dirty') }
        @{ Name = 'report'; Arguments = @('--no-mft'); ExitCode = 1; Contains = @('Repositories: 3', 'dirty=1', 'merged-branches=1', '{workname}\repos\dirty: dirty, unpushed') }
        @{ Name = 'report-filter-short'; Arguments = @('--no-mft', '-f', 'clean'); Contains = @('Repositories: 1', 'dirty=0') }
        @{ Name = 'report-verbose'; Arguments = @('--no-mft', '--verbose', '--filter', 'clean'); Contains = @('Repositories: 1') }
        @{ Name = 'report-verbose-short'; Arguments = @('--no-mft', '-v', '--filter', 'clean'); Contains = @('Repositories: 1') }
        @{ Name = 'report-silent'; Arguments = @('--no-mft', '--silent', '--filter', 'clean'); NotContains = @('Repositories:') }
        @{ Name = 'report-silent-short'; Arguments = @('--no-mft', '-s', '--filter', 'clean'); NotContains = @('Repositories:') }
        @{ Name = 'report-silent-consumer-alias'; Arguments = @('--no-mft', '-silent', '--filter', 'clean'); NotContains = @('Repositories:') }
        @{ Name = 'report-json'; Arguments = @('--no-mft', '--json', '--filter', 'clean'); Contains = @('"SchemaVersion"'); Verify = (New-MatrixJsonCheck) }
        @{ Name = 'report-json-minified'; Arguments = @('--no-mft', '--json', '--minified', '--filter', 'clean'); Contains = @('"Repositories":{'); Verify = (New-MatrixJsonCheck) }
        @{ Name = 'report-json-all-branches'; Arguments = @('--no-mft', '--json', '--minified', '--all-branches', '--filter', 'prunable'); ExitCode = 1; Contains = @('"BranchScope":"all"') }
        @{ Name = 'report-json-no-local-commit-count'; Arguments = @('--no-mft', '--json', '--minified', '--no-local-commit-count', '--filter', 'clean'); Contains = @('"BranchScope":"actionable"') }
        @{ Name = 'report-print-minified-refused'; Arguments = @('--no-mft', '--print-minified'); ExitCode = 2; Contains = @('--json --minified'); Probe = $true }
        @{ Name = 'report-minified-without-json-refused'; Arguments = @('--no-mft', '--minified'); ExitCode = 2; Contains = @('--json --minified'); Probe = $true }
        @{ Name = 'report-paths'; Arguments = @('--no-mft', '--paths', '{clean}'); Contains = @('Repositories: 1') }
        @{ Name = 'report-paths-short'; Arguments = @('--no-mft', '-p', '{clean},{dirty}'); ExitCode = 1; Contains = @('Repositories: 2') }
        @{ Name = 'report-paths-consumer-alias'; Arguments = @('--no-mft', '-paths', '{clean}'); Contains = @('Repositories: 1') }
        @{ Name = 'report-save-path'; Arguments = @('--no-mft', '--filter', 'clean', '--save-path', '{out}\report.json'); Verify = (New-MatrixFileCheck '{out}\report.json') }
        @{ Name = 'report-save-path-consumer-alias'; Arguments = @('--no-mft', '--filter', 'clean', '-save-path', '{out}\report-alias.json'); Verify = (New-MatrixFileCheck '{out}\report-alias.json') }
        @{ Name = 'report-save-path-missing-directory'; Arguments = @('--no-mft', '--save-path', '{out}\no-such-directory\report.json'); ExitCode = 2; Contains = @('--save-path does not exist'); Probe = $true }
        @{ Name = 'report-config-path'; Arguments = @('--no-mft', '--config-path', '{home}\config.json', '--filter', 'clean'); Contains = @('Repositories: 1') }
        @{ Name = 'report-config-path-missing'; Arguments = @('--no-mft', '--config-path', '{out}\no-such-config.json'); ExitCode = 2; Contains = @('configuration file does not exist'); Probe = $true }
        @{ Name = 'report-rebuild-report'; Arguments = @('--no-mft', '--rebuild-report', '--filter', 'clean'); Contains = @('Repositories: 1') }
        @{ Name = 'report-rebuild-repo-list'; Arguments = @('--no-mft', '--rebuild-repo-list', '--filter', 'clean'); Contains = @('Repositories: 1') }
        @{ Name = 'report-rebuild-all'; Arguments = @('--no-mft', '--rebuild-all', '--filter', 'clean'); Contains = @('Repositories: 1') }
        @{ Name = 'report-no-refresh'; Arguments = @('--no-mft', '--no-refresh', '--filter', 'clean'); Contains = @('Cached report: 1') }
        @{ Name = 'report-no-local-commit-count'; Arguments = @('--no-mft', '--no-local-commit-count', '--filter', 'clean'); Contains = @('Repositories: 1') }

        @{ Name = 'sweep'; Arguments = @('--no-mft', '--sweep', '--paths', '{dirty}'); Contains = @('"DirtyTrackedFiles"', '"StashCount": 1'); Verify = (New-MatrixJsonCheck) }
        @{ Name = 'sweep-print-minified'; Arguments = @('--no-mft', '--sweep', '--paths', '{dirty}', '--print-minified'); Contains = @('"StashCount":1'); Verify = (New-MatrixJsonCheck) }
        @{ Name = 'merge'; Arguments = @('--no-mft', '--merge', '--paths', '{dirty}', '--save-path', '{out}\report.json'); Verify = (New-MatrixFileCheck '{out}\report.json') }
        @{ Name = 'merge-missing-arguments'; Arguments = @('--no-mft', '--merge'); ExitCode = 2; Contains = @('--merge requires both --paths'); Probe = $true }

        @{ Name = 'tags'; Arguments = @('tags', '--no-mft', '--paths', '{clean}', '--minified'); Contains = @('"Name":"archive/one"', '"ExistsOnRemote":true', '"Name":"local/two"', '"ExistsOnRemote":false'); Verify = (New-MatrixJsonCheck) }
        @{ Name = 'tags-match'; Arguments = @('tags', '--no-mft', '--paths', '{clean}', '--match', 'archive/*', '--minified'); Contains = @('"MatchPattern":"archive/*"', 'archive/one'); NotContains = @('local/two') }
        @{ Name = 'tags-match-short'; Arguments = @('tags', '--no-mft', '--paths', '{clean}', '-m', 'local/*', '--minified'); Contains = @('local/two'); NotContains = @('archive/one') }
        @{ Name = 'tags-common-options'; Arguments = @('tags', '--no-mft', '-v', '-p', '{clean},{dirty}', '-f', 'clean', '--save-path', '{out}\tags.json', '--config-path', '{home}\config.json', '--print-minified'); Contains = @('archive/one'); NotContains = @('dirty'); Verify = (New-MatrixFileCheck '{out}\tags.json') }
        @{ Name = 'tags-common-options-before'; Arguments = @('--no-mft', '-v', '-p', '{clean},{dirty}', '-f', 'clean', '--save-path', '{out}\tags-before.json', '--config-path', '{home}\config.json', '--print-minified', 'tags'); Contains = @('archive/one'); NotContains = @('dirty'); Verify = (New-MatrixFileCheck '{out}\tags-before.json') }
        @{ Name = 'tags-options-mixed'; Arguments = @('--no-mft', '-v', 'tags', '-p', '{clean}', '--minified'); Contains = @('"Name":"archive/one"', '"ExistsOnRemote":true', '"Name":"local/two"', '"ExistsOnRemote":false'); Verify = (New-MatrixJsonCheck) }
        @{ Name = 'tags-root-only-option-refused'; Arguments = @('--all-branches', 'tags', '--no-mft'); ExitCode = 2; Contains = @("The root option --all-branches is not accepted by the 'tags' subcommand."); Probe = $true }
        @{
            Name = 'tags-configured-search'; Arguments = @('tags', '--no-mft', '--minified')
            Contains = @('repos\\clean', 'repos\\dirty', 'repos\\prunable', '"Name":"archive/one"', '"Name":"local/two"'); NotContains = @('.pr-crew'); Verify = (New-MatrixJsonCheck)
            # Inventories omit repositories with no items. Seed visible items after the ordinary
            # report rows, so all three repositories can be asserted without changing report counts.
            Setup = {
                param($context)
                $gitPath = (Get-Command git -CommandType Application | Select-Object -First 1).Source
                foreach ($repository in @($context.dirty, $context.prunable, $context.clean)) {
                    $operation = if ($repository -eq $context.clean) { 'branch' } else { 'tag' }
                    $run = Invoke-MatrixProcess -ExecutablePath $gitPath -Arguments @('-C', $repository, $operation, 'fixture-inventory', 'main') -Environment (Get-MatrixGitEnvironment)
                    if ($run.TimedOut -or $run.ExitCode -ne 0) { throw "git $operation fixture-inventory failed in ${repository}: $($run.Combined)" }
                }
            }
        }
        @{ Name = 'tags-silent'; Arguments = @('tags', '--no-mft', '-p', '{clean}', '--silent'); NotContains = @('SchemaVersion') }
        @{ Name = 'tags-silent-short'; Arguments = @('tags', '--no-mft', '-p', '{clean}', '-s'); NotContains = @('SchemaVersion') }
        @{ Name = 'tags-verbose-long'; Arguments = @('tags', '--no-mft', '-p', '{clean}', '--verbose', '--minified'); Contains = @('archive/one') }
        @{ Name = 'tags-filter-long'; Arguments = @('tags', '--no-mft', '--paths', '{clean},{dirty}', '--filter', 'dirty', '--minified'); NotContains = @('archive/one') }

        @{ Name = 'branches'; Arguments = @('branches', '--no-mft', '--paths', '{dirty}', '--minified'); Contains = @('"Name":"wip"', '"IsMerged":false'); Verify = (New-MatrixJsonCheck) }
        @{ Name = 'branches-local-only'; Arguments = @('branches', '--no-mft', '--paths', '{dirty},{prunable}', '--local-only', '--minified'); Contains = @('"Name":"wip"', '"Name":"open"') }
        @{ Name = 'branches-not-merged'; Arguments = @('branches', '--no-mft', '--paths', '{prunable}', '--not-merged', 'main', '--minified'); Contains = @('"BaseBranch":"main"', '"Name":"open"'); NotContains = @('"Name":"done"') }
        @{ Name = 'branches-not'; Arguments = @('branches', '--no-mft', '--paths', '{prunable}', '--not', 'main', '--minified'); Contains = @('"BaseBranch":"main"', '"Name":"open"'); NotContains = @('"Name":"done"') }
        @{ Name = 'branches-common-options'; Arguments = @('branches', '--no-mft', '-v', '-p', '{dirty},{clean}', '-f', 'dirty', '--save-path', '{out}\branches.json', '--config-path', '{home}\config.json', '--print-minified'); Contains = @('"Name":"wip"'); Verify = (New-MatrixFileCheck '{out}\branches.json') }
        @{ Name = 'branches-common-options-before'; Arguments = @('--no-mft', '-v', '-p', '{dirty},{clean}', '-f', 'dirty', '--save-path', '{out}\branches-before.json', '--config-path', '{home}\config.json', '--print-minified', 'branches'); Contains = @('"Name":"wip"'); Verify = (New-MatrixFileCheck '{out}\branches-before.json') }
        @{ Name = 'branches-options-mixed'; Arguments = @('--no-mft', '-v', 'branches', '-p', '{dirty}', '--minified'); Contains = @('"Name":"wip"', '"IsMerged":false'); Verify = (New-MatrixJsonCheck) }
        @{ Name = 'branches-root-only-option-refused'; Arguments = @('--all-branches', 'branches', '--no-mft'); ExitCode = 2; Contains = @("The root option --all-branches is not accepted by the 'branches' subcommand."); Probe = $true }
        @{ Name = 'branches-configured-search'; Arguments = @('branches', '--no-mft', '--minified'); Contains = @('repos\\clean', 'repos\\dirty', 'repos\\prunable', '"Name":"wip"', '"Name":"open"'); NotContains = @('.pr-crew'); Verify = (New-MatrixJsonCheck) }
        @{ Name = 'branches-silent'; Arguments = @('branches', '--no-mft', '-p', '{dirty}', '--silent'); NotContains = @('SchemaVersion') }
        @{ Name = 'branches-silent-short'; Arguments = @('branches', '--no-mft', '-p', '{dirty}', '-s'); NotContains = @('SchemaVersion') }
        @{ Name = 'branches-verbose-long'; Arguments = @('branches', '--no-mft', '-p', '{dirty}', '--verbose', '--minified'); Contains = @('"Name":"wip"') }
        @{ Name = 'branches-filter-long'; Arguments = @('branches', '--no-mft', '--paths', '{clean},{dirty}', '--filter', 'dirty', '--minified'); Contains = @('"Name":"wip"') }

        @{ Name = 'prune-dry-run'; Arguments = @('prune', '--no-mft', '--paths', '{prunable}', '--base', 'main', '--minified'); Contains = @('"Applied":false', '"Name":"done"', '"Action":"would-delete"'); Verify = (New-MatrixBranchCheck '{prunable}' 'done' $true) }
        @{ Name = 'prune-branches'; Arguments = @('prune', '--no-mft', '--paths', '{prunable}', '--branches', '--base', 'main', '--minified'); Contains = @('"Kind":"branch"', '"Name":"done"'); NotContains = @('"Kind":"stash"') }
        @{ Name = 'prune-stashes'; Arguments = @('prune', '--no-mft', '--paths', '{dirty}', '--stashes', '--base', 'main', '--minified'); Contains = @('"Kind":"stash"', '"SkipReason":"not-merged"'); NotContains = @('"Kind":"branch"') }
        @{ Name = 'prune-common-options'; Arguments = @('prune', '--no-mft', '-v', '-p', '{prunable},{clean}', '-f', 'prunable', '--base', 'main', '--save-path', '{out}\prune.json', '--config-path', '{home}\config.json', '--print-minified'); Contains = @('"Name":"done"'); Verify = (New-MatrixFileCheck '{out}\prune.json') }
        @{ Name = 'prune-common-options-before'; Arguments = @('--no-mft', '-v', '-p', '{prunable},{clean}', '-f', 'prunable', '--save-path', '{out}\prune-before.json', '--config-path', '{home}\config.json', '--print-minified', 'prune', '--base', 'main'); Contains = @('"Name":"done"'); Verify = (New-MatrixFileCheck '{out}\prune-before.json') }
        @{ Name = 'prune-options-mixed'; Arguments = @('--no-mft', '-v', 'prune', '-p', '{prunable}', '--base', 'main', '--minified'); Contains = @('"Applied":false', '"Name":"done"', '"Action":"would-delete"'); Verify = (New-MatrixBranchCheck '{prunable}' 'done' $true) }
        @{ Name = 'prune-root-only-option-refused'; Arguments = @('--all-branches', 'prune', '--no-mft'); ExitCode = 2; Contains = @("The root option --all-branches is not accepted by the 'prune' subcommand."); Probe = $true }
        @{ Name = 'prune-configured-search'; Arguments = @('prune', '--no-mft', '--base', 'main', '--minified'); Contains = @('repos\\clean', 'repos\\dirty', 'repos\\prunable', '"Applied":false', '"Name":"done"', '"Action":"would-delete"'); NotContains = @('.pr-crew'); Verify = (New-MatrixBranchCheck '{prunable}' 'done' $true) }
        @{ Name = 'prune-silent'; Arguments = @('prune', '--no-mft', '-p', '{prunable}', '--base', 'main', '--silent'); NotContains = @('SchemaVersion') }
        @{ Name = 'prune-silent-short'; Arguments = @('prune', '--no-mft', '-p', '{prunable}', '--base', 'main', '-s'); NotContains = @('SchemaVersion') }
        @{ Name = 'prune-verbose-long'; Arguments = @('prune', '--no-mft', '-p', '{prunable}', '--base', 'main', '--verbose', '--minified'); Contains = @('"Name":"done"') }
        @{ Name = 'prune-filter-long'; Arguments = @('prune', '--no-mft', '--paths', '{prunable},{clean}', '--filter', 'clean', '--base', 'main', '--minified'); NotContains = @('"Name":"done"') }
        @{ Name = 'prune-apply'; Arguments = @('prune', '--no-mft', '--paths', '{prunable}', '--branches', '--apply', '--base', 'main', '--minified'); Contains = @('"Applied":true'); Verify = { param($context, $result) $one = & (New-MatrixBranchCheck '{prunable}' 'done' $false) $context $result; $two = & (New-MatrixBranchCheck '{prunable}' 'open' $true) $context $result; if ($one) { $one } elseif ($two) { $two } } }

        @{ Name = 'db-size'; Arguments = @('--db-size'); Verify = { param($context, $result) if ($result.Output.Trim() -notmatch '^[\d.,]+ ?(B|bytes|KB|MB|GB)') { "unexpected size text: $($result.Output.Trim())" } } }
        @{ Name = 'index-cache-status'; Arguments = @('--index-cache-status'); Contains = @('Index cache status') }
        @{ Name = 'index-cache-status-with-clear-refused'; Arguments = @('--index-cache-status', '--clear-cache'); ExitCode = 2; Contains = @('cannot be combined'); Probe = $true }
        @{ Name = 'clear-index-cache'; Arguments = @('--clear-index-cache'); Contains = @('Cache inspection') }
        @{ Name = 'clear-cache'; Arguments = @('--clear-cache', '--no-refresh', '--no-mft'); Verify = { param($context, $result) foreach ($name in 'repositories.txt', 'report.json') { if (Test-Path -LiteralPath (Join-Path $context.home $name)) { "$name survived --clear-cache" } } } }
        @{ Name = 'delete-all-local-files-refused'; Arguments = @('--delete-all-local-files', '--no-refresh', '--no-mft'); Setup = { param($context) $null = New-Item -ItemType Directory -Path (Join-Path $context.home 'mft-index') -Force }; ExitCode = 1; Contains = @('while mft-index exists'); Verify = { param($context, $result) if (-not (Test-Path -LiteralPath (Join-Path $context.home 'config.json'))) { 'a refused delete removed config.json' } } }
        @{ Name = 'delete-all-local-files'; Arguments = @('--delete-all-local-files', '--no-refresh', '--no-mft'); Setup = { param($context) Remove-Item -LiteralPath (Join-Path $context.home 'mft-index') -Recurse -Force }; Terminal = $true; Verify = { param($context, $result) if (Test-Path -LiteralPath (Join-Path $context.home 'config.json')) { 'config.json survived --delete-all-local-files' } } }
    )
    foreach ($row in $rows) { Assert-MatrixRowScope $row }
    # delete-all-local-files removes the seeded configuration, after which discovery would use the
    # default search paths, so a Terminal row anywhere but last is a definition error.
    for ($index = 0; $index -lt $rows.Count - 1; $index++) {
        if ($rows[$index].ContainsKey('Terminal') -and $rows[$index].Terminal) { throw "Matrix row '$($rows[$index].Name)' is Terminal but is not the last row." }
    }
    return $rows
}

function Get-MatrixExclusions {
    return @(
        @{ Token = '--watch'; Reason = 'Starts MFTLib''s elevated journal broker on Windows (UAC prompt), and --no-mft does not provably avoid it; the watch is also a long-running stream.' }
        @{ Token = '--setup-defender'; Reason = 'Adds Windows Defender exclusions through an elevated helper (UAC prompt) and changes machine state.' }
    )
}

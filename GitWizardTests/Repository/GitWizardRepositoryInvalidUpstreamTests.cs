using System.Collections.Concurrent;
using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

/// <summary>
/// Regression tests for git-wizard#185: a branch whose <c>branch.&lt;name&gt;.remote</c> is a URL
/// (git's "fetch from this URL" form) or a stale remote name must be reported as untracked
/// instead of throwing inside LibGit2Sharp's TrackedBranch resolution, with exactly one
/// diagnostic per (common repository, branch, value) across all worktree paths.
/// NonParallelizable because GitWizardLog.LogMethod is global static state.
/// </summary>
[NonParallelizable]
// Upstream tests exercise invalid upstream tracking diagnostics and remote integration.
public class GitWizardRepositoryInvalidUpstreamTests
{
    const string DiagnosticMarker = "does not name a configured remote";

    Action<string?> _originalLogMethod = Console.WriteLine;
    bool _originalSilentMode;
    readonly ConcurrentQueue<string> _loggedMessages = new();

    [SetUp]
    public void SetUp()
    {
        _loggedMessages.Clear();
        _originalLogMethod = GitWizardLog.LogMethod;
        _originalSilentMode = GitWizardLog.SilentMode;
        GitWizardLog.SilentMode = false;
        GitWizardLog.LogMethod = message =>
        {
            if (message != null)
            {
                _loggedMessages.Enqueue(message);
            }
        };
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.SilentMode = _originalSilentMode;
        GitWizardLog.LogMethod = _originalLogMethod;
    }

    int DiagnosticsFor(string branchName) =>
        _loggedMessages.Count(message =>
            message.Contains(branchName, StringComparison.Ordinal) &&
            message.Contains(DiagnosticMarker, StringComparison.Ordinal));

    int ExceptionRecordsFor(string branchName) =>
        _loggedMessages.Count(message =>
            message.Contains(branchName, StringComparison.Ordinal) &&
            message.Contains("Exception", StringComparison.Ordinal));

    static void SetUpstreamRemote(string repositoryPath, string branchName, string remoteValue)
    {
        // Keep the branch name dot-free: the config key is composed by string, and a
        // dotted branch name would not parse back as the same section.
        using var repository = new Repository(repositoryPath);
        repository.Config.Set($"branch.{branchName}.remote", remoteValue);
        repository.Config.Set($"branch.{branchName}.merge", $"refs/heads/{branchName}");
    }

    /// <summary>
    /// Write a <c>branch.&lt;name&gt;.remote</c> value directly into the config file rather than
    /// through <c>git config</c> or <c>Configuration.Set</c>: a value large enough to
    /// exceed a pipe's buffer (git-wizard#197 round-6 review) also exceeds the ~32K Windows
    /// command-line length limit, so it cannot be passed as a process argument at all.
    /// </summary>
    static void SetUpstreamRemoteDirectlyInConfigFile(string repositoryPath, string branchName, string remoteValue)
    {
        var configPath = Path.Combine(repositoryPath, ".git", "config");
        File.AppendAllText(configPath,
            $"\n[branch \"{branchName}\"]\n\tremote = {remoteValue}\n\tmerge = refs/heads/{branchName}\n");
    }

    static void CheckoutBranch(string repositoryPath, string branchName)
    {
        using var repository = new Repository(repositoryPath);
        Commands.Checkout(repository, repository.Branches[branchName]);
    }

    static string GitDirectory(string repositoryPath) =>
        Path.Combine(repositoryPath, ".git").Replace('\\', '/');

    static string WriteIncludeFile(string directory, string fileName, string contents)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>
    /// Register a <c>gitdir:</c> conditional include matched exactly against
    /// <paramref name="gitDirectory"/> (forward slashes, no trailing slash - the form git's own
    /// documentation uses for a single repository; a trailing slash turns the pattern into a
    /// <c>**</c>-suffixed glob for a group of repositories and, verified empirically against git
    /// 2.46 on Windows, does not match the exact directory it is anchored to) that pulls in
    /// <paramref name="includeFilePath"/>. Only config resolution with repository context (native
    /// <c>Branch.TrackedBranch</c>) evaluates conditional includes; a standalone
    /// <c>Configuration.BuildFrom</c> read of the base config file never sees the override.
    /// </summary>
    static void AddConditionalInclude(string repositoryPath, string gitDirectory, string includeFilePath)
    {
        TempRepoFixture.RunGit(repositoryPath, "config", "--add",
            $"includeIf.gitdir:{gitDirectory}.path", includeFilePath.Replace('\\', '/'));
    }

    [Test]
    public void Refresh_BranchWithUrlValuedRemote_IsReportedAsUntrackedWithOneDiagnostic()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("url-remote", "url-remote.txt");
        SetUpstreamRemote(fixture.Path, "url-remote", "https://example.invalid/owner/repo.git");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        var branch = repo.Branches?.FirstOrDefault(b => b.Name == "url-remote");
        Assert.Multiple(() =>
        {
            Assert.That(branch, Is.Not.Null,
                "A branch with a URL-valued remote must stay in the branch list.");
            Assert.That(branch!.HasUpstream, Is.False,
                "A URL-valued remote names no configured remote, so the branch is untracked.");
            Assert.That(branch.AheadOfDefault, Is.EqualTo(1),
                "Default-branch divergence is independent of upstream resolution.");
            Assert.That(DiagnosticsFor("url-remote"), Is.EqualTo(1),
                "Exactly one diagnostic names the branch and its offending config value.");
            Assert.That(ExceptionRecordsFor("url-remote"), Is.EqualTo(0),
                "The misconfigured upstream must not surface as an exception record.");
        });
        Assert.That(
            _loggedMessages.Any(m => m.Contains("https://example.invalid/owner/repo.git", StringComparison.Ordinal)),
            Is.True, "The diagnostic names the offending config value.");
    }

    [Test]
    public void Refresh_BranchWithRemoteValueLargerThanPipeBuffer_IsReportedAsUntrackedWithOneDiagnostic()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("large-remote", "large-remote.txt");

        // Large enough to exceed a pipe's default buffer capacity (git-wizard#197 round-6
        // review, reviewer repro: a 131,096-char value stalled 5,002 ms before this fix).
        // Reading stdout only after the process exits lets git block on the write once the
        // value outgrows the pipe, which then trips the timeout below and drops the branch
        // instead of reporting it as untracked.
        const string prefix = "https://example.invalid/";
        var largeRemoteValue = prefix + new string('a', 131_096 - prefix.Length);
        SetUpstreamRemoteDirectlyInConfigFile(fixture.Path, "large-remote", largeRemoteValue);

        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        var branch = repo.Branches?.FirstOrDefault(b => b.Name == "large-remote");
        Assert.Multiple(() =>
        {
            Assert.That(branch, Is.Not.Null,
                "A branch whose effective remote value is larger than a pipe buffer must stay in the branch list.");
            Assert.That(branch!.HasUpstream, Is.False,
                "The oversized value names no configured remote, so the branch is untracked.");
            Assert.That(DiagnosticsFor("large-remote"), Is.EqualTo(1),
                "Exactly one diagnostic names the branch and its offending config value.");
            Assert.That(ExceptionRecordsFor("large-remote"), Is.EqualTo(0),
                "An oversized remote value must not time out and surface as an exception record.");
        });
        Assert.That(
            _loggedMessages.Any(m => m.Contains(largeRemoteValue, StringComparison.Ordinal)),
            Is.True, "The diagnostic names the offending value in full.");
    }

    [Test]
    public void Refresh_BranchWithStaleRemoteName_IsReportedAsUntrackedWithOneDiagnostic()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("stale-remote", "stale-remote.txt");
        SetUpstreamRemote(fixture.Path, "stale-remote", "no-such-remote");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        var branch = repo.Branches?.FirstOrDefault(b => b.Name == "stale-remote");
        Assert.Multiple(() =>
        {
            Assert.That(branch, Is.Not.Null);
            Assert.That(branch!.HasUpstream, Is.False,
                "A remote name that is not configured means no upstream.");
            Assert.That(branch.AheadOfDefault, Is.EqualTo(1));
            Assert.That(DiagnosticsFor("stale-remote"), Is.EqualTo(1),
                "A stale remote name gets the same single diagnostic as a URL value.");
            Assert.That(ExceptionRecordsFor("stale-remote"), Is.EqualTo(0));
        });
        Assert.That(
            _loggedMessages.Any(m => m.Contains("no-such-remote", StringComparison.Ordinal)),
            Is.True, "The diagnostic names the offending config value.");
    }

    [Test]
    public void Refresh_UrlValuedRemote_LogsOncePerRepositoryAcrossWorktreePaths()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("url-remote", "url-remote.txt");
        SetUpstreamRemote(fixture.Path, "url-remote", "https://example.invalid/owner/repo.git");
        var worktreePath = fixture.AddWorktree("wt-url-remote");

        var report = new GitWizardReport();
        report.Refresh(new[] { fixture.Path, worktreePath }, fetchRemotes: false, deepRefresh: false, allBranches: true, computeLocalCommitCount: true);

        Assert.That(DiagnosticsFor("url-remote"), Is.EqualTo(1),
            "All worktrees share one config; the diagnostic is logged once per repository.");
        Assert.That(ExceptionRecordsFor("url-remote"), Is.EqualTo(0));
    }

    [Test]
    public void Refresh_HeadBranchWithUrlValuedRemote_RemoteDivergenceStaysZeroWithOneDiagnostic()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("url-remote", "url-remote.txt");
        SetUpstreamRemote(fixture.Path, "url-remote", "https://example.invalid/owner/repo.git");
        CheckoutBranch(fixture.Path, "url-remote");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        Assert.Multiple(() =>
        {
            Assert.That(repo.AheadOfRemoteCount, Is.EqualTo(0),
                "No resolvable upstream means the existing no-upstream 0/0 representation.");
            Assert.That(repo.BehindRemoteCount, Is.EqualTo(0));
            Assert.That(DiagnosticsFor("url-remote"), Is.EqualTo(1),
                "CollectBranches and RefreshRemoteDivergence dedupe onto one diagnostic.");
            Assert.That(
                _loggedMessages.Any(m => m.Contains("Exception computing remote divergence", StringComparison.Ordinal)),
                Is.False, "RefreshRemoteDivergence must not fall into its exception path.");
        });
    }

    [Test]
    public void Refresh_RepositoryWithUrlValuedRemoteBranch_NormallyTrackedBranchStillReportsDivergence()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();
        fixture.CommitOnNewBranch("url-remote", "url-remote.txt");
        SetUpstreamRemote(fixture.Path, "url-remote", "https://example.invalid/owner/repo.git");
        fixture.AppendCommit("unpushed.txt");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        var defaultName = repo.DefaultBranch;
        var defaultBranch = repo.Branches?.FirstOrDefault(b => b.Name == defaultName);
        var urlBranch = repo.Branches?.FirstOrDefault(b => b.Name == "url-remote");
        Assert.Multiple(() =>
        {
            Assert.That(repo.AheadOfRemoteCount, Is.GreaterThan(0),
                "The checked-out default branch is ahead of its real upstream.");
            Assert.That(defaultBranch, Is.Not.Null);
            Assert.That(defaultBranch!.HasUpstream, Is.True,
                "A normally tracked branch still resolves its upstream.");
            Assert.That(urlBranch, Is.Not.Null);
            Assert.That(urlBranch!.HasUpstream, Is.False);
            Assert.That(DiagnosticsFor("url-remote"), Is.EqualTo(1));
        });
    }

    [Test]
    public void Refresh_LinkedWorktreeWithValidTracking_StillReportsUpstreamThroughFullRefresh()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();

        // create a new branch and check it out
        using (var repo = new Repository(fixture.Path))
        {
            var newBranch = repo.Branches.Add("wt-branch", repo.Head.Tip);
            Commands.Checkout(repo, newBranch);
        }

        fixture.AppendCommit("wt.txt");
        fixture.AddOriginRemoteAndPush(); // pushes current branch (wt-branch) to origin/wt-branch

        // Checkout main in the main repo so worktree can check out wt-branch
        using (var repo = new Repository(fixture.Path))
        {
            Commands.Checkout(repo, repo.Branches["master"] ?? repo.Branches["main"]);
        }

        using var worktree = new TempDirectory("gw-wt-valid-", create: false);
        var worktreePath = worktree.Path;
        TempRepoFixture.RunGit(fixture.Path, "worktree", "add", worktreePath, "wt-branch");

        // The explicit identity matters on CI hosts with no global git identity, where
        // the commit would otherwise fail and leave nothing ahead of the upstream.
        TempRepoFixture.RunGit(worktreePath, "-c", "user.email=test@example.com", "-c", "user.name=Test",
            "commit", "--allow-empty", "-m", "unpushed commit");

        var worktreeRepo = new GitWizardRepository(worktreePath);
        worktreeRepo.IsWorktree = true;
        worktreeRepo.Refresh(allBranches: true);

        var branch = worktreeRepo.Branches?.FirstOrDefault(b => b.Name == "wt-branch");
        Assert.Multiple(() =>
        {
            Assert.That(branch, Is.Not.Null);
            Assert.That(branch!.HasUpstream, Is.True, "Valid tracking in a linked worktree should report HasUpstream = true");
            Assert.That(worktreeRepo.AheadOfRemoteCount, Is.GreaterThan(0), "Should report ahead count for linked worktree");
        });
    }

    [Test]
    public void Refresh_BranchWithUrlValuedRemote_SecondRefreshLogsWarningAgain()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("url-remote", "url-remote.txt");
        SetUpstreamRemote(fixture.Path, "url-remote", "https://example.invalid/owner/repo.git");
        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);
        Assert.That(DiagnosticsFor("url-remote"), Is.EqualTo(1));

        repo.Refresh(allBranches: true);
        Assert.That(DiagnosticsFor("url-remote"), Is.EqualTo(2), "Second refresh should log the warning again");
    }

    [Test]
    public void Refresh_RepoNamedWorktrees_KeyedCorrectlyAndSharesKeyWithLinkedWorktree()
    {
        // A unique parent per run, with the repository ITSELF (not a subdirectory of it) named
        // "worktrees" - the case the old path-suffix heuristic could mistake for a linked
        // worktree.
        using var uniqueParent = new TempDirectory();
        var tempRoot = Path.Combine(uniqueParent.Path, "worktrees");
        Directory.CreateDirectory(tempRoot);
        Repository.Init(tempRoot);
        using (var repo = new Repository(tempRoot))
        {
            File.WriteAllText(Path.Combine(tempRoot, "README.md"), "initial");
            Commands.Stage(repo, "README.md");
            repo.Commit("initial", new Signature("Test", "t@t.com", DateTimeOffset.Now), new Signature("Test", "t@t.com", DateTimeOffset.Now));
            repo.Branches.Add("url-remote", repo.Head.Tip);
            Commands.Checkout(repo, repo.Branches["url-remote"]);
            File.WriteAllText(Path.Combine(tempRoot, "url-remote.txt"), "test");
            Commands.Stage(repo, "url-remote.txt");
            repo.Commit("add url-remote.txt", new Signature("Test", "t@t.com", DateTimeOffset.Now), new Signature("Test", "t@t.com", DateTimeOffset.Now));
            Commands.Checkout(repo, repo.Branches["master"] ?? repo.Branches["main"]);
        }
        SetUpstreamRemote(tempRoot, "url-remote", "https://example.invalid/owner/repo.git");

        using var worktree = new TempDirectory("gw-wt-", create: false);
        var worktreePath = worktree.Path;
        TempRepoFixture.RunGit(tempRoot, "worktree", "add", worktreePath, "-b", "wt-url-remote");

        var report = new GitWizardReport();
        report.Refresh(new[] { tempRoot, worktreePath }, allBranches: true);

        Assert.That(DiagnosticsFor("url-remote"), Is.EqualTo(1),
            "Main and linked paths of one repo should share a key even if the repo is in a directory named 'worktrees'");
    }

    [Test]
    public void Refresh_BranchWithQuotedWhitespaceRemoteValue_IsReportedAsUntrackedWithOneDiagnostic()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush(); // "origin" becomes a real, configured remote.
        fixture.CommitOnNewBranch("padded-remote", "padded-remote.txt");

        // git config discards unquoted leading/trailing whitespace, so a value git reports back
        // with surrounding whitespace was quoted in the file (remote = " origin ") and that
        // whitespace is significant: it does NOT name the configured "origin" remote.
        SetUpstreamRemote(fixture.Path, "padded-remote", " origin ");

        var repo = new GitWizardRepository(fixture.Path);

        repo.Refresh(allBranches: true);

        var branch = repo.Branches?.FirstOrDefault(b => b.Name == "padded-remote");
        Assert.Multiple(() =>
        {
            Assert.That(branch, Is.Not.Null,
                "A branch whose effective remote is whitespace-padded must stay in the branch list.");
            Assert.That(branch!.HasUpstream, Is.False,
                "' origin ' does not name the configured 'origin' remote; trimming it away would wrongly match.");
            Assert.That(DiagnosticsFor("padded-remote"), Is.EqualTo(1),
                "Exactly one diagnostic names the branch and its offending (untrimmed) config value.");
            Assert.That(ExceptionRecordsFor("padded-remote"), Is.EqualTo(0),
                "The whitespace-padded remote must not be mistaken for the configured remote and rethrown.");
        });
        Assert.That(
            _loggedMessages.Any(m => m.Contains("' origin '", StringComparison.Ordinal)),
            Is.True, "The diagnostic names the offending value verbatim, whitespace included.");
    }

    [Test]
    public void Refresh_ConditionalIncludeOverridesValidRemoteToUrl_IsReportedAsUntrackedWithOneDiagnostic()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush(); // "origin" becomes a real, configured remote.
        fixture.CommitOnNewBranch("cond-url", "cond-url.txt");

        // Base config: a remote name that IS configured, so the standalone read passes the
        // guard and lets execution reach TrackedBranch.
        SetUpstreamRemote(fixture.Path, "cond-url", "origin");

        var includeDirectory = fixture.Path + "-include";
        try
        {
            var includeFile = WriteIncludeFile(includeDirectory, "override.gitconfig",
                "[branch \"cond-url\"]\n\tremote = https://example.invalid/owner/repo.git\n");
            AddConditionalInclude(fixture.Path, GitDirectory(fixture.Path), includeFile);

            var repo = new GitWizardRepository(fixture.Path);
            repo.Refresh(allBranches: true);

            var branch = repo.Branches?.FirstOrDefault(b => b.Name == "cond-url");
            Assert.Multiple(() =>
            {
                Assert.That(branch, Is.Not.Null,
                    "The branch whose effective upstream is a URL must stay in the branch list.");
                Assert.That(branch!.HasUpstream, Is.False,
                    "Native resolution evaluates the conditional include, sees the URL, and reports no upstream.");
                Assert.That(DiagnosticsFor("cond-url"), Is.EqualTo(1),
                    "Exactly one diagnostic is logged for the unresolved upstream.");
                Assert.That(ExceptionRecordsFor("cond-url"), Is.EqualTo(0),
                    "The conditional-include override must not surface as an exception record.");
            });
            Assert.That(
                _loggedMessages.Any(m => m.Contains("https://example.invalid/owner/repo.git", StringComparison.Ordinal)),
                Is.True,
                "The diagnostic must name the effective (conditionally-included) offending value, not a placeholder.");
        }
        finally
        {
            TempDirectory.DeleteTree(includeDirectory);
        }
    }

    [Test]
    public void Refresh_BranchWithValidRemoteAndMalformedMergeRef_SurfacesThroughExistingExceptionDiagnostics()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush(); // "origin" becomes a real, configured remote.
        fixture.CommitOnNewBranch("bad-merge", "bad-merge.txt");
        SetUpstreamRemote(fixture.Path, "bad-merge", "origin");

        // Override the merge ref alone with a malformed value. TrackedBranch still throws
        // InvalidSpecificationException (git rejects the constructed reference name), but the
        // effective branch.bad-merge.remote read via git names a real, configured remote, so
        // this is NOT the invalid-remote condition.
        using (var repository = new Repository(fixture.Path))
        {
            repository.Config.Set("branch.bad-merge.merge", "refs/heads/bad..ref");
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        var branch = repo.Branches?.FirstOrDefault(b => b.Name == "bad-merge");
        Assert.Multiple(() =>
        {
            Assert.That(branch, Is.Null,
                "CollectBranches drops a branch whose resolution throws, the same as any other branch-collection exception.");
            Assert.That(DiagnosticsFor("bad-merge"), Is.EqualTo(0),
                "A malformed merge ref with a valid, configured remote is not the invalid-remote condition and must not log that diagnostic.");
            Assert.That(ExceptionRecordsFor("bad-merge"), Is.EqualTo(1),
                "The malformed ref must surface through CollectBranches' existing exception logging instead of being misreported as a bad remote.");
        });
    }

    [Test]
    public void Refresh_LocalToLocalBranchWithMalformedMergeRef_SurfacesThroughExistingExceptionDiagnostics()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("bad-merge-local", "bad-merge-local.txt");
        SetUpstreamRemote(fixture.Path, "bad-merge-local", ".");

        // Override the merge ref alone with a malformed value. remote = "." is local-to-local
        // tracking, not a remote name, so the effective-remote read must not treat this as the
        // invalid-remote condition either.
        using (var repository = new Repository(fixture.Path))
        {
            repository.Config.Set("branch.bad-merge-local.merge", "refs/heads/bad..ref");
        }

        var repo = new GitWizardRepository(fixture.Path);
        repo.Refresh(allBranches: true);

        var branch = repo.Branches?.FirstOrDefault(b => b.Name == "bad-merge-local");
        Assert.Multiple(() =>
        {
            Assert.That(branch, Is.Null,
                "CollectBranches drops a branch whose resolution throws, the same as any other branch-collection exception.");
            Assert.That(DiagnosticsFor("bad-merge-local"), Is.EqualTo(0),
                "remote = \".\" is local-to-local tracking, not the invalid-remote condition, and must not log that diagnostic.");
            Assert.That(ExceptionRecordsFor("bad-merge-local"), Is.EqualTo(1),
                "The malformed ref must surface through CollectBranches' existing exception logging instead of being misreported as a bad remote.");
        });
    }

    [Test]
    public void Refresh_ConditionalIncludeOverridesStaleRemoteToValid_ReportsUpstreamAndDivergence()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.CommitOnNewBranch("cond-valid", "cond-valid.txt");
        CheckoutBranch(fixture.Path, "cond-valid");
        fixture.AddOriginRemoteAndPush(); // sets normal tracking to origin/cond-valid.

        // Overwrite the base config with a stale remote name; the standalone read sees only
        // this and would report the branch as unresolved.
        SetUpstreamRemote(fixture.Path, "cond-valid", "no-such-remote");

        var includeDirectory = fixture.Path + "-include";
        try
        {
            // The conditional include restores the real, working remote. Only native
            // resolution (repository context) evaluates it.
            var includeFile = WriteIncludeFile(includeDirectory, "override.gitconfig",
                "[branch \"cond-valid\"]\n\tremote = origin\n");
            AddConditionalInclude(fixture.Path, GitDirectory(fixture.Path), includeFile);

            fixture.AppendCommit("unpushed.txt"); // ahead of the real upstream by one commit.

            var repo = new GitWizardRepository(fixture.Path);
            repo.Refresh(allBranches: true);

            var branch = repo.Branches?.FirstOrDefault(b => b.Name == "cond-valid");
            Assert.Multiple(() =>
            {
                Assert.That(branch, Is.Not.Null);
                Assert.That(branch!.HasUpstream, Is.True,
                    "Native resolution evaluates the conditional include and resolves the real remote.");
                Assert.That(repo.AheadOfRemoteCount, Is.GreaterThan(0),
                    "The checked-out branch is ahead of its real (conditionally-included) upstream.");
                Assert.That(DiagnosticsFor("cond-valid"), Is.EqualTo(0),
                    "A resolvable upstream must not log the unresolved-upstream diagnostic.");
            });
        }
        finally
        {
            TempDirectory.DeleteTree(includeDirectory);
        }
    }



}

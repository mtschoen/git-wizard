using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

public class RepositoryIndexRefreshTests
{
    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, false)]
    public void Refresh_OldLockWithPossibleWriterIsPreserved_Issue482(bool gitProcessRunning, bool canOpenLock)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var library = new Repository(fixture.Path);
        var lockPath = Path.Combine(library.Info.Path, "index.lock");
        File.WriteAllText(lockPath, "writer owns this lock");
        File.SetLastWriteTimeUtc(lockPath, FrozenCacheClock.Now.AddHours(-1).UtcDateTime);
        var indexPath = Path.Combine(library.Info.Path, "index");
        var indexBytes = File.ReadAllBytes(indexPath);
        var indexTimestamp = File.GetLastWriteTimeUtc(indexPath);
        var repository = new GitWizardRepository(fixture.Path)
        {
            IndexRefreshReason = RepositoryIndexRefreshReason.DeadIndexLock
        };
        var service = new RepositoryIndexRefreshService(new FrozenCacheClock())
        {
            GitProcessRunning = () => gitProcessRunning,
            CanOpenLockExclusively = _ => canOpenLock
        };

        var exception = Assert.Throws<InvalidOperationException>(() => service.Refresh(repository));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain("a git process may be using this repository"));
            Assert.That(File.ReadAllText(lockPath), Is.EqualTo("writer owns this lock"));
            Assert.That(File.ReadAllBytes(indexPath), Is.EqualTo(indexBytes));
            Assert.That(File.GetLastWriteTimeUtc(indexPath), Is.EqualTo(indexTimestamp));
            Assert.That(repository.IndexRefreshReason, Is.EqualTo(RepositoryIndexRefreshReason.DeadIndexLock));
            Assert.That(repository.RefreshError, Does.Contain("a git process may be using this repository"));
        }
    }

    [Test]
    [Platform("Win")]
    public void Refresh_OldLockHeldOpenIsPreserved_Issue482()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var library = new Repository(fixture.Path);
        var lockPath = Path.Combine(library.Info.Path, "index.lock");
        File.WriteAllText(lockPath, "live writer");
        File.SetLastWriteTimeUtc(lockPath, FrozenCacheClock.Now.AddHours(-1).UtcDateTime);
        var indexPath = Path.Combine(library.Info.Path, "index");
        var indexBytes = File.ReadAllBytes(indexPath);
        using var writer = File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var repository = new GitWizardRepository(fixture.Path)
        {
            IndexRefreshReason = RepositoryIndexRefreshReason.DeadIndexLock
        };
        var service = new RepositoryIndexRefreshService(new FrozenCacheClock())
        {
            GitProcessRunning = () => false
        };

        var exception = Assert.Throws<InvalidOperationException>(() => service.Refresh(repository));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception!.Message, Does.Contain("a git process may be using this repository"));
            Assert.That(File.Exists(lockPath), Is.True);
            Assert.That(File.ReadAllBytes(indexPath), Is.EqualTo(indexBytes));
            Assert.That(repository.IndexRefreshReason, Is.EqualTo(RepositoryIndexRefreshReason.DeadIndexLock));
        }
    }

    [TestCase(true, 0, RepositoryIndexRefreshReason.StaleIndex)]
    [TestCase(false, 31, null)]
    public void Detect_UsesEntryStatEvidenceInsteadOfIndexAge_Issue482(
        bool touchFiles, int indexAgeDays, RepositoryIndexRefreshReason? expected)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var library = new Repository(fixture.Path);
        if (touchFiles)
        {
            foreach (var entry in library.Index)
            {
                var path = Path.Combine(fixture.Path, entry.Path);
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddHours(-1));
            }
        }

        var indexPath = Path.Combine(library.Info.Path, "index");
        var timestamp = FrozenCacheClock.Now.AddDays(-indexAgeDays).UtcDateTime;
        File.SetLastWriteTimeUtc(indexPath, timestamp);
        var indexBytes = File.ReadAllBytes(indexPath);
        var clock = new StatusClock();
        var repository = new GitWizardRepository(fixture.Path);

        repository.RefreshPendingChangesStatus(library, false, 0, (target, arguments) =>
        {
            var status = GitCli.RunStatus(target, arguments);
            clock.Advance(TimeSpan.FromSeconds(2));
            return status;
        }, clock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.StatusTimeSeconds, Is.EqualTo(2));
            Assert.That(repository.IndexRefreshReason, Is.EqualTo(expected));
            Assert.That(repository.NumberOfPendingChanges, Is.Zero);
            Assert.That(File.ReadAllBytes(indexPath), Is.EqualTo(indexBytes));
            Assert.That(File.GetLastWriteTimeUtc(indexPath), Is.EqualTo(timestamp));
        }
    }

    [TestCase(0.25, null, 0)]
    [TestCase(1, null, 0)]
    [TestCase(2, RepositoryIndexRefreshReason.StaleIndex, 1)]
    public void Detect_EvidenceProbeRunsOnlyAfterSlowStatus_Issue482(
        double statusSeconds, RepositoryIndexRefreshReason? expected, int expectedProbes)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var library = new Repository(fixture.Path);
        var probes = 0;
        var service = new RepositoryIndexRefreshService(new FrozenCacheClock())
        {
            StaleStatEvidence = _ =>
            {
                probes++;
                return true;
            }
        };

        var reason = service.Detect(library, statusSeconds);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reason, Is.EqualTo(expected));
            Assert.That(probes, Is.EqualTo(expectedProbes), "Fast status must not pay for evidence collection.");
        }
    }

    [TestCase(false, RepositoryIndexRefreshReason.StaleIndex)]
    [TestCase(true, null)]
    public void Detect_DistinguishesUnchangedContentFromRealEdits_Issue482(
        bool editContent, RepositoryIndexRefreshReason? expected)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var library = new Repository(fixture.Path);
        library.Config.Set("core.autocrlf", true);
        var readmePath = Path.Combine(fixture.Path, "README.md");
        var contents = new string('a', 8192) + "\r\nlast line\r\n";
        File.WriteAllText(readmePath, contents);
        Commands.Stage(library, "README.md");
        if (editContent)
        {
            File.WriteAllText(readmePath, contents.Replace("last line", "real edit", StringComparison.Ordinal));
        }
        File.SetLastWriteTimeUtc(readmePath, File.GetLastWriteTimeUtc(readmePath).AddHours(-1));
        File.SetLastWriteTimeUtc(Path.Combine(library.Info.Path, "index"), FrozenCacheClock.Now.AddDays(-31).UtcDateTime);
        var service = new RepositoryIndexRefreshService(new FrozenCacheClock());

        Assert.That(service.Detect(library, 2), Is.EqualTo(expected));
    }

    [TestCase(false, false, 2, RepositoryIndexRefreshReason.StaleIndex)]
    [TestCase(true, false, 2, RepositoryIndexRefreshReason.StaleIndex)]
    [TestCase(true, false, 0.25, null)]
    [TestCase(true, true, 2, RepositoryIndexRefreshReason.StaleIndex)]
    [TestCase(true, true, 0.25, null)]
    public void Status_PersistsMeasuredPhaseAndDetectsStaleIndex_Issues481And482(
        bool routeByTime, bool stickyRouting, double statusSeconds, RepositoryIndexRefreshReason? expected)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var library = new Repository(fixture.Path);
        var readmePath = Path.Combine(fixture.Path, "README.md");
        File.SetLastWriteTimeUtc(readmePath, File.GetLastWriteTimeUtc(readmePath).AddHours(-1));
        var clock = new StatusClock();
        var repository = new GitWizardRepository(fixture.Path)
        {
            StatusTimeSeconds = stickyRouting ? 0.25 : 2,
            UseCliStatus = stickyRouting,
            IndexRefreshReason = statusSeconds > 1 ? null : RepositoryIndexRefreshReason.StaleIndex
        };

        repository.RefreshPendingChangesStatus(library, false, routeByTime ? library.Index.Count + 1 : 0, (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(statusSeconds));
            return new GitCliResult(true, string.Empty);
        }, clock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(repository.StatusTimeSeconds, Is.EqualTo(statusSeconds));
            Assert.That(repository.UseCliStatus, Is.EqualTo(routeByTime));
            Assert.That(repository.IndexRefreshReason, Is.EqualTo(expected));
        }
    }

    [TestCase(601, RepositoryIndexRefreshReason.DeadIndexLock)]
    [TestCase(600, null)]
    [TestCase(60, null)]
    public void Detect_LockAge(int ageSeconds, RepositoryIndexRefreshReason? expected)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var repository = new Repository(fixture.Path);
        var lockPath = Path.Combine(repository.Info.Path, "index.lock");
        File.WriteAllText(lockPath, string.Empty);
        File.SetLastWriteTimeUtc(lockPath, FrozenCacheClock.Now.AddSeconds(-ageSeconds).UtcDateTime);
        var service = new RepositoryIndexRefreshService(new FrozenCacheClock());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(service.Detect(repository, 0), Is.EqualTo(expected));
            Assert.That(File.Exists(lockPath), Is.True, "Detection must never refresh or delete the lock.");
        }
    }

    [TestCase(true, 31, 1.1, RepositoryIndexRefreshReason.StaleIndex)]
    [TestCase(true, 0, 1.1, RepositoryIndexRefreshReason.StaleIndex)]
    [TestCase(false, 31, 1.1, null)]
    [TestCase(true, 31, 1, null)]
    [TestCase(true, 31, 0.1, null)]
    public void Detect_StaleEntryAndStatusDuration(
        bool touchFile, int ageDays, double seconds, RepositoryIndexRefreshReason? expected)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var repository = new Repository(fixture.Path);
        var indexPath = Path.Combine(repository.Info.Path, "index");
        var timestamp = FrozenCacheClock.Now.AddDays(-ageDays).UtcDateTime;
        File.SetLastWriteTimeUtc(indexPath, timestamp);
        if (touchFile)
        {
            var readmePath = Path.Combine(fixture.Path, "README.md");
            File.SetLastWriteTimeUtc(readmePath, File.GetLastWriteTimeUtc(readmePath).AddHours(-1));
        }
        var service = new RepositoryIndexRefreshService(new FrozenCacheClock());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(service.Detect(repository, seconds), Is.EqualTo(expected));
            Assert.That(File.GetLastWriteTimeUtc(indexPath), Is.EqualTo(timestamp));
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void Refresh_ResolvesGitDirectoryAndClearsWarning(bool linkedWorktree, bool separateGitDirectory)
    {
        using var scenario = GitScenarioRepo.Create();
        using var metadata = new TempDirectory();
        if (separateGitDirectory)
        {
            scenario.Git("init", "--separate-git-dir", Path.Combine(metadata.Path, "metadata"));
        }

        scenario.Git("branch", "linked");
        var path = linkedWorktree ? scenario.AddWorktree("linked") : scenario.Path;
        using var library = new Repository(path);
        var lockPath = Path.Combine(library.Info.Path, "index.lock");
        File.WriteAllText(lockPath, string.Empty);
        File.SetLastWriteTimeUtc(lockPath, FrozenCacheClock.Now.AddHours(-1).UtcDateTime);
        var repository = new GitWizardRepository(path);
        repository.RefreshPendingChangesStatus(library, false, timeProvider: new FrozenCacheClock());
        Assert.That(repository.IndexRefreshReason, Is.EqualTo(RepositoryIndexRefreshReason.DeadIndexLock));

        new RepositoryIndexRefreshService(new FrozenCacheClock())
        {
            GitProcessRunning = () => false
        }.Refresh(repository);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(lockPath), Is.False);
            Assert.That(repository.IndexRefreshReason, Is.Null);
            Assert.That(repository.RefreshError, Is.Null);
            Assert.That(repository.CurrentBranch, Is.EqualTo(linkedWorktree ? "linked" : "main"));
        }
    }

    [Test]
    public void Refresh_RechecksLockThatBecameFresh()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var library = new Repository(fixture.Path);
        var lockPath = Path.Combine(library.Info.Path, "index.lock");
        File.WriteAllText(lockPath, "active lock");
        File.SetLastWriteTimeUtc(lockPath, FrozenCacheClock.Now.UtcDateTime);
        var repository = new GitWizardRepository(fixture.Path)
        {
            IndexRefreshReason = RepositoryIndexRefreshReason.DeadIndexLock
        };
        var service = new RepositoryIndexRefreshService(new FrozenCacheClock());

        Assert.Throws<InvalidOperationException>(() => service.Refresh(repository));
        Assert.That(File.ReadAllText(lockPath), Is.EqualTo("active lock"));
    }

    [Test]
    public void Refresh_ModifiedFilesExitOneIsSuccess()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var library = new Repository(fixture.Path);
        var indexPath = Path.Combine(library.Info.Path, "index");
        File.SetLastWriteTimeUtc(indexPath, FrozenCacheClock.Now.AddDays(-31).UtcDateTime);
        File.WriteAllText(Path.Combine(fixture.Path, "README.md"), "modified content");
        var needsUpdate = GitCli.RunQuiet(library, "update-index", "--refresh");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(needsUpdate.Succeeded, Is.False);
            Assert.That(needsUpdate.Output, Does.Contain("needs update"));
        }
        File.SetLastWriteTimeUtc(indexPath, FrozenCacheClock.Now.AddDays(-31).UtcDateTime);
        var repository = new GitWizardRepository(fixture.Path)
        {
            StatusTimeSeconds = 2,
            IndexRefreshReason = RepositoryIndexRefreshReason.StaleIndex
        };
        var service = new RepositoryIndexRefreshService(new FrozenCacheClock());

        service.Refresh(repository);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.GetLastWriteTimeUtc(indexPath), Is.Not.EqualTo(FrozenCacheClock.Now.AddDays(-31).UtcDateTime));
            Assert.That(repository.HasPendingChanges, Is.True);
            Assert.That(repository.NumberOfPendingChanges, Is.EqualTo(1));
            Assert.That(repository.IndexRefreshReason, Is.Null);
            Assert.That(repository.RefreshError, Is.Null);
        }
    }

    sealed class StatusClock : TimeProvider
    {
        long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => FrozenCacheClock.Now;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}

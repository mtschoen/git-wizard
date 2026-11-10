using GitWizard;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests;

public class RepositoryPendingChangesStatusTests
{
    [TestCase("", false, 0)]
    [TestCase("? folder/naïve file.txt\0", true, 1)]
    [TestCase("! ignored\0", false, 0)]
    [TestCase("u UU N... 100644 100644 100644 100644 a b c conflict\0", true, 0)]
    [TestCase("1 .A N... 000000 100644 100644 a b intent-to-add\0", true, 1)]
    [TestCase("2 RM N... 100644 100644 100644 a b R100 new name\0? old name\0", true, 2)]
    [TestCase("2 C. N... 100644 100644 100644 a b C100 copy\0original\0", true, 1)]
    public void Parse_CountsGroupsNotPaths(string output, bool expectedDirty, int expectedCount)
    {
        var status = RepositoryPendingChangesStatus.Parse(output);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.IsDirty, Is.EqualTo(expectedDirty));
            Assert.That(status.Count, Is.EqualTo(expectedCount));
        }
    }

    [TestCase("? sub/\0", new[] { "sub/" })]
    [TestCase("1 M. N... 100644 100644 100644 a b tracked\0", new string[0])]
    [TestCase("? sub/\0? path with spaces\0? sub/\0", new[] { "sub/", "path with spaces" })]
    public void Parse_RetainsOnlyUntrackedRecordPaths(string output, string[] expectedPaths)
    {
        var status = RepositoryPendingChangesStatus.Parse(output);

        Assert.That(status.UntrackedPaths, Is.EquivalentTo(expectedPaths));
    }

    [Test]
    public void Parse_MapsBothColumnsIndependently(
        [Values('.', 'M', 'A', 'D', 'R', 'C', 'T', 'U')] char index,
        [Values('.', 'M', 'A', 'D', 'R', 'T', 'U')] char workingTree)
    {
        var status = RepositoryPendingChangesStatus.Parse(
            $"1 {index}{workingTree} N... 100644 100644 100644 a b folder/naïve\nfile.txt\0");
        var expectedCount = (index is 'M' or 'A' or 'D' or 'R' or 'C' ? 1 : 0) +
            (workingTree is 'M' or 'A' or 'D' or 'R' ? 1 : 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.IsDirty, Is.EqualTo(index != '.' || workingTree != '.'));
            Assert.That(status.Count, Is.EqualTo(expectedCount));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Refresh_CliMatchesLibgit2Groups(bool failCli)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var repository = new Repository(fixture.Path);
        repository.Config.Set("core.autocrlf", false);
        repository.Config.Set("status.renames", true);
        string[] names = ["modify", "delete", "rename", "workmodify", "workdelete", "both"];
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(fixture.Path, name), $"original {name}\n");
        }

        Commands.Stage(repository, names);
        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UnixEpoch);
        repository.Commit("baseline", signature, signature);
        File.WriteAllText(Path.Combine(fixture.Path, "added"), "added\n");
        File.WriteAllText(Path.Combine(fixture.Path, "modify"), "staged modification\n");
        File.WriteAllText(Path.Combine(fixture.Path, "both"), "staged both\n");
        File.Delete(Path.Combine(fixture.Path, "delete"));
        File.Move(Path.Combine(fixture.Path, "rename"), Path.Combine(fixture.Path, "renamed"));
        Commands.Stage(repository, "*");
        File.WriteAllText(Path.Combine(fixture.Path, "workmodify"), "work modification\n");
        File.WriteAllText(Path.Combine(fixture.Path, "both"), "work both\n");
        File.Delete(Path.Combine(fixture.Path, "workdelete"));
        Directory.CreateDirectory(Path.Combine(fixture.Path, "nested"));
        File.WriteAllText(Path.Combine(fixture.Path, "nested", "naïve file.txt"), "untracked\n");
        var expected = new GitWizardRepository(fixture.Path);
        expected.RefreshPendingChangesStatus(repository, false);
        var actual = new GitWizardRepository(fixture.Path);
        var attempted = false;
        string[] expectedArguments =
        [
            "--no-optional-locks", "status", "--porcelain=v2", "-z", "--untracked-files=all",
            "--ignore-submodules=all"
        ];

        actual.RefreshPendingChangesStatus(repository, false, 0, (target, arguments) =>
        {
            attempted = true;
            Assert.That(arguments, Is.EqualTo(expectedArguments));
            var result = failCli
                ? GitCli.RunStatus(target, "--no-optional-locks", "git-wizard-missing-status-command")
                : GitCli.RunStatus(target, arguments);
            Assert.That(result.Succeeded, Is.EqualTo(!failCli));
            return result;
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempted, Is.True);
            Assert.That(actual.HasPendingChanges, Is.EqualTo(expected.HasPendingChanges));
            Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(expected.NumberOfPendingChanges));
            Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(9));
        }
    }

    [Test]
    public void Refresh_CliMatchesLibgit2ForIntentToAddFiles()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var repository = new Repository(fixture.Path);
        File.WriteAllText(Path.Combine(fixture.Path, "empty"), string.Empty);
        File.WriteAllText(Path.Combine(fixture.Path, "nonempty"), "content\n");
        var addResult = GitCli.Run(repository, "add", "-N", "empty", "nonempty");
        Assert.That(addResult.Succeeded, Is.True);

        var expected = new GitWizardRepository(fixture.Path);
        expected.RefreshPendingChangesStatus(repository, false);
        var actual = new GitWizardRepository(fixture.Path);
        var attempted = false;

        actual.RefreshPendingChangesStatus(repository, false, 0, (target, arguments) =>
        {
            attempted = true;
            return GitCli.RunStatus(target, arguments);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attempted, Is.True);
            Assert.That(actual.HasPendingChanges, Is.True);
            Assert.That(actual.HasPendingChanges, Is.EqualTo(expected.HasPendingChanges));
            Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(expected.NumberOfPendingChanges));
            Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(3));
        }
    }

    [TestCase(0.0)]
    [TestCase(0.2)]
    [TestCase(RepositoryPendingChangesStatus.StatusTimeThresholdSeconds)]
    public void Refresh_ThresholdAvoidsSpawningForSmallRepository(double previousStatusSeconds)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var repository = new Repository(fixture.Path);
        var clock = new ControlledDelayClock();
        var actual = new GitWizardRepository(fixture.Path)
        {
            StatusTimeSeconds = previousStatusSeconds
        };

        actual.RefreshPendingChangesStatus(repository, false, repository.Index.Count + 1,
            (_, _) => throw new AssertionException("Small repository must use libgit2"),
            clock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.HasPendingChanges, Is.False);
            Assert.That(actual.StatusTimeSeconds, Is.Zero);
            Assert.That(actual.UseCliStatus, Is.False);
        }
    }

    [TestCase(1.001, false, TestName = "Refresh_SlowPreviousStatusRoutesSmallRepositoryToCli_Issue481")]
    [TestCase(0.15, true, TestName = "Refresh_FastCliStatusStaysOnCliAcrossRefreshes_Issue481")]
    public void Refresh_TimeRoutedSmallRepositoryUsesCliAndRecordsElapsedSeconds(
        double previousStatusSeconds, bool previousUseCliStatus)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var repository = new Repository(fixture.Path);
        var clock = new ControlledDelayClock();
        var actual = new GitWizardRepository(fixture.Path)
        {
            StatusTimeSeconds = previousStatusSeconds,
            UseCliStatus = previousUseCliStatus
        };

        actual.RefreshPendingChangesStatus(repository, false, repository.Index.Count + 1,
            (_, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(250));
                return new GitCliResult(true, "? synthetic change\0");
            },
            clock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.HasPendingChanges, Is.True);
            Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(1));
            Assert.That(actual.StatusTimeSeconds, Is.EqualTo(0.25));
            Assert.That(actual.UseCliStatus, Is.True);
        }
    }

    [TestCase(RepositoryPendingChangesStatus.StatusTimeThresholdSeconds, false)]
    [TestCase(1.001, true)]
    public void Refresh_Libgit2ElapsedDurationControlsNextRefreshRouting(
        double initialLibgit2Seconds, bool expectedCliOnNextRefresh)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var repository = new Repository(fixture.Path);
        var clock = new RecordedTimestampClock(0.0, initialLibgit2Seconds, 10.0, 10.25);
        var actual = new GitWizardRepository(fixture.Path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.StatusTimeSeconds, Is.Zero);
            Assert.That(actual.UseCliStatus, Is.False);
        }

        actual.RefreshPendingChangesStatus(repository, false, repository.Index.Count + 1,
            (_, _) => throw new AssertionException("Initial unseeded refresh must use libgit2"),
            clock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.HasPendingChanges, Is.False);
            Assert.That(actual.NumberOfPendingChanges, Is.Zero);
            Assert.That(actual.StatusTimeSeconds, Is.EqualTo(initialLibgit2Seconds));
            Assert.That(actual.UseCliStatus, Is.False);
        }

        var cliInvoked = false;
        actual.RefreshPendingChangesStatus(repository, false, repository.Index.Count + 1,
            (_, _) =>
            {
                cliInvoked = true;
                return new GitCliResult(true, "? synthetic change\0");
            },
            clock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cliInvoked, Is.EqualTo(expectedCliOnNextRefresh));
            Assert.That(actual.HasPendingChanges, Is.EqualTo(expectedCliOnNextRefresh));
            Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(expectedCliOnNextRefresh ? 1 : 0));
            Assert.That(actual.StatusTimeSeconds, Is.EqualTo(0.25));
            Assert.That(actual.UseCliStatus, Is.EqualTo(expectedCliOnNextRefresh));
        }
    }

    [Test]
    public void Refresh_AtThresholdUsesCliStatusInsteadOfLibgit2()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var repository = new Repository(fixture.Path);
        var clock = new ControlledDelayClock();
        var actual = new GitWizardRepository(fixture.Path);

        actual.RefreshPendingChangesStatus(repository, false, repository.Index.Count,
            (_, _) =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(125));
                return new GitCliResult(true, "? synthetic change\0");
            },
            clock);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.HasPendingChanges, Is.True);
            Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(1));
            Assert.That(actual.StatusTimeSeconds, Is.EqualTo(0.125));
            Assert.That(actual.UseCliStatus, Is.False);
        }
    }

    [Test]
    [NonParallelizable]
    public void Refresh_TwoArgumentCallAtDefaultThreshold_Issue476UsesCliStatus()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var repository = new Repository(fixture.Path);
        var readme = (Blob)repository.Head.Tip["README.md"].Target;
        for (var position = 1; position < RepositoryPendingChangesStatus.TrackedFileThreshold; position++)
        {
            repository.Index.Add(readme, $"tracked-{position:D5}", Mode.NonExecutableFile);
        }

        repository.Index.Write();
        Assert.That(repository.Index.Count, Is.EqualTo(RepositoryPendingChangesStatus.TrackedFileThreshold));
        var tracePath = Path.Combine(repository.Info.Path, "status-trace.json");
        var previousTrace = Environment.GetEnvironmentVariable("GIT_TRACE2_EVENT");
        var actual = new GitWizardRepository(fixture.Path);
        try
        {
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", tracePath);

            actual.RefreshPendingChangesStatus(repository, false);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.RefreshError, Is.Null);
                Assert.That(actual.HasPendingChanges, Is.True);
                Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(139998));
                Assert.That(File.Exists(tracePath), Is.True, "The default call must start git status.");
            }

            Assert.That(File.ReadAllText(tracePath), Does.Contain("--porcelain=v2"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", previousTrace);
        }
    }

    [TestCase(1.5, false, TestName = "GenerateReportAsync_SlowCachedStatusRoutesSmallRepositoryToCli_Issue481")]
    [TestCase(0.15, true, TestName = "GenerateReportAsync_FastCachedCliStatusStaysOnCli_Issue481")]
    [NonParallelizable]
    public async Task GenerateReportAsync_CarriesCachedStatusRoutingForSmallRepository(
        double cachedStatusSeconds, bool cachedUseCliStatus)
    {
        GitWizardLog.SilentMode = true;
        using var sandbox = new LocalFilesSandbox();
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var cachedReport = new GitWizardReport();
        cachedReport.Repositories[fixture.Path] = new GitWizardRepository(fixture.Path)
        {
            StatusTimeSeconds = cachedStatusSeconds,
            UseCliStatus = cachedUseCliStatus
        };
        await cachedReport.SaveAsync(GitWizardReport.GetCachedReportPath());

        var tracePath = Path.Combine(sandbox.Path, "status-trace.json");
        var previousTrace = Environment.GetEnvironmentVariable("GIT_TRACE2_EVENT");
        try
        {
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", tracePath);

            await using var report = await GitWizardReport.GenerateReportAsync(
                GitWizardConfiguration.CreateDefaultConfiguration(), [fixture.Path]);
            var refreshed = report.Repositories[fixture.Path];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(refreshed.RefreshError, Is.Null);
                Assert.That(refreshed.UseCliStatus, Is.True);
                Assert.That(File.Exists(tracePath), Is.True, "Cached slow status routing must start git status.");
            }

            Assert.That(await File.ReadAllTextAsync(tracePath), Does.Contain("--porcelain=v2"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_TRACE2_EVENT", previousTrace);
        }
    }

    [Test]
    public void GitCli_PreservesNulDelimitedOutput()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var repository = new Repository(fixture.Path);

        var result = GitCli.RunStatus(repository, "--no-optional-locks", "ls-files", "-z");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Output, Is.EqualTo("README.md\0"));
        }
    }

    [TestCase(false, "")]
    [TestCase(true, "invalid status\0")]
    [TestCase(true, "1 .A N... 000000 100644\0")]
    [NonParallelizable]
    public void Refresh_FallbackWarnsOnceAcrossRefreshes(bool succeeded, string output)
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        using var repository = new Repository(fixture.Path);
        File.WriteAllText(Path.Combine(fixture.Path, "untracked"), "untracked");
        var actual = new GitWizardRepository(fixture.Path)
        {
            UseCliStatus = true
        };
        var previousSink = GitWizardLog.TypedLogMethod;
        var warnings = new List<string>();
        var repositoryPath = fixture.Path;
        GitWizardLog.TypedLogMethod = (message, type) =>
        {
            if (type == GitWizardLog.LogType.Warning && message?.Contains(repositoryPath, StringComparison.Ordinal) == true)
            {
                warnings.Add(message);
            }
        };
        try
        {
            for (var refresh = 0; refresh < 2; refresh++)
            {
                actual.RefreshPendingChangesStatus(repository, false, 0, (_, _) => new GitCliResult(succeeded, output));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual.HasPendingChanges, Is.True);
                Assert.That(actual.NumberOfPendingChanges, Is.EqualTo(1));
                Assert.That(actual.UseCliStatus, Is.False);
                Assert.That(warnings, Has.Count.EqualTo(1));
                Assert.That(warnings[0], Does.Contain("falling back to libgit2"));
            }
        }
        finally
        {
            GitWizardLog.TypedLogMethod = previousSink;
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GitCli_CancellationBoundsInheritedPipes(bool parentExited)
    {
        using var cancellation = new CancellationTokenSource();
        var processExit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (parentExited)
        {
            processExit.SetResult();
        }
        else
        {
            output.SetResult("output closed before inherited stderr");
        }

        var reading = GitCli.ReadProcessOutputAsync(processExit.Task, output.Task, cancellation.Token);
        cancellation.Cancel();
        processExit.TrySetResult();
        output.TrySetResult("inherited stdout finally closed");

        Assert.CatchAsync<OperationCanceledException>(async () => await reading.ConfigureAwait(false));
    }

    sealed class RecordedTimestampClock(params double[] timestampsInSeconds) : TimeProvider
    {
        int _index;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() =>
            TimeSpan.FromSeconds(timestampsInSeconds[_index++]).Ticks;
    }

    [TestCase("1 .M", "Invalid porcelain v2 status columns", TestName = "Parse_OrdinaryRecordWithTooFewColumns_IsRejected")]
    [TestCase("2 R. N... 100644 100644 100644 a b R100 new.txt\0", "Missing porcelain v2 rename source", TestName = "Parse_RenameWithoutItsSourceRecord_IsRejected")]
    public void Parse_MalformedRecord_IsRejectedWithTheReason(string output, string expectedMessage)
    {
        var failure = Assert.Throws<FormatException>(() => RepositoryPendingChangesStatus.Parse(output));

        Assert.That(failure.Message, Is.EqualTo(expectedMessage));
    }
}

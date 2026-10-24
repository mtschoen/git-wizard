using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Watch;

// Watch tests are split by concern because the combined fixture exceeds 500 lines.
// Covers RepositoryWatchService (membership admission, creation, and deletion tracking).
[TestFixture]
public class RepositoryWatchServiceMembershipTests
{
    static async Task<List<RepositoryChangeEvent>> DrainAsync(RepositoryWatchService svc)
    {
        var events = new List<RepositoryChangeEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var ev in svc.RunAsync(cts.Token))
            events.Add(ev);
        return events;
    }

    [Test]
    public async Task DotGitCreatedUnderTempDirectory_ExcludedByDefault()
    {
        var repositoryPath = Path.Combine(TempDirectory.SystemTempPath, "gw-membership", "repo");
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
                new[] { new VolumeChangeBatch(WatchPath.Parse(repositoryPath).Volume!, new[]
                {
                    new VolumeChangeEntry(Path.Combine(repositoryPath, ".git"), VolumeEntryKind.Created)
                    }) }),
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { Path.GetPathRoot(repositoryPath)! },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DotGitCreatedUnderTempDirectory_EmittedWhenIncludeTempDirectories()
    {
        var repositoryPath = Path.Combine(TempDirectory.SystemTempPath, "gw-membership", "repo");
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
                new[] { new VolumeChangeBatch(WatchPath.Parse(repositoryPath).Volume!, new[]
                {
                    new VolumeChangeEntry(Path.Combine(repositoryPath, ".git"), VolumeEntryKind.Created)
                    }) }),
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { Path.GetPathRoot(repositoryPath)! },
            debounce: TimeSpan.FromMilliseconds(10),
            includeTempDirectories: true);

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo(new[]
        {
                new RepositoryChangeEvent(repositoryPath, RepositoryChangeKind.Created),
            }));
    }

    [Test]
    public async Task TrackedTempDirectory_ExcludedByDefault_OnModificationAndGitRecreation()
    {
        var trackedTempRepo = Path.Combine(TempDirectory.SystemTempPath, "gw-membership", "repo");
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
                new[]
                {
                    new VolumeChangeBatch(WatchPath.Parse(trackedTempRepo).Volume!, new[]
                        {
                            new VolumeChangeEntry(Path.Combine(trackedTempRepo, "file.txt"), VolumeEntryKind.Modified),
                            new VolumeChangeEntry(Path.Combine(trackedTempRepo, ".git"), VolumeEntryKind.Created),
                        })
                }),
            trackedRoots: new[] { trackedTempRepo },
            searchRoots: new[] { Path.GetPathRoot(trackedTempRepo)! },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task IgnoredPath_ExcludedEvenWhenIncludeTempDirectoriesTrue()
    {
        var ignoredTempSubtree = Path.Combine(TempDirectory.SystemTempPath, "gw-membership", "ignored");
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(
                new[]
                {
                    new VolumeChangeBatch(WatchPath.Parse(ignoredTempSubtree).Volume!, new[]
                        {
                            new VolumeChangeEntry(Path.Combine(ignoredTempSubtree, "repo", ".git"), VolumeEntryKind.Created),
                        })
                }),
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { Path.GetPathRoot(ignoredTempSubtree)! },
            debounce: TimeSpan.FromMilliseconds(10),
            includeTempDirectories: true,
            ignoredPaths: new[] { ignoredTempSubtree });

        var events = await DrainAsync(svc);

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task LiveDiscoveredRepository_SubsequentEdit_EmitsChanged()
    {
        var batches = new[]
        {
                new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\newrepo\.git", VolumeEntryKind.Created)
                }),
                new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\newrepo\a.txt", VolumeEntryKind.Modified)
                }),
            };
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(batches),
            trackedRoots: Array.Empty<string>(),
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\newrepo", RepositoryChangeKind.Created)));
        Assert.That(events, Does.Contain(
            new RepositoryChangeEvent(@"C:\repo\newrepo", RepositoryChangeKind.Changed)));
    }

    [Test]
    public async Task TrackedRootDeletedThenRecreated_EmitsDeletedThenCreatedAgain()
    {
        var batches = new[]
        {
                new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\gone\.git", VolumeEntryKind.Deleted)
                }),
                new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\gone\.git", VolumeEntryKind.Created)
                }),
                new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\gone\a.txt", VolumeEntryKind.Modified)
                }),
            };
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(batches),
            trackedRoots: new[] { @"C:\repo\gone" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo(new[]
        {
                new RepositoryChangeEvent(@"C:\repo\gone", RepositoryChangeKind.Deleted),
                new RepositoryChangeEvent(@"C:\repo\gone", RepositoryChangeKind.Created),
                new RepositoryChangeEvent(@"C:\repo\gone", RepositoryChangeKind.Changed),
            }));
    }

    [Test]
    public async Task TrackedRootDeletedThenRecreatedInSameBatch_EmitsDeletedThenCreatedAndRemainsTracked()
    {
        var batches = new[]
        {
                new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Deleted),
                    new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Created),
                }),
                new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\lifecycle\file.txt", VolumeEntryKind.Modified)
                }),
            };
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(batches),
            trackedRoots: new[] { @"C:\repo\lifecycle" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo(new[]
        {
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Deleted),
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Created),
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Changed),
            }));
    }

    [Test]
    public async Task TrackedRootDeletedThenRecreatedAndModifiedInSameBatch_EmitsInOrder()
    {
        var batch = new VolumeChangeBatch("C", new[]
        {
                new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Deleted),
                new VolumeChangeEntry(@"C:\repo\lifecycle\.git", VolumeEntryKind.Created),
                new VolumeChangeEntry(@"C:\repo\lifecycle\file.txt", VolumeEntryKind.Modified),
            });
        var svc = new RepositoryWatchService(
            new ScriptedVolumeChangeSource(new[] { batch }),
            trackedRoots: new[] { @"C:\repo\lifecycle" },
            searchRoots: new[] { @"C:\repo" },
            debounce: TimeSpan.FromMilliseconds(10));

        var events = await DrainAsync(svc);

        Assert.That(events, Is.EqualTo(new[]
        {
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Deleted),
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Created),
                new RepositoryChangeEvent(@"C:\repo\lifecycle", RepositoryChangeKind.Changed),
            }));
    }

    [Test]
    public async Task MovedInAfterCreatedAndDeletedInSameBatch_EmitsCreatedDeletedCreated()
    {
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"watch-recreate-{Guid.NewGuid():N}");
        var repoPath = Path.Combine(searchRoot, "repo");
        var gitPath = Path.Combine(repoPath, ".git");
        Directory.CreateDirectory(gitPath);
        try
        {
            var volume = WatchPath.Parse(repoPath).Volume!;
            var batch = new VolumeChangeBatch(volume, new[]
            {
                    new VolumeChangeEntry(gitPath, VolumeEntryKind.Created),
                    new VolumeChangeEntry(repoPath, VolumeEntryKind.Deleted),
                    new VolumeChangeEntry(repoPath, VolumeEntryKind.MovedIn),
                });
            var service = new RepositoryWatchService(
                new ScriptedVolumeChangeSource(new[] { batch }),
                trackedRoots: Array.Empty<string>(),
                searchRoots: new[] { searchRoot },
                debounce: TimeSpan.FromMilliseconds(10));

            var events = await DrainAsync(service);

            Assert.That(events, Is.EqualTo(new[]
            {
                    new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
                    new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Deleted),
                    new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
                }));
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }

    [Test]
    public async Task MovedInAfterCreatedAndDeleted_EmitsChangedOnSubsequentBatch()
    {
        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"watch-recreate-changed-{Guid.NewGuid():N}");
        var repoPath = Path.Combine(searchRoot, "repo");
        var gitPath = Path.Combine(repoPath, ".git");
        Directory.CreateDirectory(gitPath);
        try
        {
            var volume = WatchPath.Parse(repoPath).Volume!;
            var batches = new[]
            {
                    new VolumeChangeBatch(volume, new[]
                    {
                        new VolumeChangeEntry(gitPath, VolumeEntryKind.Created),
                        new VolumeChangeEntry(repoPath, VolumeEntryKind.Deleted),
                        new VolumeChangeEntry(repoPath, VolumeEntryKind.MovedIn),
                    }),
                    new VolumeChangeBatch(volume, new[]
                    {
                        new VolumeChangeEntry(Path.Combine(repoPath, "file.txt"), VolumeEntryKind.Modified),
                    }),
                };
            var service = new RepositoryWatchService(
                new DelayedVolumeChangeSource(batches, TimeSpan.FromMilliseconds(60)),
                trackedRoots: Array.Empty<string>(),
                searchRoots: new[] { searchRoot },
                debounce: TimeSpan.FromMilliseconds(10));

            var events = await DrainAsync(service);

            Assert.That(events, Is.EqualTo(new[]
            {
                    new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
                    new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Deleted),
                    new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Created),
                    new RepositoryChangeEvent(repoPath, RepositoryChangeKind.Changed),
                }));
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }

    [Test]
    public async Task WindowsCaseRenameAfterCreatedInSameBatch_EmitsCreatedDeletedCreated()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Ignore("Case-only Windows rename test is Windows-only.");

        var searchRoot = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"watch-case-rename-create-{Guid.NewGuid():N}");
        var lowerPath = Path.Combine(searchRoot, "repo");
        var upperPath = Path.Combine(searchRoot, "Repo");
        var gitPath = Path.Combine(upperPath, ".git");
        Directory.CreateDirectory(gitPath);
        try
        {
            var volume = WatchPath.Parse(upperPath).Volume!;
            var batch = new VolumeChangeBatch(volume, new[]
            {
                    new VolumeChangeEntry(Path.Combine(lowerPath, ".git"), VolumeEntryKind.Created),
                    new VolumeChangeEntry(lowerPath, VolumeEntryKind.Deleted),
                    new VolumeChangeEntry(upperPath, VolumeEntryKind.MovedIn),
                });
            var service = new RepositoryWatchService(
                new ScriptedVolumeChangeSource(new[] { batch }),
                trackedRoots: Array.Empty<string>(),
                searchRoots: new[] { searchRoot },
                debounce: TimeSpan.FromMilliseconds(10));

            var events = await DrainAsync(service);

            Assert.That(events, Is.EqualTo(new[]
            {
                    new RepositoryChangeEvent(lowerPath, RepositoryChangeKind.Created),
                    new RepositoryChangeEvent(lowerPath, RepositoryChangeKind.Deleted),
                    new RepositoryChangeEvent(upperPath, RepositoryChangeKind.Created),
                }));
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }
}

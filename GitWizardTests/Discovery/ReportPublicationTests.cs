using System.Text.Json;
using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;
using LibGit2Sharp;
using MFTLib.Index;
using MFTLibTestExtensions;

namespace GitWizardTests.Discovery;

[TestFixture]
public class ReportPublicationTests
{
    string? _tempHome;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _tempHome = TestUtilities.RedirectLocalFilesToTemp();
        TestUtilities.ResetStaticCaches();
    }

    [TearDown]
    public void TearDown()
    {
        TestUtilities.ResetStaticCaches();
        TestUtilities.ClearLocalFilesRedirect(_tempHome);
    }

    static DriveStatus CreateDriveStatus(char letter, DriveState state, string? failureMessage = null) =>
        SyntheticNotifications.CreateDriveStatus(letter, state, BlockSource.ProducedByScan, 10, false, DateTime.UtcNow) with { FailureKind = DriveFailureKind.None, WatchSupported = state == DriveState.Ready, MftProducerFailureMessage = failureMessage };

    static RepositoryDiscoveryCoordinator CreateCoordinator(
        FileIndex index,
        DiscoveryOutcome outcome,
        string dRoot)
    {
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = root =>
            {
                var drive = string.Equals(root, dRoot, StringComparison.OrdinalIgnoreCase) ? "D" : "C";
                return new RootClassification(root, RootFileSystem.Ntfs, drive, null);
            },
            CrawlRootAsync = (_, _, _) => throw new AssertionException("Unexpected crawler call"),
            OpenIndexAsync = (_, _, _, _) =>
            {
                var cStatus = outcome == DiscoveryOutcome.Unusable
                    ? CreateDriveStatus('C', DriveState.Offline, "Drive C offline")
                    : CreateDriveStatus('C', DriveState.Ready);
                var dStatus = outcome == DiscoveryOutcome.CompleteEmpty
                    ? CreateDriveStatus('D', DriveState.Ready)
                    : CreateDriveStatus('D', DriveState.Failed, "Drive D failure");

                var session = new MftIndexSession(
                    index,
                    null,
                    ["C", "D"],
                    [],
                    outcome == DiscoveryOutcome.CompleteEmpty
                        ? []
                        : new Dictionary<string, string> { ["D"] = "Drive D failure" })
                {
                    DriveStatusesOverride = () =>
                    [
                        new MftDriveStatus("C", cStatus, outcome == DiscoveryOutcome.Unusable ? "Drive C offline" : null),
                        new MftDriveStatus("D", dStatus, outcome == DiscoveryOutcome.CompleteEmpty ? null : "Drive D failure")
                    ]
                };
                return Task.FromResult(session);
            },
            ReadListAsync = _ => Task.FromResult<string[]?>([]),
            WriteListAsync = (_, _) => Task.CompletedTask,
            CandidateExists = (_, _) => true,
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };
        return new RepositoryDiscoveryCoordinator(dependencies);
    }

    static (string CachedPath, byte[] PriorBytes) SeedCachedReport()
    {
        var cachedPath = GitWizardReport.GetCachedReportPath();
        var dir = Path.GetDirectoryName(cachedPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var report = new GitWizardReport();
        report.Repositories[@"C:\repo1"] = new GitWizardRepository(@"C:\repo1");
        report.Repositories[@"C:\repo2"] = new GitWizardRepository(@"C:\repo2");
        report.Save(cachedPath);

        return (cachedPath, File.ReadAllBytes(cachedPath));
    }

    [TestCase(DiscoveryOutcome.Partial, false, false, false, false, false)]
    [TestCase(DiscoveryOutcome.Partial, true, false, false, false, false)]
    [TestCase(DiscoveryOutcome.Partial, true, true, false, false, false)]
    [TestCase(DiscoveryOutcome.Partial, true, false, true, false, false)]
    [TestCase(DiscoveryOutcome.Partial, true, false, false, true, false)]
    [TestCase(DiscoveryOutcome.Partial, true, false, false, false, true)]
    [TestCase(DiscoveryOutcome.Unusable, false, false, false, false, false)]
    [TestCase(DiscoveryOutcome.Unusable, false, false, false, false, true)]
    public async Task Refusal_IncompleteDiscovery_PreservesCachedReportAndCustomSavePath_Exits3(
        DiscoveryOutcome outcome,
        bool nonzeroCandidates,
        bool summary,
        bool minified,
        bool silent,
        bool seedSavePath)
    {
        using var files = new PackedIndexFixture();
        if (nonzeroCandidates)
            files.AddMarker("healthy-repo");

        var dRoot = Path.Combine(files.DirectoryPath, "d-root");
        Directory.CreateDirectory(dRoot);

        await using var index = await files.OpenIndexAsync('C');
        var coordinator = CreateCoordinator(index, outcome, dRoot);

        var (cachedReportPath, priorBytes) = SeedCachedReport();
        var customSavePath = Path.Combine(files.DirectoryPath, "custom-save.json");
        byte[]? priorSaveBytes = null;
        if (seedSavePath)
        {
            priorSaveBytes = """{"seeded": true}"""u8.ToArray();
            await File.WriteAllBytesAsync(customSavePath, priorSaveBytes);
        }

        var args = new List<string> { "--rebuild-report", "--save-path", customSavePath };
        if (summary) args.Add("--summary");
        if (minified) args.Add("--minified");
        if (silent) args.Add("--silent");

        var runConfig = new Program.RunConfiguration(CliParser.Parse(args.ToArray()));
        var gitWizardConfig = new GitWizardConfiguration
        {
            SearchPaths = { files.TreePath, dRoot }
        };

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunDefaultReportCoreAsync(
            runConfig,
            gitWizardConfig,
            coordinator,
            stdout,
            stderr);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(3));
            Assert.That(File.ReadAllBytes(cachedReportPath), Is.EqualTo(priorBytes));
            if (seedSavePath)
                Assert.That(File.ReadAllBytes(customSavePath), Is.EqualTo(priorSaveBytes));
            else
                Assert.That(File.Exists(customSavePath), Is.False);
            Assert.That(stdout.ToString(), Does.Not.Contain("\"Repositories\""));
            Assert.That(stderr.ToString(), Does.Contain("D"));
        });
    }

    [Test]
    public async Task CompleteEmpty_ReplacesCachedAndCustomReportsWithSchema21EmptyInventory_Exits0()
    {
        using var files = new PackedIndexFixture();
        var dRoot = Path.Combine(files.DirectoryPath, "d-root");
        Directory.CreateDirectory(dRoot);

        await using var index = await files.OpenIndexAsync('C');
        var coordinator = CreateCoordinator(index, DiscoveryOutcome.CompleteEmpty, dRoot);

        var (cachedReportPath, _) = SeedCachedReport();
        var customSavePath = Path.Combine(files.DirectoryPath, "empty-save.json");

        var runConfig = new Program.RunConfiguration(CliParser.Parse(["--rebuild-report", "--save-path", customSavePath]));
        var gitWizardConfig = new GitWizardConfiguration
        {
            SearchPaths = { files.TreePath, dRoot }
        };

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await Program.RunDefaultReportCoreAsync(
            runConfig,
            gitWizardConfig,
            coordinator,
            stdout,
            stderr);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(File.Exists(cachedReportPath), Is.True);
            Assert.That(File.Exists(customSavePath), Is.True);
        });

        var cachedText = await File.ReadAllTextAsync(cachedReportPath);
        var customText = await File.ReadAllTextAsync(customSavePath);

        using var cachedDoc = JsonDocument.Parse(cachedText);
        using var customDoc = JsonDocument.Parse(customText);

        Assert.Multiple(() =>
        {
            Assert.That(cachedDoc.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("2.1"));
            Assert.That(cachedDoc.RootElement.GetProperty("Repositories").EnumerateObject().Count(), Is.EqualTo(0));
            Assert.That(customDoc.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("2.1"));
            Assert.That(customDoc.RootElement.GetProperty("Repositories").EnumerateObject().Count(), Is.EqualTo(0));
        });
    }

    [Test]
    public async Task ExplicitPaths_BypassesThrowingCoordinator_SavesNormally_PreservesAttentionExit1()
    {
        var dependencies = new RepositoryDiscoveryDependencies
        {
            IsWindows = true,
            IsLinux = false,
            ClassifyRoot = _ => throw new AssertionException("Coordinator should not be invoked for explicit paths."),
            CrawlRootAsync = (_, _, _) => throw new AssertionException("Coordinator should not be invoked for explicit paths."),
            OpenIndexAsync = (_, _, _, _) => throw new AssertionException("Coordinator should not be invoked for explicit paths."),
            ReadListAsync = _ => throw new AssertionException("Coordinator should not be invoked for explicit paths."),
            WriteListAsync = (_, _) => throw new AssertionException("Coordinator should not be invoked for explicit paths."),
            CandidateExists = (_, _) => throw new AssertionException("Coordinator should not be invoked for explicit paths."),
            CreateLinuxSource = () => throw new PlatformNotSupportedException()
        };
        var throwingCoordinator = new RepositoryDiscoveryCoordinator(dependencies);

        var tempDir = Path.Combine(Path.GetTempPath(), "gw-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            Repository.Init(tempDir);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "uncommitted.txt"), "pending changes");

            var customSavePath = Path.Combine(tempDir, "explicit-report.json");
            var runConfig = new Program.RunConfiguration(CliParser.Parse(["--paths", tempDir, "--save-path", customSavePath]));
            var gitWizardConfig = new GitWizardConfiguration();

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = await Program.RunDefaultReportCoreAsync(
                runConfig,
                gitWizardConfig,
                throwingCoordinator,
                stdout,
                stderr);

            Assert.Multiple(() =>
            {
                Assert.That(exitCode, Is.EqualTo(1));
                Assert.That(File.Exists(customSavePath), Is.True);
            });

            var savedText = await File.ReadAllTextAsync(customSavePath);
            using var doc = JsonDocument.Parse(savedText);
            Assert.That(doc.RootElement.GetProperty("Repositories").EnumerateObject().Count(), Is.EqualTo(1));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Test]
    public void InMemoryReport_RejectionGuards_ThrowWhenIncomplete()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "gw-guard-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var incompleteReport = new GitWizardReport
            {
                DiscoveryInventory = new DiscoveryInventory
                {
                    Outcome = DiscoveryOutcome.Partial,
                    Route = DiscoveryRoute.Mixed,
                    Paths = [],
                    RequestedNtfsDrives = [],
                    Observations = [],
                    DriveStatuses = [],
                    Errors = new Dictionary<string, string> { ["D"] = "error" },
                    WatchExcludedRoots = []
                }
            };

            Assert.Multiple(() =>
            {
                Assert.Throws<InvalidOperationException>(() => incompleteReport.Save(tempFile));
                Assert.ThrowsAsync<InvalidOperationException>(async () => await incompleteReport.SaveAsync(tempFile));
                Assert.Throws<InvalidOperationException>(() => incompleteReport.SaveAtomic(tempFile));
            });

            var completeReport = new GitWizardReport
            {
                DiscoveryInventory = new DiscoveryInventory
                {
                    Outcome = DiscoveryOutcome.CompleteEmpty,
                    Route = DiscoveryRoute.Indexed,
                    Paths = [],
                    RequestedNtfsDrives = [],
                    Observations = [],
                    DriveStatuses = [],
                    Errors = new Dictionary<string, string>(),
                    WatchExcludedRoots = []
                }
            };

            Assert.DoesNotThrow(() => completeReport.Save(tempFile));
            Assert.DoesNotThrowAsync(async () => await completeReport.SaveAsync(tempFile));
            Assert.DoesNotThrow(() => completeReport.SaveAtomic(tempFile));

            var nullInventoryReport = new GitWizardReport();
            Assert.DoesNotThrow(() => nullInventoryReport.Save(tempFile));
            Assert.DoesNotThrowAsync(async () => await nullInventoryReport.SaveAsync(tempFile));
            Assert.DoesNotThrow(() => nullInventoryReport.SaveAtomic(tempFile));
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch { /* best effort */ }
        }
    }
}

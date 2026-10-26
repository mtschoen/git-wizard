using System.Text.Json;
using GitWizard;
using GitWizard.CLI;
using GitWizardTests.TestSupport;
using LibGit2Sharp;

namespace GitWizardTests.Discovery;

[TestFixture]
public class ReportPublicationTests
{
    LocalFilesSandbox? _sandbox;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _sandbox = new LocalFilesSandbox();
    }

    [TearDown]
    public void TearDown()
    {
        _sandbox?.Dispose();
        _sandbox = null;
    }

    static RepositoryDiscoveryCoordinator CreateCoordinator(
        PackedIndexFixture files,
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
            OpenIndexAsync = (_, _, _, _) => StatusSessions.OpenAsync(files,
                ('C', outcome == DiscoveryOutcome.Unusable ? SyntheticDriveBehavior.Offline : SyntheticDriveBehavior.Ready),
                ('D', outcome == DiscoveryOutcome.CompleteEmpty ? SyntheticDriveBehavior.Ready : SyntheticDriveBehavior.ProducerFails)),
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
        {
            Directory.CreateDirectory(dir);
        }

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
        bool json,
        bool minified,
        bool silent,
        bool seedSavePath)
    {
        using var files = new PackedIndexFixture();
        if (nonzeroCandidates)
        {
            files.AddMarker("healthy-repo");
        }

        var dRoot = Path.Combine(files.DirectoryPath, "d-root");
        Directory.CreateDirectory(dRoot);

        var coordinator = CreateCoordinator(files, outcome, dRoot);

        var (cachedReportPath, priorBytes) = SeedCachedReport();
        var customSavePath = Path.Combine(files.DirectoryPath, "custom-save.json");
        byte[]? priorSaveBytes = null;
        if (seedSavePath)
        {
            priorSaveBytes = [.. """{"seeded": true}"""u8];
            await File.WriteAllBytesAsync(customSavePath, priorSaveBytes);
        }

        var args = new List<string> { "--rebuild-report", "--save-path", customSavePath };
        if (json || minified)
        {
            args.Add("--json");
        }

        if (minified)
        {
            args.Add("--minified");
        }

        if (silent)
        {
            args.Add("--silent");
        }

        var runConfig = CliParser.Parse([.. args]);
        Assert.That(runConfig.HasParseError, Is.False);
        var gitWizardConfig = new GitWizardConfiguration
        {
            SearchPaths = { files.TreePath, dRoot }
        };

        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();

        var exitCode = await Program.RunDefaultReportCoreAsync(
            runConfig,
            gitWizardConfig,
            coordinator,
            stdout,
            stderr);

        var output = stdout.ToString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(3));
            Assert.That(await File.ReadAllBytesAsync(cachedReportPath), Is.EqualTo(priorBytes));
            if (seedSavePath)
            {
                Assert.That(await File.ReadAllBytesAsync(customSavePath), Is.EqualTo(priorSaveBytes));
            }
            else
            {
                Assert.That(File.Exists(customSavePath), Is.False);
            }

            if (silent)
            {
                Assert.That(output, Is.Empty);
                Assert.That(stderr.ToString(), Is.Empty);
            }
            else if (json || minified)
            {
                using var document = JsonDocument.Parse(output);
                Assert.That(document.RootElement.TryGetProperty("Repositories", out _), Is.False);
                var warnings = document.RootElement.GetProperty("run").GetProperty("warnings");
                Assert.That(warnings.EnumerateArray().Any(w => w.GetString()?.Contains('D') == true), Is.True);
                Assert.That(stderr.ToString(), Does.Not.Contain("Discovery error"));
            }
            else
            {
                Assert.That(output, Does.Contain($"Run: created={(nonzeroCandidates ? 1 : 0)} refreshed={(nonzeroCandidates ? 1 : 0)} skipped=0").And.Contain("D"));
                Assert.That(output, Does.Not.Contain("\"Repositories\""));
                Assert.That(stderr.ToString(), Does.Not.Contain("Discovery error"));
            }
        }
    }

    [Test]
    public async Task CompleteEmpty_ReplacesCachedAndCustomReportsWithSchema21EmptyInventory_Exits0()
    {
        using var files = new PackedIndexFixture();
        var dRoot = Path.Combine(files.DirectoryPath, "d-root");
        Directory.CreateDirectory(dRoot);

        var coordinator = CreateCoordinator(files, DiscoveryOutcome.CompleteEmpty, dRoot);

        var (cachedReportPath, _) = SeedCachedReport();
        var customSavePath = Path.Combine(files.DirectoryPath, "empty-save.json");

        var runConfig = CliParser.Parse(["--rebuild-report", "--save-path", customSavePath]);
        var gitWizardConfig = new GitWizardConfiguration
        {
            SearchPaths = { files.TreePath, dRoot }
        };

        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();

        var exitCode = await Program.RunDefaultReportCoreAsync(
            runConfig,
            gitWizardConfig,
            coordinator,
            stdout,
            stderr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(File.Exists(cachedReportPath), Is.True);
            Assert.That(File.Exists(customSavePath), Is.True);
        }

        var cachedText = await File.ReadAllTextAsync(cachedReportPath);
        var customText = await File.ReadAllTextAsync(customSavePath);

        using var cachedDoc = JsonDocument.Parse(cachedText);
        using var customDoc = JsonDocument.Parse(customText);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cachedDoc.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("2.1"));
            Assert.That(cachedDoc.RootElement.GetProperty("Repositories").EnumerateObject().Count(), Is.Zero);
            Assert.That(customDoc.RootElement.GetProperty("SchemaVersion").GetString(), Is.EqualTo("2.1"));
            Assert.That(customDoc.RootElement.GetProperty("Repositories").EnumerateObject().Count(), Is.Zero);
        }
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

        using var tempDirOwner = new TempDirectory("gw-repo-");
        var tempDir = tempDirOwner.Path;

        Repository.Init(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "uncommitted.txt"), "pending changes");

        var customSavePath = Path.Combine(tempDir, "explicit-report.json");
        var runConfig = CliParser.Parse(["--paths", tempDir, "--save-path", customSavePath]);
        var gitWizardConfig = new GitWizardConfiguration();

        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();

        var exitCode = await Program.RunDefaultReportCoreAsync(
            runConfig,
            gitWizardConfig,
            throwingCoordinator,
            stdout,
            stderr);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(File.Exists(customSavePath), Is.True);
        }

        var savedText = await File.ReadAllTextAsync(customSavePath);
        using var doc = JsonDocument.Parse(savedText);
        Assert.That(doc.RootElement.GetProperty("Repositories").EnumerateObject().Count(), Is.EqualTo(1));
    }

    [Test]
    public void InMemoryReport_RejectionGuards_ThrowWhenIncomplete()
    {
        using var tempDir = new TempDirectory();
        var tempFile = Path.Combine(tempDir.Path, "gw-guard.json");
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

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<InvalidOperationException>(() => incompleteReport.Save(tempFile));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await incompleteReport.SaveAsync(tempFile));
            Assert.Throws<InvalidOperationException>(() => incompleteReport.SaveAtomic(tempFile));
        }

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
}

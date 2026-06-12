using GitWizard;
using MFTLib;

namespace GitWizardTests;

/// <summary>
/// Exercises the real-privilege code paths the fake-injected <see cref="ElevationDecisionTests"/>
/// deliberately cannot reach: the already-elevated branch of
/// <see cref="GitWizardApi.TryFindAllRepositoriesUsingMft"/> doing a real <c>MftVolume</c> scan, and
/// the elevated-child entry point <see cref="GitWizardApi.RunElevatedMftScan"/>.
///
/// These run only when the test process is genuinely elevated. A normal non-elevated
/// <c>dotnet test</c> short-circuits each test to Inconclusive (no UAC, no failure);
/// <c>scripts/run-coverage.ps1</c> self-elevates to include them in the coverage truth. Mirrors
/// MFTLib's RequiresAdmin pattern.
///
/// IMPORTANT: the guard MUST be the first statement of each test *body*, not in [SetUp]. NUnit does
/// NOT reliably skip the test body when SetUp calls Assert.Inconclusive, so a SetUp-only guard lets
/// the body run against the real provider and fire UAC. (Assume.That in SetUp would also work, but
/// the body-level guard keeps the intent obvious.)
///
/// Windows-only (MFT is NTFS-specific), so they contribute nothing to the Linux CI coverage gate -
/// that gate is the cheap cross-platform regression floor; the real-privilege truth comes from the
/// self-elevating run.
/// </summary>
[Category("RequiresAdmin")]
[Platform("Win")]
public class ElevationCoverageTests
{
    string _tempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        // Safe even when we'll go Inconclusive: this only makes a temp dir, no elevation call.
        _tempRoot = TestUtilities.RedirectLocalFilesToTemp();
    }

    [TearDown]
    public void TearDown() => TestUtilities.ClearLocalFilesRedirect(_tempRoot);

    [Test]
    public void RunElevatedMftScan_EmptyConfig_WritesEmptyOutput()
    {
        // Guard FIRST, in the body (NUnit does not reliably skip the body from a SetUp guard).
        if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        // A config with no search paths scans nothing and writes an empty output file - covering
        // RunElevatedMftScan's read-config / write-output path without a slow full-volume scan.
        var configPath = Path.Combine(_tempRoot, "config.json");
        new GitWizardConfiguration().Save(configPath);
        var outputPath = Path.Combine(_tempRoot, "mft-output.txt");

        GitWizardApi.RunElevatedMftScan(configPath, outputPath);

        Assert.That(File.Exists(outputPath), Is.True);
        Assert.That(File.ReadAllLines(outputPath), Is.Empty);
    }

    [Test]
    public void TryFindAllRepositoriesUsingMft_Elevated_RealMftScanDoesNotThrow()
    {
        // Guard FIRST, in the body (NUnit does not reliably skip the body from a SetUp guard).
        if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        // Real MftVolume scan via the already-elevated branch (the default provider reports elevated
        // here, so no child process spawns). Exercises TryFindGitRepositoriesUsingMft (Open +
        // FindRecords + filter). We do NOT assert it discovers the freshly-created .git: the raw MFT
        // read can lag a just-made directory, so asserting discovery would be timing-flaky. The
        // coverage goal is exercising the real scan path without throwing.
        var searchRoot = Path.Combine(Path.GetTempPath(), "GitWizardMftScan", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(searchRoot, "repo", ".git"));
        try
        {
            var configuration = new GitWizardConfiguration { SearchPaths = { searchRoot } };
            var paths = new SortedSet<string>();

            Assert.DoesNotThrow(() => GitWizardApi.TryFindAllRepositoriesUsingMft(
                configuration, paths, elevation: ElevationUtilities.DefaultProvider));
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }
}

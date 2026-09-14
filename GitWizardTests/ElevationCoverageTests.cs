using GitWizard;
using MFTLib;

namespace GitWizardTests;

/// <summary>
/// Exercises the real-privilege code path the fake-injected <see cref="ElevationDecisionTests"/>
/// deliberately cannot reach: the broker-backed discovery path of
/// <see cref="GitWizardApi.TryFindAllRepositoriesUsingMftAsync"/> opening a real packed index.
///
/// Runs only when the test process is genuinely elevated. A normal non-elevated
/// <c>dotnet test</c> short-circuits each test to Inconclusive (no UAC, no failure);
/// <c>scripts/run-coverage.ps1</c> self-elevates to include it in the coverage truth. Mirrors
/// MFTLib's RequiresAdmin pattern.
///
/// IMPORTANT: the guard MUST be the first statement of each test *body*, not in [SetUp]. NUnit does
/// NOT reliably skip the test body when SetUp calls Assert.Inconclusive, so a SetUp-only guard lets
/// the body run against the real provider and fire UAC. (Assume.That in SetUp would also work, but
/// the body-level guard keeps the intent obvious.)
///
/// Windows-only (MFT is NTFS-specific), so it contributes nothing to the Linux CI coverage gate -
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
    public async Task TryFindAllRepositoriesUsingMftAsync_Elevated_FindsPreExistingRepository()
    {
        // Guard FIRST, in the body (NUnit does not reliably skip the body from a SetUp guard).
        if (!ElevationUtilities.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        var searchRoot = Path.Combine(Path.GetTempPath(), "GitWizardMftScan", Guid.NewGuid().ToString("N"));
        var repositoryPath = Path.Combine(searchRoot, "repo");
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
        try
        {
            var configuration = new GitWizardConfiguration { SearchPaths = { searchRoot } };
            var paths = new SortedSet<string>();

            var discovered = await GitWizardApi.TryFindAllRepositoriesUsingMftAsync(configuration, paths);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(discovered, Is.True);
                Assert.That(paths, Does.Contain(repositoryPath).IgnoreCase);
            }
        }
        finally
        {
            Directory.Delete(searchRoot, recursive: true);
        }
    }
}

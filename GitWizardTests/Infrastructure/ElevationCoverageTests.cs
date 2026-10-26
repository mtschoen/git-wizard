using GitWizard;
using GitWizardTests.TestSupport;
using MFTLib;

namespace GitWizardTests;

/// <summary>
/// <para>
/// Exercises the real-privilege code path the fake-injected <see cref="ElevationDecisionTests"/>
/// deliberately cannot reach: the broker-backed discovery path of
/// <see cref="GitWizardApi.TryDiscoverRepositoriesUsingMftAsync"/> opening a real packed index.
/// </para>
/// <para>
/// Runs only when the test process is genuinely elevated. A normal non-elevated
/// <c>dotnet test</c> short-circuits each test to Inconclusive (no UAC, no failure);
/// <c>scripts/run-coverage.ps1</c> self-elevates to include it in the coverage truth. Mirrors
/// MFTLib's RequiresAdmin pattern.
/// </para>
/// <para>
/// IMPORTANT: the guard MUST be the first statement of each test *body*, not in [SetUp]. NUnit does
/// NOT reliably skip the test body when SetUp calls Assert.Inconclusive, so a SetUp-only guard lets
/// the body run against the real provider and fire UAC. (Assume.That in SetUp would also work, but
/// the body-level guard keeps the intent obvious.)
/// </para>
/// <para>
/// Windows-only (MFT is NTFS-specific), so it contributes nothing to the Linux CI coverage gate -
/// that gate is the cheap cross-platform regression floor; the real-privilege truth comes from the
/// self-elevating run.
/// </para>
/// </summary>
[Category("RequiresAdmin")]
[Platform("Win")]
public class ElevationCoverageTests
{
    LocalFilesSandbox? _sandbox;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        // Safe even when we'll go Inconclusive: this only makes a temp dir, no elevation call.
        _sandbox = new LocalFilesSandbox();
    }

    [TearDown]
    public void TearDown() => _sandbox?.Dispose();

    [Test]
    public async Task TryDiscoverRepositoriesUsingMftAsync_Elevated_FindsPreExistingRepository()
    {
        // Guard FIRST, in the body (NUnit does not reliably skip the body from a SetUp guard).
        if (!ElevationUtilities.DefaultProvider.IsElevated()) { Assert.Inconclusive("Requires admin"); return; }

        using var rootDir = new TempDirectory("GitWizardMftScan-");
        var searchRoot = rootDir.Path;
        var repositoryPath = Path.Combine(searchRoot, "repo");
        Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));

        var configuration = new GitWizardConfiguration { SearchPaths = { searchRoot } };
        var paths = new SortedSet<string>();

        await using var result = await GitWizardApi.TryDiscoverRepositoriesUsingMftAsync(
            configuration, paths, brokerLauncher: UiBrokerLauncher.LaunchUiBroker);

        var connectionFailure = result.ScanErrors.Values.FirstOrDefault(TestUtilities.IsBrokerConnectionFailure);
        if (connectionFailure is not null)
        {
            Assert.Fail($"The UI app-host broker never connected: {connectionFailure}");
            return;
        }

        if (result.ScanErrors.Count > 0)
        {
            Assert.Fail($"MFT scan failed: {string.Join("; ", result.ScanErrors.Values)}");
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.True,
                "The UI broker host connected, but the existing repository was not discovered.");
            Assert.That(paths, Does.Contain(repositoryPath).IgnoreCase);
        }
    }

    [Test]
    public async Task TryDiscoverRepositoriesUsingMftAsync_InjectedBrokerLauncher_InvokesLauncher()
    {
        var receivedBrokerArguments = new List<string>();
        var configuration = new GitWizardConfiguration { SearchPaths = { TempDirectory.SystemTempPath } };

        await using var result = await GitWizardApi.TryDiscoverRepositoriesUsingMftAsync(
            configuration, new List<string>(), brokerLauncher: brokerArguments =>
            {
                receivedBrokerArguments.Add(brokerArguments);
                return false;
            });

        Assert.That(result.Success, Is.False);
        var brokerArgumentCount = receivedBrokerArguments.Count;
        Assert.That(brokerArgumentCount, Is.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receivedBrokerArguments.Single(), Does.StartWith("--broker --pipe "));
            Assert.That(result.ScanErrors.Values, Has.Some.Contains("Failed to launch the elevated broker"));
        }
        Assert.That(result.ScanErrors.Values, Has.Some.Matches<string>(TestUtilities.IsBrokerConnectionFailure));
    }

    [Test]
    public async Task TryDiscoverRepositoriesUsingMftAsync_BrokerNeverConnects_RecordsTimeoutInScanErrors()
    {
        var receivedBrokerArguments = new List<string>();
        var configuration = new GitWizardConfiguration { SearchPaths = { TempDirectory.SystemTempPath } };

        await using var result = await GitWizardApi.TryDiscoverRepositoriesUsingMftAsync(
            configuration, new List<string>(),
            brokerLauncher: brokerArguments =>
            {
                receivedBrokerArguments.Add(brokerArguments);
                return true;
            },
            connectTimeout: TimeSpan.FromMilliseconds(50));

        Assert.That(result.Success, Is.False);
        var brokerArgumentCount = receivedBrokerArguments.Count;
        Assert.That(brokerArgumentCount, Is.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receivedBrokerArguments.Single(), Does.StartWith("--broker --pipe "));
            Assert.That(result.ScanErrors.Values, Has.Some.Contains("Timed out waiting"));
        }
        Assert.That(result.ScanErrors.Values, Has.Some.Contains("never connected"));
        Assert.That(result.ScanErrors.Values, Has.Some.Matches<string>(TestUtilities.IsBrokerConnectionFailure));
    }

}

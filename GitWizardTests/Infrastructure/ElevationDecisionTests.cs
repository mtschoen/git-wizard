using GitWizard;
using MFTLib;

namespace GitWizardTests;

/// <summary>
/// Validates that git-wizard's self-elevation decision logic is testable through
/// MFTLib's public <see cref="IElevationProvider"/> - including the already-elevated
/// branch - without triggering real UAC. The fake controls every elevation answer.
/// </summary>
public class ElevationDecisionTests
{
    sealed class FakeElevationProvider : IElevationProvider
    {
        public bool Elevated;
        public bool SelfElevatable = true;
        public bool RunElevatedResult;
        public readonly List<(IReadOnlyList<string> Arguments, TimeSpan Timeout)> RunElevatedCalls = [];

        public bool IsElevated() => Elevated;
        public bool CanSelfElevate() => SelfElevatable;

        public bool TryRunElevated(IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            RunElevatedCalls.Add((arguments, timeout));
            return RunElevatedResult;
        }
    }

    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    // --- Defender (the fake means no real process is ever spawned) ---

    [Test]
    [Platform("Win")]
    public void AddExclusions_NotElevatedButCanSelfElevate_RoutesThroughTryRunElevated()
    {
        var fake = new FakeElevationProvider { Elevated = false, SelfElevatable = true, RunElevatedResult = true };

        var result = WindowsDefender.AddExclusions(fake);

        Assert.That(result, Is.True);
        Assert.That(fake.RunElevatedCalls, Has.Count.EqualTo(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fake.RunElevatedCalls[0].Arguments, Is.EqualTo(["--elevated-defender"]));
            Assert.That(fake.RunElevatedCalls[0].Timeout, Is.EqualTo(TimeSpan.FromSeconds(60)));
        }
    }

    [Test]
    [Platform("Unix")]
    public void AddExclusions_OffWindows_ReturnsFalseWithoutElevating()
    {
        var fake = new FakeElevationProvider { Elevated = false, SelfElevatable = true, RunElevatedResult = true };

        var result = WindowsDefender.AddExclusions(fake);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.False);
            Assert.That(fake.RunElevatedCalls, Is.Empty);
        }
    }

    [Test]
    public void TryRunElevatedHelper_WithoutHelperArgument_ReturnsNullWithoutElevating()
    {
        var fake = new FakeElevationProvider { Elevated = false, SelfElevatable = true, RunElevatedResult = true };

        var result = WindowsDefender.TryRunElevatedHelper(["git-wizard.exe", "-summary"], fake);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.Null);
            Assert.That(fake.RunElevatedCalls, Is.Empty);
        }
    }

    [Test]
    [Platform("Win")]
    [TestCase(true, 0)]
    [TestCase(false, 1)]
    public void TryRunElevatedHelper_WithHelperArgument_ReturnsExclusionOutcomeExitCode(bool succeeds, int expectedExitCode)
    {
        var fake = new FakeElevationProvider { Elevated = false, SelfElevatable = true, RunElevatedResult = succeeds };

        var result = WindowsDefender.TryRunElevatedHelper(["--elevated-defender"], fake);

        Assert.That(result, Is.EqualTo(expectedExitCode));
    }

    [Test]
    [Platform("Unix")]
    public void TryRunElevatedHelper_WithHelperArgumentOffWindows_ReturnsFailureExitCode()
    {
        var fake = new FakeElevationProvider { Elevated = false, SelfElevatable = true, RunElevatedResult = true };

        var result = WindowsDefender.TryRunElevatedHelper(["--elevated-defender"], fake);

        Assert.That(result, Is.EqualTo(1));
    }

    [Test]
    [Platform("Win")]
    public async Task DiscoverRepositoriesWithIndexAsync_EmptySearchPaths_DoesNotInvokeConnectionFactory()
    {
        var calls = 0;
        var paths = new SortedSet<string>();
        await using var result = await GitWizardApi.DiscoverRepositoriesWithIndexAsync(
            new GitWizardConfiguration(), paths, [], [], new IndexDiscoveryComposition(
                _ =>
                {
                    calls++;
                    throw new AssertionException("Empty search paths must not connect.");
                },
                _ => true));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Success, Is.False);
            Assert.That(calls, Is.Zero);
            Assert.That(paths, Is.Empty);
        }
    }
}

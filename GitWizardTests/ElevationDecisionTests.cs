using GitWizard;
using MFTLib;

namespace GitWizardTests;

/// <summary>
/// Validates that git-wizard's self-elevation decision logic is testable through
/// MFTLib's public <see cref="IElevationProvider"/> — including the already-elevated
/// branch — without triggering real UAC. The fake controls every elevation answer.
/// </summary>
public class ElevationDecisionTests
{
    sealed class FakeElevationProvider : IElevationProvider
    {
        public bool Elevated;
        public bool SelfElevatable = true;
        public bool RunElevatedResult;
        public readonly List<(string Arguments, int TimeoutMs)> RunElevatedCalls = new();

        public bool IsElevated() => Elevated;
        public bool CanSelfElevate() => SelfElevatable;

        public bool TryRunElevated(string arguments, int timeoutMs = 60000)
        {
            RunElevatedCalls.Add((arguments, timeoutMs));
            return RunElevatedResult;
        }
    }

    [SetUp]
    public void SetUp() => GitWizardLog.SilentMode = true;

    // --- Defender (cross-platform; the fake means no real process is ever spawned) ---

    [Test]
    public void AddExclusions_NotElevatedButCanSelfElevate_RoutesThroughTryRunElevated()
    {
        var fake = new FakeElevationProvider { Elevated = false, SelfElevatable = true, RunElevatedResult = true };

        var result = WindowsDefender.AddExclusions(fake);

        Assert.That(result, Is.True);
        Assert.That(fake.RunElevatedCalls, Has.Count.EqualTo(1));
        Assert.That(fake.RunElevatedCalls[0].Arguments, Is.EqualTo("--elevated-defender"));
        Assert.That(fake.RunElevatedCalls[0].TimeoutMs, Is.EqualTo(30000));
    }

    // --- MFT (Windows-only decision logic; the fake controls elevation, no real scan) ---

    [Test]
    [Platform("Win")]
    public void TryFindAllRepositoriesUsingMft_NotElevated_RoutesThroughTryRunElevated()
    {
        var fake = new FakeElevationProvider { Elevated = false, RunElevatedResult = false };
        var paths = new SortedSet<string>();

        var result = GitWizardApi.TryFindAllRepositoriesUsingMft(
            new GitWizardConfiguration(), paths, elevation: fake);

        Assert.That(result, Is.False);
        Assert.That(fake.RunElevatedCalls, Has.Count.EqualTo(1));
        Assert.That(fake.RunElevatedCalls[0].Arguments, Does.StartWith("--elevated-mft --config-path "));
        Assert.That(fake.RunElevatedCalls[0].TimeoutMs, Is.EqualTo(120000));
    }

    [Test]
    [Platform("Win")]
    public void TryFindAllRepositoriesUsingMft_ElevatedWithEmptySearchPaths_DoesNotSelfElevate()
    {
        // Already-elevated branch: scans directly. Empty search paths ⇒ no real MftVolume.Open,
        // and TryRunElevated must never be called. This branch is unreachable via the internal
        // Func seams alone (IsElevated does a real token check) — it needs the injected provider.
        var fake = new FakeElevationProvider { Elevated = true };
        var paths = new SortedSet<string>();

        var result = GitWizardApi.TryFindAllRepositoriesUsingMft(
            new GitWizardConfiguration(), paths, elevation: fake);

        Assert.That(result, Is.False);
        Assert.That(fake.RunElevatedCalls, Is.Empty);
        Assert.That(paths, Is.Empty);
    }
}

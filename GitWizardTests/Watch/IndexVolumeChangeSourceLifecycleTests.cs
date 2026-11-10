using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;
using MFTLib.Index;

namespace GitWizardTests.Watch;

/// <summary>
/// The usage rules of <see cref="IndexVolumeChangeSource"/>: arm before watching, one watch reader,
/// and journal-settings queries need an index session.
/// </summary>
[TestFixture]
public class IndexVolumeChangeSourceLifecycleTests
{
    static IndexVolumeChangeSource SourceWithoutSession() =>
        new((_, _, _) => throw new AssertionException("No session may be opened by these calls."));

    [Test]
    public async Task WatchAsync_BeforeArming_IsRejected()
    {
        await using var source = SourceWithoutSession();
        await using var reader = source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();

        var rejection = Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.MoveNextAsync());

        Assert.That(rejection.Message, Is.EqualTo("ArmAndCatchUpAsync must finish before WatchAsync."));
    }

    [Test]
    public async Task WatchAsync_SecondReader_IsRejectedWhileTheFirstIsClaimed()
    {
        using var files = new PackedIndexFixture();
        files.AddMarker("repo");
        var owner = new MftIndexSession([await files.OpenIndexAsync()], new CountingOwner(), ["X"], new Dictionary<string, string>());
        await using var source = new IndexVolumeChangeSource((_, _, _) => Task.FromResult(owner), owner);
        await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
        await using var first = source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();
        await first.MoveNextAsync();
        await using var second = source.WatchAsync(CancellationToken.None).GetAsyncEnumerator();

        var rejection = Assert.ThrowsAsync<InvalidOperationException>(async () => await second.MoveNextAsync());

        Assert.That(rejection.Message, Is.EqualTo("This source already has a watch reader."));
    }

    [Test]
    public async Task QueryUsnJournalSettings_WithoutASession_ExplainsWhatIsMissing()
    {
        await using var source = SourceWithoutSession();

        var failure = Assert.Throws(
            OperatingSystem.IsWindows() ? typeof(InvalidOperationException) : typeof(PlatformNotSupportedException),
            () => source.QueryUsnJournalSettings(new VolumeKey('X')));

        Assert.That(failure.Message, Is.EqualTo(OperatingSystem.IsWindows()
            ? "No index session available."
            : "USN journals require Windows."));
    }

    [Test]
    public async Task GrowUsnJournalAsync_WithoutASession_IsRejected()
    {
        await using var source = SourceWithoutSession();

        var failure = Assert.ThrowsAsync<InvalidOperationException>(() => source.GrowUsnJournalAsync(
            'X', new UsnJournalSettings { MaximumSize = 4096, AllocationDelta = 512 }, CancellationToken.None));

        Assert.That(failure.Message, Is.EqualTo("No index session available."));
    }
}

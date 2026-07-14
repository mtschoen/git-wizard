using GitWizard.Watch;

namespace GitWizardTests.Watch;

// A scripted IVolumeChangeSource used across the phase-1/phase-2 tests.
internal sealed class FakeVolumeChangeSource : IVolumeChangeSource
{
    readonly IReadOnlyList<VolumeColdRecord> _cold;
    readonly IReadOnlyList<VolumeChangeBatch> _batches;
    public event Action<string>? SourceDied;

    public FakeVolumeChangeSource(
        IReadOnlyList<VolumeColdRecord> cold, IReadOnlyList<VolumeChangeBatch> batches)
    {
        _cold = cold;
        _batches = batches;
    }

    public Task<IReadOnlyList<VolumeColdRecord>> ArmAndCatchUpAsync(
        IReadOnlyCollection<string> volumes, CancellationToken ct) => Task.FromResult(_cold);

    public async IAsyncEnumerable<VolumeChangeBatch> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var batch in _batches)
        {
            ct.ThrowIfCancellationRequested();
            yield return batch;
            await Task.Yield();
        }
    }

    public void KillSource(string reason) => SourceDied?.Invoke(reason);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[TestFixture]
public class FakeVolumeChangeSourceTests
{
    [Test]
    public async Task WatchAsync_YieldsScriptedBatches()
    {
        var source = new FakeVolumeChangeSource(
            cold: Array.Empty<VolumeColdRecord>(),
            batches: new[]
            {
                new VolumeChangeBatch("C", new[]
                {
                    new VolumeChangeEntry(@"C:\repo\a\file.txt", VolumeEntryKind.Modified)
                })
            });

        var seen = new List<VolumeChangeBatch>();
        await foreach (var batch in source.WatchAsync(CancellationToken.None))
            seen.Add(batch);

        Assert.That(seen, Has.Count.EqualTo(1));
        Assert.That(seen[0].Volume, Is.EqualTo("C"));
        Assert.That(seen[0].Entries[0].FullPath, Is.EqualTo(@"C:\repo\a\file.txt"));
        Assert.That(seen[0].Entries[0].Kind, Is.EqualTo(VolumeEntryKind.Modified));
    }

    [Test]
    public async Task ArmAndCatchUpAsync_ReturnsScriptedColdRecords()
    {
        var source = new FakeVolumeChangeSource(
            cold: new[] { new VolumeColdRecord(@"C:\repo\a\file.txt", 42UL) },
            batches: Array.Empty<VolumeChangeBatch>());

        var cold = await source.ArmAndCatchUpAsync(new[] { "C" }, CancellationToken.None);

        Assert.That(cold[0].Path, Is.EqualTo(@"C:\repo\a\file.txt"));
        Assert.That(cold[0].RecordId, Is.EqualTo(42UL));
    }

    [Test]
    public void KillSource_RaisesSourceDiedWithReason()
    {
        var source = new FakeVolumeChangeSource(
            cold: Array.Empty<VolumeColdRecord>(), batches: Array.Empty<VolumeChangeBatch>());
        string? reason = null;
        source.SourceDied += r => reason = r;

        source.KillSource("volume unmounted");

        Assert.That(reason, Is.EqualTo("volume unmounted"));
    }
}

[TestFixture]
public class RepositoryChangeEventTests
{
    [Test]
    public void Constructor_SetsRepoRootKindAndNewPath()
    {
        var evt = new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Renamed, @"C:\repo\b");

        Assert.That(evt.RepoRoot, Is.EqualTo(@"C:\repo\a"));
        Assert.That(evt.Kind, Is.EqualTo(RepositoryChangeKind.Renamed));
        Assert.That(evt.NewPath, Is.EqualTo(@"C:\repo\b"));
    }

    [Test]
    public void Constructor_DefaultsNewPathToNull()
    {
        var evt = new RepositoryChangeEvent(@"C:\repo\a", RepositoryChangeKind.Changed);

        Assert.That(evt.NewPath, Is.Null);
    }
}

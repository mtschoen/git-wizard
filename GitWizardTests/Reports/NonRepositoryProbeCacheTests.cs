using GitWizard;

namespace GitWizardTests;

public class NonRepositoryProbeCacheTests
{
    static readonly DateTimeOffset InitialTimeUtc = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void FailedProbe_UnchangedLastWriteTime_IsSkippedUntilItChanges()
    {
        var candidatePath = Path.GetFullPath("junk-candidate");
        var directoryFacts = new DirectoryFacts { LastWriteTimeUtc = InitialTimeUtc };
        var entries = new SortedDictionary<string, NonRepositoryProbeEntry>();
        var cache = new NonRepositoryProbeCache(entries, new ControllableClock(InitialTimeUtc),
            _ => true, _ => directoryFacts.LastWriteTimeUtc);
        var probeCount = 0;

        ProbeCandidate();
        ProbeCandidate();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(probeCount, Is.EqualTo(1));
            var expectedKey = OperatingSystem.IsWindows() ? candidatePath.ToLowerInvariant() : candidatePath;
            Assert.That(entries.Keys.Single(), Is.EqualTo(expectedKey));
        }

        directoryFacts.LastWriteTimeUtc = directoryFacts.LastWriteTimeUtc.AddMinutes(1);
        ProbeCandidate();

        Assert.That(probeCount, Is.EqualTo(2));
        return;

        void ProbeCandidate()
        {
            if (cache.ShouldSkip(candidatePath, out var observedWriteTimeUtc))
            {
                return;
            }

            probeCount++;
            cache.Record(candidatePath, observedWriteTimeUtc);
        }
    }

    [Test]
    public void DropMissingPaths_RemovesDisappearedCandidate()
    {
        var candidatePath = Path.GetFullPath("missing-candidate");
        var directoryFacts = new DirectoryFacts();
        var entries = new SortedDictionary<string, NonRepositoryProbeEntry>();
        var cache = new NonRepositoryProbeCache(entries, new ControllableClock(InitialTimeUtc),
            _ => directoryFacts.Exists, _ => InitialTimeUtc);
        cache.Record(candidatePath, InitialTimeUtc);
        Assert.That(cache.Contains(candidatePath), Is.True, "The test setup must contain a failed probe.");
        directoryFacts.Exists = false;

        cache.DropMissingPaths();

        Assert.That(entries, Is.Empty);
    }

    [Test]
    public void Record_BeyondCapacity_EvictsLeastRecentlyUsedCandidate()
    {
        var firstPath = Path.GetFullPath("junk-first");
        var secondPath = Path.GetFullPath("junk-second");
        var thirdPath = Path.GetFullPath("junk-third");
        var clock = new ControllableClock(InitialTimeUtc);
        var entries = new SortedDictionary<string, NonRepositoryProbeEntry>();
        var cache = new NonRepositoryProbeCache(entries, clock, _ => true, _ => InitialTimeUtc, capacity: 2);
        cache.Record(firstPath, InitialTimeUtc);
        clock.Advance(TimeSpan.FromMinutes(1));
        cache.Record(secondPath, InitialTimeUtc);
        clock.Advance(TimeSpan.FromMinutes(1));
        cache.ShouldSkip(firstPath, out _);
        clock.Advance(TimeSpan.FromMinutes(1));

        cache.Record(thirdPath, InitialTimeUtc);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Contains(firstPath), Is.True);
            Assert.That(cache.Contains(secondPath), Is.False);
            Assert.That(cache.Contains(thirdPath), Is.True);
        }
    }

    [Test]
    public void Remove_ValidCandidate_DeletesFailure()
    {
        var candidatePath = Path.GetFullPath("recovered-candidate");
        var entries = new SortedDictionary<string, NonRepositoryProbeEntry>();
        var cache = new NonRepositoryProbeCache(entries, new ControllableClock(InitialTimeUtc),
            _ => true, _ => InitialTimeUtc);
        cache.Record(candidatePath, InitialTimeUtc);
        Assert.That(cache.Contains(candidatePath), Is.True, "The test setup must contain a failed probe.");

        cache.Remove(candidatePath);

        Assert.That(cache.Contains(candidatePath), Is.False);
    }

    sealed class DirectoryFacts
    {
        public bool Exists { get; set; } = true;
        public DateTimeOffset LastWriteTimeUtc { get; set; }
    }

    sealed class ControllableClock(DateTimeOffset initialTimeUtc) : TimeProvider
    {
        DateTimeOffset _utcNow = initialTimeUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}

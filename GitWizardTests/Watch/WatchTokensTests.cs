using GitWizard.Watch;

namespace GitWizardTests.Watch;

[TestFixture]
public class WatchTokensTests
{
    static void AssertTable<TEnum>(Dictionary<TEnum, string> expected, Func<TEnum, string> token)
        where TEnum : struct, Enum
    {
        Assert.That(expected.Keys, Is.EquivalentTo(Enum.GetValues<TEnum>()));
        foreach (var (member, text) in expected)
        {
            Assert.That(token(member), Is.EqualTo(text), member.ToString());
        }
        Assert.That(expected.Values, Is.Unique);
    }

    [Test]
    public void ReadinessModeToken_EveryMember_HasDistinctStableToken() =>
        AssertTable(new Dictionary<WatchReadinessMode, string>
        {
            [WatchReadinessMode.PumpActive] = "pump",
            [WatchReadinessMode.JournalBacklog] = "journal",
            [WatchReadinessMode.FilesystemMarks] = "marks"
        }, WatchTokens.Token);

    [Test]
    public void VolumeStateToken_EveryMember_HasDistinctStableToken() =>
        AssertTable(new Dictionary<VolumeWatchState, string>
        {
            [VolumeWatchState.CatchingUp] = "catching-up",
            [VolumeWatchState.CaughtUp] = "caught-up",
            [VolumeWatchState.Excluded] = "excluded"
        }, WatchTokens.Token);

    [Test]
    public void BlockOriginToken_EveryMember_HasDistinctStableToken() =>
        AssertTable(new Dictionary<VolumeBlockOrigin, string>
        {
            [VolumeBlockOrigin.Unknown] = "unknown",
            [VolumeBlockOrigin.Warm] = "warm",
            [VolumeBlockOrigin.Cold] = "cold"
        }, WatchTokens.Token);

    [Test]
    public void StartupPhaseToken_EveryMember_HasDistinctStableToken() =>
        AssertTable(new Dictionary<WatchStartupPhase, string>
        {
            [WatchStartupPhase.Opening] = "opening",
            [WatchStartupPhase.RequestingElevation] = "requesting-elevation",
            [WatchStartupPhase.BrokerConnected] = "broker-connected",
            [WatchStartupPhase.Scanning] = "scanning",
            [WatchStartupPhase.CatchingUp] = "catching-up",
            [WatchStartupPhase.Discovering] = "discovering",
            [WatchStartupPhase.Reconciling] = "reconciling"
        }, WatchTokens.Token);

    [Test]
    public void Token_UnmappedMember_Throws()
    {
        Assert.That(() => ((WatchStartupPhase)99).Token(), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => ((WatchReadinessMode)99).Token(), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => ((VolumeWatchState)99).Token(), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => ((VolumeBlockOrigin)99).Token(), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Humanize_ReplacesHyphensWithSpaces()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(VolumeWatchState.CatchingUp.Token().Humanize(), Is.EqualTo("catching up"));
            Assert.That(VolumeWatchState.CaughtUp.Token().Humanize(), Is.EqualTo("caught up"));
            Assert.That(VolumeBlockOrigin.Warm.Token().Humanize(), Is.EqualTo("warm"));
        }
    }
}

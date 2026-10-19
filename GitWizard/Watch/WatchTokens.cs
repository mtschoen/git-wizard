namespace GitWizard.Watch;

/// <summary>
/// The stable lowercase tokens for the watch enums. The CLI <c>ready:</c>, <c>drive:</c> and
/// <c>startup:</c> lines print them as is (a documented contract); the UI humanizes the same token.
/// </summary>
public static class WatchTokens
{
    public static string Token(this WatchReadinessMode mode) => mode switch
    {
        WatchReadinessMode.PumpActive => "pump",
        WatchReadinessMode.JournalBacklog => "journal",
        WatchReadinessMode.FilesystemMarks => "marks",
        _ => throw Unmapped(mode)
    };

    public static string Token(this VolumeWatchState state) => state switch
    {
        VolumeWatchState.CatchingUp => "catching-up",
        VolumeWatchState.CaughtUp => "caught-up",
        VolumeWatchState.Excluded => "excluded",
        _ => throw Unmapped(state)
    };

    public static string Token(this VolumeBlockOrigin origin) => origin switch
    {
        VolumeBlockOrigin.Unknown => "unknown",
        VolumeBlockOrigin.Warm => "warm",
        VolumeBlockOrigin.Cold => "cold",
        _ => throw Unmapped(origin)
    };

    public static string Token(this WatchStartupPhase phase) => phase switch
    {
        WatchStartupPhase.Opening => "opening",
        WatchStartupPhase.RequestingElevation => "requesting-elevation",
        WatchStartupPhase.BrokerConnected => "broker-connected",
        WatchStartupPhase.Scanning => "scanning",
        WatchStartupPhase.CatchingUp => "catching-up",
        WatchStartupPhase.Discovering => "discovering",
        WatchStartupPhase.Reconciling => "reconciling",
        _ => throw Unmapped(phase)
    };

    private static ArgumentOutOfRangeException Unmapped<TEnum>(TEnum value) where TEnum : struct, Enum =>
        new(nameof(value), value, $"{typeof(TEnum).Name} has no watch token for this member.");

    /// <summary>Turns a hyphenated token into spaced display wording.</summary>
    public static string Humanize(this string token) => token.Replace('-', ' ');
}

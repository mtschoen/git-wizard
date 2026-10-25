namespace GitWizardTests.TestSupport;

sealed class FrozenCacheClock : TimeProvider
{
    public static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

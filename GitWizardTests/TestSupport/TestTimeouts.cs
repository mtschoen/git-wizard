namespace GitWizardTests.TestSupport;

static class TestTimeouts
{
    public static TimeSpan HangGuard => TimeSpan.FromSeconds(10);
    public static TimeSpan BrokerHangGuard => TimeSpan.FromSeconds(30);
    public static TimeSpan ShortHangGuard => TimeSpan.FromSeconds(5);
}

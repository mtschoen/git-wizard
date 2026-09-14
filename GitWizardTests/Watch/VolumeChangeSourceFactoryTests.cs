using GitWizard.Watch;
using GitWizardTests.TestSupport;
using MFTLib;

namespace GitWizardTests.Watch;

[TestFixture]
public class VolumeChangeSourceFactoryTests
{
    [TearDown]
    public void TearDown()
    {
        VolumeChangeSourceFactory.ResetToDefaults();
    }

    [Test]
    public void IsElevated_OnLinux_MatchesProcessPrivilege()
    {
        if (OperatingSystem.IsLinux())
        {
            var isElevated = VolumeChangeSourceFactory.IsElevated();
            var expected = VolumeChangeSourceFactory.LinuxNative.GetEffectiveUserId() == 0;
            Assert.That(isElevated, Is.EqualTo(expected));
        }
        else
        {
            Assert.Ignore("Linux-only test.");
        }
    }

    [Test]
    public void IsElevated_Override_ReturnsConfiguredValue()
    {
        VolumeChangeSourceFactory._isElevatedOverride = () => true;
        Assert.That(VolumeChangeSourceFactory.IsElevated(), Is.True);

        VolumeChangeSourceFactory._isElevatedOverride = () => false;
        Assert.That(VolumeChangeSourceFactory.IsElevated(), Is.False);
    }

    [Test]
    public void IsElevated_SimulatedUnsupportedPlatform_ReturnsFalse()
    {
        VolumeChangeSourceFactory._isWindowsOverride = () => false;
        VolumeChangeSourceFactory._isLinuxOverride = () => false;

        Assert.That(VolumeChangeSourceFactory.IsElevated(), Is.False);
    }

    [Test]
    public void IsElevated_SimulatedWindows_CallsElevationUtilities()
    {
        VolumeChangeSourceFactory._isWindowsOverride = () => true;
        VolumeChangeSourceFactory._isLinuxOverride = () => false;

        // ElevationUtilities on non-Windows returns false without error
        Assert.That(VolumeChangeSourceFactory.IsElevated(), Is.EqualTo(ElevationUtilities.IsElevated()));
    }

    [Test]
    public async Task Create_Default_ReturnsPlatformSpecificSource()
    {
        await using var source = VolumeChangeSourceFactory.Create();

        if (OperatingSystem.IsWindows())
            Assert.That(source.GetType().Name, Is.EqualTo("IndexVolumeChangeSource"));
        else if (OperatingSystem.IsLinux())
            Assert.That(source.GetType().Name, Is.EqualTo("FanotifyVolumeChangeSource"));
        else
            Assert.Ignore("Unsupported platform.");
    }

    [Test]
    public async Task Create_WithOverride_ReturnsConfiguredSource()
    {
        var fakeSource = new FakeVolumeChangeSource(Array.Empty<VolumeChangeBatch>());
        VolumeChangeSourceFactory._sourceFactoryOverride = () => fakeSource;

        await using var source = VolumeChangeSourceFactory.Create();
        Assert.That(source, Is.SameAs(fakeSource));
    }

    [Test]
    public void Create_SimulatedUnsupportedPlatform_ThrowsPlatformNotSupportedException()
    {
        VolumeChangeSourceFactory._isWindowsOverride = () => false;
        VolumeChangeSourceFactory._isLinuxOverride = () => false;

        Assert.Throws<PlatformNotSupportedException>(() => VolumeChangeSourceFactory.Create());
    }

    [Test]
    public async Task Create_WindowsWithPreparedIndex_CreatesIndexVolumeChangeSource()
    {
        VolumeChangeSourceFactory._isWindowsOverride = () => true;
        VolumeChangeSourceFactory._isLinuxOverride = () => false;
        await using var fixture = new IndexOwnerFixture();
        await fixture.StartAsync();
        var source = VolumeChangeSourceFactory.Create(fixture.Owner,
            _ => throw new AssertionException("Construction must not connect"));
        try
        {
            Assert.That(source.GetType().Name, Is.EqualTo("IndexVolumeChangeSource"));
            await source.ArmAndCatchUpAsync(["X"], CancellationToken.None);
            Assert.That(fixture.Owner.IsDisposed, Is.False);
        }
        finally
        {
            await source.DisposeAsync();
        }
        Assert.That(fixture.ConnectionDisposals, Is.EqualTo(1));
    }
}

using GitWizardUI.ViewModels;

namespace GitWizardTests;

/// <summary>
/// <c>GITWIZARD_NO_MFT</c> keeps unattended headless captures on the filesystem walk, so no
/// elevated broker (UAC prompt) is launched.
/// </summary>
[NonParallelizable]
public class MainViewModelNoMftTests
{
    const string VariableName = "GITWIZARD_NO_MFT";
    string? _previousValue;

    [SetUp]
    public void SetUp() => _previousValue = Environment.GetEnvironmentVariable(VariableName);

    [TearDown]
    public void TearDown() => Environment.SetEnvironmentVariable(VariableName, _previousValue);

    [Test]
    public void NoMftRequested_IsFalseWhenVariableIsUnsetOrEmpty()
    {
        Environment.SetEnvironmentVariable(VariableName, null);
        Assert.That(MainViewModel.NoMftRequested, Is.False);

        Environment.SetEnvironmentVariable(VariableName, string.Empty);
        Assert.That(MainViewModel.NoMftRequested, Is.False);
    }

    [Test]
    public void NoMftRequested_IsTrueWhenVariableIsSet()
    {
        Environment.SetEnvironmentVariable(VariableName, "1");
        Assert.That(MainViewModel.NoMftRequested, Is.True);
    }
}

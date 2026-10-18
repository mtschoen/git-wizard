using GitWizard.CLI;

namespace GitWizardTests;

/// <summary>
/// Verifies that the CLI's runtime configuration copies every parser result.
/// </summary>
public class RunConfigurationAdapterTests
{
    [Test]
    public void Constructor_ProcessArguments_CopiesEveryParserResult()
    {
        var expected = CliParser.ParseProcessArgs(Environment.GetCommandLineArgs());
        var actual = new Program.RunConfiguration();

        Assert.Multiple(() =>
        {
            Assert.That(actual.RebuildRepositoryList, Is.EqualTo(expected.RebuildRepositoryList));
            Assert.That(actual.RebuildReport, Is.EqualTo(expected.RebuildReport));
            Assert.That(actual.ClearCache, Is.EqualTo(expected.ClearCache));
            Assert.That(actual.ClearIndexCache, Is.EqualTo(expected.ClearIndexCache));
            Assert.That(actual.DeleteAllLocalFiles, Is.EqualTo(expected.DeleteAllLocalFiles));
            Assert.That(actual.SetupDefender, Is.EqualTo(expected.SetupDefender));
            Assert.That(actual.ScanOnly, Is.EqualTo(expected.ScanOnly));
            Assert.That(actual.NoMft, Is.EqualTo(expected.NoMft));
            Assert.That(actual.FilterPattern, Is.EqualTo(expected.FilterPattern));
            Assert.That(actual.PathsArgument, Is.EqualTo(expected.PathsArgument));
            Assert.That(actual.Summary, Is.EqualTo(expected.Summary));
            Assert.That(actual.Merge, Is.EqualTo(expected.Merge));
            Assert.That(actual.RefreshReport, Is.EqualTo(expected.RefreshReport));
            Assert.That(actual.Minified, Is.EqualTo(expected.Minified));
            Assert.That(actual.SavePath, Is.EqualTo(expected.SavePath));
            Assert.That(actual.CustomConfigurationPath, Is.EqualTo(expected.CustomConfigurationPath));
            Assert.That(actual.DbSize, Is.EqualTo(expected.DbSize));
            Assert.That(actual.AllBranches, Is.EqualTo(expected.AllBranches));
            Assert.That(actual.Watch, Is.EqualTo(expected.Watch));
            Assert.That(actual.NoLocalCommitCount, Is.EqualTo(expected.NoLocalCommitCount));
            Assert.That(actual.HasParseError, Is.EqualTo(expected.HasError));
            Assert.That(actual.ExitRequested, Is.EqualTo(expected.ExitRequested));
        });
    }
}

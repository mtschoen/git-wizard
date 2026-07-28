using System.Reflection;
using GitWizard.CLI;

namespace GitWizardTests;

/// <summary>
/// Verifies that the CLI's private runtime configuration copies every parser result.
/// </summary>
public class RunConfigurationAdapterTests
{
    static readonly Type RunConfigurationType =
        typeof(Program).GetNestedType("RunConfiguration", BindingFlags.NonPublic)!;

    static T ReadField<T>(object configuration, string fieldName) =>
        (T)RunConfigurationType
            .GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(configuration)!;

    [Test]
    public void Constructor_ProcessArguments_CopiesEveryParserResult()
    {
        var expected = CliParser.ParseProcessArgs(Environment.GetCommandLineArgs());
        var actual = Activator.CreateInstance(
            RunConfigurationType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: null,
            culture: null)!;

        Assert.Multiple(() =>
        {
            Assert.That(ReadField<bool>(actual, "RebuildRepositoryList"), Is.EqualTo(expected.RebuildRepositoryList));
            Assert.That(ReadField<bool>(actual, "RebuildReport"), Is.EqualTo(expected.RebuildReport));
            Assert.That(ReadField<bool>(actual, "ClearCache"), Is.EqualTo(expected.ClearCache));
            Assert.That(ReadField<bool>(actual, "DeleteAllLocalFiles"), Is.EqualTo(expected.DeleteAllLocalFiles));
            Assert.That(ReadField<bool>(actual, "SetupDefender"), Is.EqualTo(expected.SetupDefender));
            Assert.That(ReadField<bool>(actual, "ScanOnly"), Is.EqualTo(expected.ScanOnly));
            Assert.That(ReadField<bool>(actual, "NoMft"), Is.EqualTo(expected.NoMft));
            Assert.That(ReadField<string?>(actual, "FilterPattern"), Is.EqualTo(expected.FilterPattern));
            Assert.That(ReadField<string?>(actual, "PathsArgument"), Is.EqualTo(expected.PathsArgument));
            Assert.That(ReadField<bool>(actual, "Summary"), Is.EqualTo(expected.Summary));
            Assert.That(ReadField<bool>(actual, "Merge"), Is.EqualTo(expected.Merge));
            Assert.That(ReadField<bool>(actual, "RefreshReport"), Is.EqualTo(expected.RefreshReport));
            Assert.That(ReadField<bool>(actual, "Minified"), Is.EqualTo(expected.Minified));
            Assert.That(ReadField<string?>(actual, "SavePath"), Is.EqualTo(expected.SavePath));
            Assert.That(ReadField<string?>(actual, "CustomConfigurationPath"), Is.EqualTo(expected.CustomConfigurationPath));
            Assert.That(ReadField<bool>(actual, "DbSize"), Is.EqualTo(expected.DbSize));
            Assert.That(ReadField<bool>(actual, "AllBranches"), Is.EqualTo(expected.AllBranches));
            Assert.That(ReadField<bool>(actual, "Watch"), Is.EqualTo(expected.Watch));
            Assert.That(ReadField<bool>(actual, "NoLocalCommitCount"), Is.EqualTo(expected.NoLocalCommitCount));
            Assert.That(ReadField<bool>(actual, "HasParseError"), Is.EqualTo(expected.HasError));
            Assert.That(ReadField<bool>(actual, "ExitRequested"), Is.EqualTo(expected.ExitRequested));
        });
    }
}

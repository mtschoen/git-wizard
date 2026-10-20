using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

/// <summary>
/// Table-driven checks that each command line resolves to one <see cref="CommandKind"/>
/// and that a default <see cref="RunConfiguration"/> is the parse of no arguments.
/// </summary>
[NonParallelizable]
internal sealed class RunConfigurationCommandKindTests
{
    bool _originalVerboseMode;
    bool _originalSilentMode;
    Action<string?> _originalLogMethod = null!;

    [SetUp]
    public void SetUp()
    {
        _originalVerboseMode = GitWizardLog.VerboseMode;
        _originalSilentMode = GitWizardLog.SilentMode;
        _originalLogMethod = GitWizardLog.LogMethod;
        GitWizardLog.SilentMode = true;
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.VerboseMode = _originalVerboseMode;
        GitWizardLog.SilentMode = _originalSilentMode;
        GitWizardLog.LogMethod = _originalLogMethod;
    }

    [TestCase(new string[0], CommandKind.Report)]
    [TestCase(new[] { "--json" }, CommandKind.Report)]
    [TestCase(new[] { "--sweep" }, CommandKind.Sweep)]
    [TestCase(new[] { "tags" }, CommandKind.Tags)]
    [TestCase(new[] { "branches" }, CommandKind.Branches)]
    [TestCase(new[] { "--scan-only" }, CommandKind.ScanOnly)]
    [TestCase(new[] { "--merge" }, CommandKind.Merge)]
    [TestCase(new[] { "--watch" }, CommandKind.Watch)]
    [TestCase(new[] { "--db-size" }, CommandKind.DbSize)]
    [TestCase(new[] { "--clear-index-cache" }, CommandKind.ClearIndexCache)]
    [TestCase(new[] { "--index-cache-status" }, CommandKind.IndexCacheStatus)]
    [TestCase(new[] { "--help" }, CommandKind.HelpPrinted)]
    [TestCase(new[] { "--version" }, CommandKind.HelpPrinted)]
    [TestCase(new[] { "tags", "--help" }, CommandKind.HelpPrinted)]
    [TestCase(new[] { "branches", "-h" }, CommandKind.HelpPrinted)]
    [TestCase(new[] { "--sweep", "--merge" }, CommandKind.Sweep)]
    [TestCase(new[] { "--merge", "--scan-only" }, CommandKind.Merge)]
    [TestCase(new[] { "--scan-only", "--watch" }, CommandKind.ScanOnly)]
    [TestCase(new[] { "--watch", "--merge" }, CommandKind.Merge)]
    [TestCase(new[] { "--db-size", "--sweep" }, CommandKind.DbSize)]
    [TestCase(new[] { "--clear-index-cache", "--watch" }, CommandKind.ClearIndexCache)]
    [TestCase(new[] { "--index-cache-status", "--db-size" }, CommandKind.IndexCacheStatus)]
    [TestCase(new[] { "--clear-cache", "--no-refresh" }, CommandKind.Report)]
    [TestCase(new[] { "--delete-all-local-files" }, CommandKind.Report)]
    public void Parse_CommandLine_ResolvesOneCommandKind(string[] args, CommandKind expected)
    {
        Assert.That(CliParser.Parse(args).Command, Is.EqualTo(expected));
    }

    [Test]
    public void Parse_NoArguments_EqualsDefaultRunConfiguration()
    {
        Assert.That(CliParser.Parse([]), Is.EqualTo(new RunConfiguration()));
    }

    [Test]
    public void DefaultRunConfiguration_IsAReportThatRefreshes()
    {
        var configuration = new RunConfiguration();

        Assert.Multiple(() =>
        {
            Assert.That(configuration.Command, Is.EqualTo(CommandKind.Report));
            Assert.That(configuration.RefreshReport, Is.True);
            Assert.That(configuration.HasParseError, Is.False);
        });
    }

    [Test]
    public void Parse_UnknownFlag_ReportsParseErrorWithDefaultCommand()
    {
        var configuration = CliParser.Parse(["--no-such-flag"]);

        Assert.Multiple(() =>
        {
            Assert.That(configuration.HasParseError, Is.True);
            Assert.That(configuration.Command, Is.EqualTo(CommandKind.Report));
        });
    }

    [Test]
    public void Parse_ScanOnly_ImpliesRebuildRepositoryList()
    {
        Assert.That(CliParser.Parse(["--scan-only"]).RebuildRepositoryList, Is.True);
    }

    [TestCase(CommandKind.HelpPrinted)]
    [TestCase(CommandKind.IndexCacheStatus)]
    [TestCase(CommandKind.ClearIndexCache)]
    [TestCase(CommandKind.DbSize)]
    public void RunCommandCoreAsync_ImmediateExitCommand_IsRejected(CommandKind command)
    {
        var configuration = new RunConfiguration { Command = command };

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Program.RunCommandCoreAsync(configuration, new GitWizardConfiguration()));
    }
}

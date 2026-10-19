using GitWizard;
using GitWizard.CLI;

namespace GitWizardTests;

[TestFixture]
public class TagsAndBranchesCliTests
{
    bool _originalVerboseMode;

    [SetUp]
    public void SetUp()
    {
        _originalVerboseMode = GitWizardLog.VerboseMode;
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.VerboseMode = _originalVerboseMode;
    }

    [Test]
    public void Parse_TagsCommand_SetsDefaults()
    {
        var result = CliParser.Parse(["tags"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Tags));
            Assert.That(result.TagMatchPattern, Is.EqualTo("*"));
            Assert.That(result.HasParseError, Is.False);
            Assert.That(result.Command, Is.Not.EqualTo(CommandKind.HelpPrinted));
        });
    }

    [Test]
    public void Parse_TagsCommand_WithMatchOption()
    {
        var result = CliParser.Parse(["tags", "-m", "archive/*"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Tags));
            Assert.That(result.TagMatchPattern, Is.EqualTo("archive/*"));
            Assert.That(result.HasParseError, Is.False);
        });
    }

    [Test]
    public void Parse_TagsCommand_WithLongMatchOption()
    {
        var result = CliParser.Parse(["tags", "--match", "v1.*"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Tags));
            Assert.That(result.TagMatchPattern, Is.EqualTo("v1.*"));
            Assert.That(result.HasParseError, Is.False);
        });
    }

    [Test]
    public void Parse_TagsCommand_WithStandardOptions()
    {
        var result = CliParser.Parse(["tags", "-p", "/repo/one,/repo/two", "-f", "repo", "--print-minified", "--no-mft", "-v"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Tags));
            Assert.That(result.PathsArgument, Is.EqualTo("/repo/one,/repo/two"));
            Assert.That(result.FilterPattern, Is.EqualTo("repo"));
            Assert.That(result.Minified, Is.True);
            Assert.That(result.NoMft, Is.True);
            Assert.That(GitWizardLog.VerboseMode, Is.True);
        });
    }

    [Test]
    public void Parse_TagsCommand_HelpOption_RequestsExit()
    {
        var result = CliParser.Parse(["tags", "--help"]);

        Assert.That(result.Command, Is.EqualTo(CommandKind.HelpPrinted));
    }

    [Test]
    public void Parse_BranchesCommand_SetsDefaults()
    {
        var result = CliParser.Parse(["branches"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Branches));
            Assert.That(result.NotMergedOnly, Is.False);
            Assert.That(result.NotMergedBranch, Is.Null);
            Assert.That(result.HasParseError, Is.False);
            Assert.That(result.Command, Is.Not.EqualTo(CommandKind.HelpPrinted));
        });
    }

    [Test]
    public void Parse_BranchesCommand_WithNotMergedBranch()
    {
        var result = CliParser.Parse(["branches", "--not-merged", "main"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Branches));
            Assert.That(result.NotMergedOnly, Is.True);
            Assert.That(result.NotMergedBranch, Is.EqualTo("main"));
            Assert.That(result.HasParseError, Is.False);
        });
    }

    [Test]
    public void Parse_BranchesCommand_WithNotAlias()
    {
        var result = CliParser.Parse(["branches", "--not", "develop"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Branches));
            Assert.That(result.NotMergedOnly, Is.True);
            Assert.That(result.NotMergedBranch, Is.EqualTo("develop"));
            Assert.That(result.HasParseError, Is.False);
        });
    }

    [Test]
    public void Parse_BranchesCommand_WithStandardOptions()
    {
        var result = CliParser.Parse(["branches", "-p", "/path/a", "-f", "alpha", "--print-minified", "--no-mft"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Branches));
            Assert.That(result.PathsArgument, Is.EqualTo("/path/a"));
            Assert.That(result.FilterPattern, Is.EqualTo("alpha"));
            Assert.That(result.Minified, Is.True);
            Assert.That(result.NoMft, Is.True);
        });
    }

    [Test]
    public void Parse_BranchesCommand_HelpOption_RequestsExit()
    {
        var result = CliParser.Parse(["branches", "-h"]);

        Assert.That(result.Command, Is.EqualTo(CommandKind.HelpPrinted));
    }
}

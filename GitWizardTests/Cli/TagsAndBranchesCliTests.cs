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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Tags));
            Assert.That(result.TagMatchPattern, Is.EqualTo("*"));
            Assert.That(result.HasParseError, Is.False);
            Assert.That(result.Command, Is.Not.EqualTo(CommandKind.HelpPrinted));
        }
    }

    [TestCase("-m", "archive/*")]
    [TestCase("--match", "v1.*")]
    public void Parse_TagsCommand_MatchOptionSetsPattern(string matchOption, string pattern)
    {
        var result = CliParser.Parse(["tags", matchOption, pattern]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Tags));
            Assert.That(result.TagMatchPattern, Is.EqualTo(pattern));
            Assert.That(result.HasParseError, Is.False);
        }
    }

    [TestCase("--print-minified")]
    [TestCase("--minified")]
    public void Parse_TagsCommand_WithStandardOptions(string minifiedOption)
    {
        var result = CliParser.Parse(["tags", "-p", "/repo/one,/repo/two", "-f", "repo", minifiedOption, "--no-mft", "-v"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasParseError, Is.False);
            Assert.That(result.Command, Is.EqualTo(CommandKind.Tags));
            Assert.That(result.PathsArgument, Is.EqualTo("/repo/one,/repo/two"));
            Assert.That(result.FilterPattern, Is.EqualTo("repo"));
            Assert.That(result.Minified, Is.True);
            Assert.That(result.NoMft, Is.True);
            Assert.That(GitWizardLog.VerboseMode, Is.True);
        }
    }

    [TestCase("tags", "--help")]
    [TestCase("branches", "-h")]
    public void Parse_HelpOption_RequestsExit(string command, string helpOption)
    {
        var result = CliParser.Parse([command, helpOption]);

        Assert.That(result.Command, Is.EqualTo(CommandKind.HelpPrinted));
    }

    [Test]
    public void Parse_BranchesCommand_SetsDefaults()
    {
        var result = CliParser.Parse(["branches"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Branches));
            Assert.That(result.NotMergedOnly, Is.False);
            Assert.That(result.NotMergedBranch, Is.Null);
            Assert.That(result.HasParseError, Is.False);
            Assert.That(result.Command, Is.Not.EqualTo(CommandKind.HelpPrinted));
        }
    }

    [TestCase("--not-merged", "main")]
    [TestCase("--not", "develop")]
    public void Parse_BranchesCommand_NotMergedOptionSetsBranch(string notMergedOption, string branch)
    {
        var result = CliParser.Parse(["branches", notMergedOption, branch]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Command, Is.EqualTo(CommandKind.Branches));
            Assert.That(result.NotMergedOnly, Is.True);
            Assert.That(result.NotMergedBranch, Is.EqualTo(branch));
            Assert.That(result.HasParseError, Is.False);
        }
    }

    [TestCase("--print-minified")]
    [TestCase("--minified")]
    public void Parse_BranchesCommand_WithStandardOptions(string minifiedOption)
    {
        var result = CliParser.Parse(["branches", "-p", "/path/a", "-f", "alpha", minifiedOption, "--no-mft"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasParseError, Is.False);
            Assert.That(result.Command, Is.EqualTo(CommandKind.Branches));
            Assert.That(result.PathsArgument, Is.EqualTo("/path/a"));
            Assert.That(result.FilterPattern, Is.EqualTo("alpha"));
            Assert.That(result.Minified, Is.True);
            Assert.That(result.NoMft, Is.True);
        }
    }
}

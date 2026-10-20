namespace GitWizardTests;

using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GitWizard.CLI;

[NonParallelizable]
public class AgentInstructionsTests
{
    static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "git-wizard.slnx")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot locate git-wizard.slnx.");
        }
    }

    static string Read(string path) => File.ReadAllText(Path.Combine(RepositoryRoot, path));

    [Test]
    public void Instructions_StayWithinDocumentAndBlockBudgets()
    {
        var instructions = Read("AGENTS.md");
        Assert.Multiple(() =>
        {
            Assert.That(instructions.EnumerateRunes().Count(), Is.LessThan(15000));
            foreach (var block in Regex.Split(instructions, @"\r?\n[\t ]*\r?\n"))
                Assert.That(block.EnumerateRunes().Count(), Is.LessThan(3000), block);
        });
    }

    [Test]
    public void Instructions_LinkToExistingDocumentation()
    {
        var links = Regex.Matches(Read("AGENTS.md"), @"\]\((docs/[^)#]+\.md)(?:#[^)]*)?\)");
        Assert.That(links, Is.Not.Empty);
        foreach (Match link in links)
            Assert.That(File.Exists(Path.Combine(RepositoryRoot, link.Groups[1].Value)), Is.True, link.Value);
    }

    [TestCase("")]
    [TestCase("tags")]
    [TestCase("branches")]
    public void DocumentedFlags_CoverParserAndHelp(string command)
    {
        var documented = Read("docs/cli-options.md");
        var help = Capture(command.Length == 0 ? ["--help"] : [command, "--help"]);
        var parser = Read("git-wizard/CliParser.cs") + Read("git-wizard/CliParser.Subcommands.cs");
        var flags = Regex.Matches(help, @"--[a-z][a-z-]*")
            .Concat(Regex.Matches(parser, @"--[a-z][a-z-]*"))
            .Select(match => match.Value).Distinct();
        Assert.Multiple(() =>
        {
            foreach (var flag in flags)
                Assert.That(documented, Does.Contain($"`{flag}`"), flag);
        });
    }

    [Test]
    public void CliAndDesktopVersions_MatchSharedBuildProperty()
    {
        var properties = XDocument.Parse(Read("Directory.Build.props"));
        var version = properties.Descendants("Version").Single().Value;
        Assert.Multiple(() =>
        {
            Assert.That(Capture(["--version"]).Trim(), Is.EqualTo(version));
            foreach (var assembly in new[] { typeof(RunConfiguration).Assembly, typeof(GitWizardUI.App).Assembly })
            {
                var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
                Assert.That(informational.Split('+')[0], Is.EqualTo(version));
            }
            Assert.That(XDocument.Parse(Read("GitWizardUI/GitWizardUI.csproj")).Descendants("Version"), Is.Empty);
            Assert.That(XDocument.Parse(Read("git-wizard/git-wizard.csproj")).Descendants("Version"), Is.Empty);
        });
    }

    static string Capture(string[] arguments)
    {
        using var output = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            Assert.That(CliParser.Parse(arguments).Command, Is.EqualTo(CommandKind.HelpPrinted));
            return output.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}

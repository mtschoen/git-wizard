namespace GitWizardTests.TestSupport;

using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using GitWizard.CLI;
using NUnit.Framework.Interfaces;

[NonParallelizable]
public class AgentInstructionsTests
{
    static string Read(string path) => File.ReadAllText(Path.Combine(SourceTree.RepositoryRoot, path));

    [Test]
    public void Instructions_StayWithinDocumentAndBlockBudgets()
    {
        var instructions = Read("AGENTS.md");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(instructions.EnumerateRunes().Count(), Is.LessThan(15000));
            foreach (var block in Regex.Split(instructions, @"\r?\n[\t ]*\r?\n"))
            {
                Assert.That(block.EnumerateRunes().Count(), Is.LessThan(3000), block);
            }
        }
    }

    [Test]
    public void Instructions_LinkToExistingDocumentation()
    {
        var links = Regex.Matches(Read("AGENTS.md"), @"\]\((docs/[^)#]+\.md)(?:#[^)]*)?\)");
        Assert.That(links, Is.Not.Empty);
        foreach (Match link in links)
        {
            Assert.That(File.Exists(Path.Combine(SourceTree.RepositoryRoot, link.Groups[1].Value)), Is.True, link.Value);
        }
    }

    [TestCase("")]
    [TestCase("tags")]
    [TestCase("branches")]
    public void DocumentedFlags_CoverParserAndHelp(string command)
    {
        var documented = Read("docs/cli-options.md");
        var help = Capture(command.Length == 0 ? ["--help"] : [command, "--help"]);
        var parser = Read("git-wizard/CliParser.cs") + Read("git-wizard/CliParser.Subcommands.cs");
        var flags = Regex.Matches(help, "--[a-z][a-z-]*")
            .Concat(Regex.Matches(parser, "--[a-z][a-z-]*"))
            .Select(match => match.Value).Distinct();
        using (Assert.EnterMultipleScope())
        {
            foreach (var flag in flags)
            {
                Assert.That(documented, Does.Contain($"`{flag}`"), flag);
            }
        }
    }

    [Test]
    public void CliAndDesktopVersions_MatchSharedBuildProperty()
    {
        var properties = XDocument.Parse(Read("Directory.Build.props"));
        var version = properties.Descendants("Version").Single().Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Capture(["--version"]).Trim(), Is.EqualTo(version));
            foreach (var assembly in new[] { typeof(RunConfiguration).Assembly, typeof(GitWizardUI.App).Assembly })
            {
                var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
                Assert.That(informational.Split('+')[0], Is.EqualTo(version));
            }
            Assert.That(XDocument.Parse(Read("GitWizardUI/GitWizardUI.csproj")).Descendants("Version"), Is.Empty);
            Assert.That(XDocument.Parse(Read("git-wizard/git-wizard.csproj")).Descendants("Version"), Is.Empty);
        }
    }

    [Test]
    public void NamedTestClassesExist()
    {
        var text = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "AGENTS.md"));
        var names = typeof(AgentInstructionsTests).Assembly.GetTypes()
            .Where(type => type.IsClass && type.GetMethods().Any(method =>
                method.GetCustomAttributes().Any(attribute =>
                    attribute is ITestBuilder or ISimpleTestBuilder)))
            .Select(type => type.Name);

        Assert.That(MissingTestClasses(text, names), Is.Empty,
            "AGENTS.md must name existing fixtures; update references with fixture renames.");
    }

    [TestCase("`ExistingTests` and `ExistingTests.Case`", "")]
    [TestCase("`MissingFixtureTests`", "MissingFixtureTests")]
    [TestCase("`RefreshConcurrencyTest`", "RefreshConcurrencyTest")]
    [TestCase("GitWizardTests uses VSTest", "")]
    public void MissingNamesAreReported(string text, string expected)
    {
        Assert.That(string.Join(",", MissingTestClasses(text, ["ExistingTests"])), Is.EqualTo(expected));
    }

    static string[] MissingTestClasses(string text, IEnumerable<string> fixtureNames)
    {
        var known = fixtureNames.ToHashSet(StringComparer.Ordinal);
        return Regex.Matches(text, @"\b[A-Z][A-Za-z0-9]*Tests?\b", RegexOptions.None,
                TimeSpan.FromSeconds(1))
            .Select(match => match.Value)
            .Where(name => name is not "GitWizardTests" and not "VSTest" && !known.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
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

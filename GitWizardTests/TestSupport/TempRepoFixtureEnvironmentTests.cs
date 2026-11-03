using LibGit2Sharp;

namespace GitWizardTests.TestSupport;

[TestFixture, NonParallelizable]
sealed class TempRepoFixtureEnvironmentTests
{
    static readonly string[] OverrideNames =
    [
        "GIT_CONFIG_COUNT",
        "GIT_CONFIG_KEY_0",
        "GIT_CONFIG_VALUE_0",
        "GIT_CONFIG_PARAMETERS",
    ];

    readonly Dictionary<string, string?> _savedValues = [];

    [SetUp]
    public void SaveEnvironment()
    {
        foreach (var name in OverrideNames)
        {
            _savedValues[name] = Environment.GetEnvironmentVariable(name);
        }
    }

    [TearDown]
    public void RestoreEnvironment()
    {
        foreach (var (name, value) in _savedValues)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Test]
    public void AddOriginRemoteAndPush_IgnoresInheritedGitConfigOverrides()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "remote.origin.pushurl");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "https://127.0.0.1:1/unreachable.git");

        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        fixture.AddOriginRemoteAndPush();

        string originUrl;
        string branch;
        using (var repository = new Repository(fixture.Path))
        {
            originUrl = repository.Network.Remotes["origin"].Url;
            branch = repository.Head.FriendlyName;
        }

        using var origin = new Repository(originUrl);
        Assert.That(origin.Branches[branch], Is.Not.Null, "push must reach the local bare origin");
    }
}

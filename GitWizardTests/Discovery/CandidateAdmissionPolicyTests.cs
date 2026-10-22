using GitWizard;
using GitWizard.Watch;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Discovery;

/// <summary>
/// One table of admission cases runs through the recursive walk, the indexed candidate filter and
/// replay. Every route must admit exactly the same repositories.
/// </summary>
[TestFixture]
[NonParallelizable]
public class CandidateAdmissionPolicyTests
{
    public sealed record AdmissionCase(
        string Name,
        string Repository,
        string? IgnoredRelative,
        bool SkipHiddenDirectories,
        string? HiddenAttributeDirectory,
        bool Expected,
        bool IgnoreVolumeRoot = false,
        bool IncludeTemporary = true,
        string[]? TemporaryRelative = null)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<AdmissionCase> Cases()
    {
        yield return new("ordinary repository", "repo", null, true, null, true);
        yield return new("ignored root itself", "", "", false, null, false);
        yield return new("ignored ancestor", "ignored/repo", "ignored", false, null, false);
        yield return new("ignored sibling leaves repository", "repo", "ignored", false, null, true);
        yield return new("dot-prefixed ancestor skipped", ".dot/repo", null, true, null, false);
        yield return new("dot-prefixed ancestor kept", ".dot/repo", null, false, null, true);
        yield return new("hidden attribute ancestor skipped", "plain/repo", null, true, "plain", false);
        yield return new("hidden attribute ancestor kept", "plain/repo", null, false, "plain", true);
        yield return new("temporary directory excluded", "tmp/repo", null, false, null, false,
            IncludeTemporary: false, TemporaryRelative: ["tmp"]);
        yield return new("temporary directory globally included", "tmp/repo", null, false, null, true,
            IncludeTemporary: true, TemporaryRelative: ["tmp"]);
        yield return new("temporary sibling leaves repository", "repo", null, false, null, true,
            IncludeTemporary: false, TemporaryRelative: ["tmp"]);
        yield return new("temporary search root opts in", "repo", null, false, null, true,
            IncludeTemporary: false, TemporaryRelative: [""]);
        yield return new("temporary search root opts in for nested repository", "plain/repo", null, false, null, true,
            IncludeTemporary: false, TemporaryRelative: [""]);
        yield return new("ignored volume root", "repo", null, false, null, false, IgnoreVolumeRoot: true);
        yield return new("ignored volume root with nested repository", "plain/repo", null, true, null, false, IgnoreVolumeRoot: true);
    }

    [TestCaseSource(nameof(Cases))]
    public async Task EveryRouteAdmitsTheSameRepositories(AdmissionCase admissionCase)
    {
        if (admissionCase.HiddenAttributeDirectory is not null && !OperatingSystem.IsWindows())
            Assert.Ignore("The Hidden file attribute exists only on Windows.");

        using var fixture = new PackedIndexFixture();
        var scopePath = Path.Combine(fixture.TreePath, "scope");
        fixture.AddMarker(Path.Combine("scope", admissionCase.Repository.Replace('/', Path.DirectorySeparatorChar)));
        if (admissionCase.HiddenAttributeDirectory is { } hiddenDirectory)
        {
            var directory = Path.Combine(scopePath, hiddenDirectory);
            File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
        }

        var ignoredPath = IgnoredPathFor(admissionCase, scopePath);
        var repositoryPath = Path.Combine(scopePath, admissionCase.Repository.Replace('/', Path.DirectorySeparatorChar));
        var configuration = new GitWizardConfiguration
        {
            IncludeTempDirectories = admissionCase.IncludeTemporary,
            SkipHiddenDirectories = admissionCase.SkipHiddenDirectories
        };
        if (ignoredPath is not null)
            configuration.IgnoredPaths.Add(ignoredPath);

        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, true, []);
        var temporaryRoots = (admissionCase.TemporaryRelative ?? [])
            .SelectMany(relative => new[]
            {
                Path.Combine(scopePath, relative.Replace('/', Path.DirectorySeparatorChar)),
                ("X:/scope/" + relative).TrimEnd('/')
            })
            .Select(WatchPath.Parse)
            .ToArray();
        TempDirectories.IsTempPathOverride = path =>
        {
            var parsed = WatchPath.Parse(path);
            return temporaryRoots.Any(parsed.IsUnderOrEqual);
        };

        try
        {
            await AssertEveryRouteAsync(admissionCase, fixture, request, scopePath, ignoredPath, repositoryPath);
        }
        finally
        {
            TempDirectories.IsTempPathOverride = null;
        }
    }

    static async Task AssertEveryRouteAsync(
        AdmissionCase admissionCase, PackedIndexFixture fixture, DiscoveryRequest request,
        string scopePath, string? ignoredPath, string repositoryPath)
    {
        var walk = EnumeratedDiscovery.Run(request, scopePath).Paths
            .Any(path => SamePath(path, repositoryPath));

        await using var index = await fixture.OpenIndexAsync();
        var logicalIgnored = LogicalIgnoredFor(admissionCase, fixture.TreePath, ignoredPath);
        var indexed = CandidateSelection
            .Paths(index, "X:/scope", logicalIgnored, admissionCase.SkipHiddenDirectories, admissionCase.IncludeTemporary)
            .Any(path => SamePath(path, repositoryPath));

        var scope = DiscoveryScope.CreateExpanded(
            request, [scopePath], [], ignoredPath is null ? [] : [ignoredPath],
            root => new RootClassification(root, RootFileSystem.Ntfs, "X", null));
        var replay = CandidateAdmissionPolicy.AdmitsInventoryCandidate(scope, repositoryPath);
        var linuxRetainedRoots = (await LinuxRetainedRootAdmissionTests.InventoryAsync(
                scopePath, repositoryPath, ignoredPath, admissionCase.SkipHiddenDirectories, admissionCase.IncludeTemporary))
            .Any(path => SamePath(path, repositoryPath));

        Assert.Multiple(() =>
        {
            Assert.That(walk, Is.EqualTo(admissionCase.Expected), "recursive walk");
            Assert.That(indexed, Is.EqualTo(admissionCase.Expected), "indexed candidate filter");
            Assert.That(replay, Is.EqualTo(admissionCase.Expected), "replay");
            Assert.That(linuxRetainedRoots, Is.EqualTo(admissionCase.Expected), "Linux retained roots");
        });
    }

    public sealed record SyntaxCase(
        string Name,
        string SearchRoot,
        string Candidate,
        string? IgnoredRoot,
        bool IncludeTempDirectories,
        bool Expected)
    {
        public override string ToString() => Name;
    }

    public static IEnumerable<SyntaxCase> SyntaxCases()
    {
        yield return new("Windows ignored volume root with trailing separator", "C:\\", @"C:\repos\repo", "C:\\", false, false);
        yield return new("Windows ignored volume root with slash", "C:\\", @"C:\repos\repo", "C:/", false, false);
        yield return new("Windows ignored volume root keeps another drive", "D:\\", @"D:\repos\repo", "C:\\", false, true);
        yield return new("POSIX ignored volume root", "/", "/repos/repo", "/", false, false);
        yield return new("POSIX ignored volume root repository at the root", "/", "/repo", "/", false, false);
        yield return new("POSIX ignored sibling", "/", "/repos/repo", "/other", false, true);
        yield return new("Windows temporary directory under a volume root", "C:\\", @"C:\Users\someone\AppData\Local\Temp\repo", null, false, false);
        yield return new("Windows temporary directory included", "C:\\", @"C:\Users\someone\AppData\Local\Temp\repo", null, true, true);
        yield return new("Windows temporary search root admits its repositories", @"C:\Users\someone\AppData\Local\Temp", @"C:\Users\someone\AppData\Local\Temp\repo", null, false, true);
        yield return new("POSIX temporary directory under a volume root", "/", "/tmp/repo", null, false, false);
        yield return new("POSIX temporary directory included", "/", "/tmp/repo", null, true, true);
        yield return new("POSIX temporary search root admits its repositories", "/tmp", "/tmp/repo", null, false, true);
        yield return new("POSIX ordinary repository", "/", "/repos/repo", null, false, true);
    }

    [TestCaseSource(nameof(SyntaxCases))]
    public void EveryPathSyntaxAdmitsTheSameRepositories(SyntaxCase syntaxCase)
    {
        var configuration = new GitWizardConfiguration { IncludeTempDirectories = syntaxCase.IncludeTempDirectories };
        var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, true, []);
        var scope = DiscoveryScope.CreateExpanded(
            request, [syntaxCase.SearchRoot], [], syntaxCase.IgnoredRoot is null ? [] : [syntaxCase.IgnoredRoot],
            root => new RootClassification(root, RootFileSystem.Recursive, null, null));
        var policy = CandidateAdmissionPolicy.ForScope(scope);
        var searchRoot = WatchPath.Parse(syntaxCase.SearchRoot);
        var candidate = WatchPath.Parse(syntaxCase.Candidate);

        Assert.Multiple(() =>
        {
            Assert.That(policy.AdmitsWithin(searchRoot, candidate), Is.EqualTo(syntaxCase.Expected), "walk");
            Assert.That(
                CandidateAdmissionPolicy.AdmitsInventoryCandidate(scope, syntaxCase.Candidate),
                Is.EqualTo(syntaxCase.Expected),
                "replay and Linux retained roots");
        });
    }

    [TestCase(@"C:\", @"C:\repos\repo", true)]
    [TestCase("C:/", @"C:\repos", true)]
    [TestCase(@"C:\", @"D:\repos", false)]
    [TestCase("/", "/repos/repo", true)]
    [TestCase("/repos", "/repos2", false)]
    public void SharedPathPredicateAcceptsSeparatorTerminatedRoots(string root, string path, bool expected) =>
        Assert.That(GitWizardApi.IsSameOrChildPath(path, root), Is.EqualTo(expected));

    static string? IgnoredPathFor(AdmissionCase admissionCase, string scopePath)
    {
        if (admissionCase.IgnoreVolumeRoot)
            return Path.GetPathRoot(scopePath);

        return admissionCase.IgnoredRelative is null
            ? null
            : Path.Combine(scopePath, admissionCase.IgnoredRelative.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar);
    }

    static string[] LogicalIgnoredFor(AdmissionCase admissionCase, string treePath, string? ignoredPath)
    {
        if (ignoredPath is null)
            return [];

        return admissionCase.IgnoreVolumeRoot ? ["X:/"] : [ToLogical(treePath, ignoredPath)];
    }

    static string ToLogical(string treePath, string path) =>
        "X:/" + Path.GetRelativePath(treePath, path).Replace('\\', '/');

    static bool SamePath(string left, string right) =>
        string.Equals(
            left.Replace('\\', '/').TrimEnd('/'), right.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

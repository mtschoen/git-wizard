using GitWizard;
using GitWizard.Watch;

namespace GitWizardTests;

// Compares the filter's tracked-root lookup with a plain scan over every tracked root, the
// definition of the longest-root, drive-root, path-syntax and .git rules the lookup must keep.
public class RepositoryChangeFilterRootLookupTests
{
    static readonly string[] RootPool =
    [
        @"C:\", "C:", @"C:\repos", @"C:\repos\repo-a", @"C:\repos\repo-a\sub", @"C:\Repos\Repo-B", @"C:\repos\repo-c",
        @"D:\work\x", @"D:\work\x\y", @"\server\share\r", @"\server\share", @"\", @"\data\r",
        "/", "/srv/repos/repo", "/srv/repos/repo/nested", "/srv/Repos/Repo", "/srv/a", "/srv/a b/c"
    ];

    static readonly string[] Segments =
        [".git", ".GIT", "src", "Src", "objects", "bin", "Debug", "file.obj", "x", "sub", "repo-a", "REPO-A", ".gitignore", "a b"];

    static string Join(string root, string[] parts, bool windows)
    {
        var separator = windows ? @"\" : "/";
        var text = root.TrimEnd('\\', '/');
        return parts.Length == 0 ? root : text + separator + string.Join(separator, parts);
    }

    static List<string> GeneratePaths(Random random, IReadOnlyList<string> roots)
    {
        var paths = new List<string>();
        foreach (var root in roots.Concat(RootPool))
        {
            var windows = !root.StartsWith('/');
            paths.Add(root);
            paths.Add(Join(root, [".git"], windows));
            paths.Add(Join(root, [".GIT"], windows));
            for (var index = 0; index < 12; index++)
            {
                var parts = Enumerable.Range(0, random.Next(1, 5)).Select(_ => Segments[random.Next(Segments.Length)]).ToArray();
                paths.Add(Join(root, parts, windows));
                paths.Add(Join(root, parts, windows).ToUpperInvariant());
            }
        }
        paths.AddRange([@"C:\", @"C:\x", "C:", @"C:\repos\repo-a2", @"C:\repos\repo-a\.git\HEAD", "/", "/x", "relative", string.Empty]);
        return paths;
    }

    static WatchPath? ReferenceContaining(IEnumerable<WatchPath> roots, WatchPath path) =>
        roots.Where(path.IsUnderOrEqual).OrderByDescending(root => root.Comparison.Length).Cast<WatchPath?>().FirstOrDefault();

    static WatchPath? ReferenceDeleted(IEnumerable<WatchPath> roots, WatchPath path) =>
        roots.OrderByDescending(root => root.Comparison.Length)
            .Where(root => path.IsEqualTo(root) || path.IsEqualToChildOf(root, ".git")).Cast<WatchPath?>().FirstOrDefault();

    static (RepositoryChangeKind, string)? Expected(IReadOnlyCollection<WatchPath> roots, VolumeChangeEntry entry)
    {
        var path = WatchPath.Parse(entry.FullPath);
        switch (entry.Kind)
        {
            case VolumeEntryKind.Deleted:
                if (ReferenceDeleted(roots, path) is { } deleted)
                {
                    return (RepositoryChangeKind.Deleted, deleted.Original);
                }
                break;
        }

        return ReferenceContaining(roots, path) is { } root ? (RepositoryChangeKind.Changed, root.Original) : null;
    }

    static void AssertEquivalent(RepositoryChangeFilter filter, IReadOnlyCollection<WatchPath> roots, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            foreach (var kind in new[] { VolumeEntryKind.Modified, VolumeEntryKind.Created, VolumeEntryKind.Deleted })
            {
                var entry = new VolumeChangeEntry(path, kind);
                Assert.That(filter.ClassifyEntry(entry), Is.EqualTo(Expected(roots, entry)), $"{kind} {path}");
            }
        }
    }

    [TestCase(577)]
    [TestCase(1)]
    public void ClassifyEntry_MatchesAScanOverEveryTrackedRoot(int seed)
    {
        var random = new Random(seed);
        var chosen = RootPool.Where(_ => random.Next(3) != 0).ToList();
        var roots = new Dictionary<string, WatchPath>(WatchPath.StringComparer);
        foreach (var root in chosen)
        {
            roots[root] = WatchPath.Parse(root);
        }
        var filter = new RepositoryChangeFilter([.. chosen], [], null, true);
        var paths = GeneratePaths(random, chosen);

        AssertEquivalent(filter, roots.Values, paths);

        foreach (var removed in chosen.Where(_ => random.Next(2) == 0).ToList())
        {
            filter.RemoveRepositoryRoot(removed);
            roots.Remove(removed);
        }
        foreach (var added in RootPool.Where(_ => random.Next(4) == 0))
        {
            filter.AddRepositoryRoot(added);
            roots.TryAdd(added, WatchPath.Parse(added));
        }

        AssertEquivalent(filter, roots.Values, paths);
    }
}

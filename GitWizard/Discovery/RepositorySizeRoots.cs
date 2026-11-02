using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard.Discovery;

/// <summary>Names why an index root is not a complete snapshot and tells which repositories a root covers.</summary>
static class RepositorySizeRoots
{
    public static string DescribeIncompleteFields(RootStatus status)
    {
        var fields = new List<string>();
        if (status.State != RootState.Ready)
        {
            fields.Add($"State={status.State}");
        }

        if (status.Producer != ProducerKind.Mft)
        {
            fields.Add($"Producer={status.Producer?.ToString() ?? "none"}");
        }

        if (status.Block is { IsCapacityExhausted: true })
        {
            fields.Add("capacity exhausted");
        }

        if (status.Block is { AccessDeniedSubtreeCount: > 0 and var denied })
        {
            fields.Add($"AccessDeniedSubtreeCount={denied}");
        }

        return fields.Count == 0 ? "unspecified" : string.Join(", ", fields);
    }

    // A root is the index root directory (such as the C drive root), or just a volume letter when the index could not name its root.
    public static bool Covers(string root, string path)
    {
        if (root.Length == 1)
        {
            return path.Length >= 2 && path[1] == ':'
                && char.ToUpperInvariant(path[0]) == char.ToUpperInvariant(root[0]);
        }

        return WatchPath.Parse(path).IsUnderOrEqual(WatchPath.Parse(root));
    }
}

/// <summary>An index root that is not a complete snapshot, the fields that say so, and how many repositories it explains.</summary>
sealed class IncompleteSizeRoot(string root, string reasons)
{
    public string Root { get; } = root;
    public string Reasons { get; } = reasons;
    public int Repositories { get; set; }
}

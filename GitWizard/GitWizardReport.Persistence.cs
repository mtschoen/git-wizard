using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitWizard;

public partial class GitWizardReport
{
    /// <summary>
    /// Targeted single-repo merge refresh used by the CLI <c>-merge</c> flag (issue #42).
    /// Reads the existing report at <paramref name="savePath"/> if present (otherwise starts
    /// from an empty report), refreshes only the supplied <paramref name="repositoryPaths"/>,
    /// upserts those entries into the existing report's repository collection by path key -
    /// leaving every other entry intact - stamps <see cref="CurrentSchemaVersion"/> on the
    /// whole report, and writes it back atomically (temp file in the same directory followed
    /// by an overwriting rename) so a concurrent reader never observes a half-written file.
    /// </summary>
    /// <remarks>
    /// Concurrency: two callers merging disjoint repos race on the file as a whole - last
    /// writer wins (a merge that started from a now-stale snapshot will clobber the other
    /// caller's entries). This is acceptable for the projdash fallback use case; there is no
    /// lockfile. See <c>docs/report-schema.md</c>.
    /// </remarks>
    /// <param name="savePath">Path to the report JSON to read and write.</param>
    /// <param name="configuration">Configuration used to refresh the supplied repos.</param>
    /// <param name="repositoryPaths">The repository paths to refresh and upsert.</param>
    /// <param name="updateHandler">Optional handler for progress updates.</param>
    /// <param name="allBranches">When true, include all branches in each refreshed repo.</param>
    /// <param name="computeLocalCommitCount">When false, skip the expensive per-branch local-commit-count iteration.</param>
    /// <returns>The merged report (also written to <paramref name="savePath"/>).</returns>
    public static GitWizardReport MergeIntoFile(string savePath, GitWizardConfiguration configuration,
        ICollection<string> repositoryPaths, IUpdateHandler? updateHandler = null, bool allBranches = false,
        bool computeLocalCommitCount = true)
    {
        // Refresh just the supplied repos in an isolated report so we get fresh entries for them
        // without disturbing anything else (Refresh prunes deleted paths and would otherwise
        // touch the whole collection, so it must run on a throwaway report - not the on-disk one).
        // CA2000 wants this owned report disposed, but GitWizardReport is IAsyncDisposable only
        // and MergeIntoFile is synchronous, so honouring it would mean blocking on the disposal
        // task - which the sync-over-async gate rejects, and rightly. Disposal exists solely to
        // tear down a discovery session; this path never opens one, so there is nothing to
        // release and the object is ordinary garbage once the method returns.
#pragma warning disable CA2000
        var refreshed = new GitWizardReport(configuration);
#pragma warning restore CA2000
        if (repositoryPaths.Count > 0)
            refreshed.Refresh(repositoryPaths, updateHandler, allBranches: allBranches, computeLocalCommitCount: computeLocalCommitCount);

        // Read the existing report as a JSON DOM rather than deserializing into GitWizardReport.
        // Deserialize-then-reserialize would drop every `private set` field (e.g. CurrentBranch)
        // on the UNTOUCHED entries - defeating the "leave other entries intact" contract (#42).
        // Keeping untouched entries as their original JsonNode preserves them byte-for-byte.
        var root = ReadJsonObjectFromFile(savePath) ?? new JsonObject();

        if (root[nameof(Repositories)] is not JsonObject repositories)
        {
            repositories = new JsonObject();
            root[nameof(Repositories)] = repositories;
        }

        // Upsert ONLY the refreshed repos by path key; every other entry's JsonNode is untouched.
        foreach (var kvp in refreshed.Repositories)
            repositories[kvp.Key] = JsonSerializer.SerializeToNode(kvp.Value, SerializerOptions);

        root[nameof(SchemaVersion)] = CurrentSchemaVersion;

        var jsonText = root.ToJsonString(SerializerOptions);
        WriteAtomic(savePath, jsonText);

        // Build the returned view: deserialize the merged DOM (gives every entry, though
        // private-set fields on the UNTOUCHED entries read back null - a pre-existing limitation
        // of reading a report into typed objects), then overlay the freshly-refreshed repos so
        // the RETURNED target entries carry their full in-memory state (e.g. CurrentBranch). The
        // ON-DISK file (the #42 contract surface) still preserved untouched entries verbatim.
        var result = JsonSerializer.Deserialize<GitWizardReport>(jsonText)
                     ?? new GitWizardReport(configuration);
        foreach (var kvp in refreshed.Repositories)
            result.Repositories[kvp.Key] = kvp.Value;
        return result;
    }

    static JsonObject? ReadJsonObjectFromFile(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var handle = File.OpenHandle(path);
            var length = checked((int)RandomAccess.GetLength(handle));
            var buffer = new byte[length];
            var totalRead = 0;
            while (totalRead < length)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(totalRead), totalRead);
                if (read == 0)
                    break;
                totalRead += read;
            }
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, totalRead);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception,
                $"Failed to read/parse report at {path}; starting merge from an empty report.");
            return null;
        }
    }

    public void Save(string path)
    {
        if (DiscoveryInventory is { CanPublishFullInventory: false })
            throw new InvalidOperationException("Incomplete discovery report cannot be saved.");

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        try
        {
            SchemaVersion = CurrentSchemaVersion;
            var snapshot = CreateSnapshotForSerialization();
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, SerializerOptions));
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Failed to save report to path: {path}.");
        }
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        if (DiscoveryInventory is { CanPublishFullInventory: false })
            throw new InvalidOperationException("Incomplete discovery report cannot be saved.");

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        try
        {
            SchemaVersion = CurrentSchemaVersion;
            var snapshot = CreateSnapshotForSerialization();
            var jsonText = JsonSerializer.Serialize(snapshot, SerializerOptions);
            await File.WriteAllTextAsync(path, jsonText, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Failed to save report to path: {path}.");
        }
    }

    /// <summary>
    /// Builds a throwaway <see cref="GitWizardReport"/> that shares this report's scalar and
    /// set-valued state but carries a <see cref="SortedDictionary{TKey,TValue}"/> copy of
    /// <see cref="Repositories"/> taken under <c>lock (Repositories)</c>. Used by
    /// <see cref="Save"/>, <see cref="SaveAsync"/>, and <see cref="SaveAtomic"/> so that
    /// serialization walks a private, immutable copy of the dictionary instead of the live
    /// one, which an in-flight orphan refresh from a concurrent Refresh call could otherwise
    /// still be mutating (see <see cref="RefreshSingleRepository"/>,
    /// <see cref="PruneDeletedRepositories"/>, <see cref="PruneNonRepositoryPaths"/>).
    /// </summary>
    GitWizardReport CreateSnapshotForSerialization()
    {
        SortedDictionary<string, GitWizardRepository> repositoriesSnapshot;
        lock (Repositories)
        {
            repositoriesSnapshot = new SortedDictionary<string, GitWizardRepository>(Repositories);
        }

        return new GitWizardReport
        {
            SchemaVersion = SchemaVersion,
            BranchScope = BranchScope,
            SearchPaths = SearchPaths,
            IgnoredPaths = IgnoredPaths,
            SkipHiddenDirectories = SkipHiddenDirectories,
            IncludeTempDirectories = IncludeTempDirectories,
            Repositories = repositoriesSnapshot,
            DeletedPaths = DeletedPaths,
            NonRepositoryPaths = NonRepositoryPaths,
        };
    }

    /// <summary>
    /// Write the report to <paramref name="path"/> atomically: serialize to a temp file in the
    /// same directory, then rename it over the destination. The rename is atomic on a single
    /// volume, so a concurrent reader sees either the old file or the new one - never a
    /// partially-written file. Stamps <see cref="CurrentSchemaVersion"/> before writing.
    /// </summary>
    public void SaveAtomic(string path)
    {
        if (DiscoveryInventory is { CanPublishFullInventory: false })
            throw new InvalidOperationException("Incomplete discovery report cannot be saved.");

        SchemaVersion = CurrentSchemaVersion;
        var snapshot = CreateSnapshotForSerialization();
        WriteAtomic(path, JsonSerializer.Serialize(snapshot, SerializerOptions));
    }

    /// <summary>
    /// Write <paramref name="jsonText"/> to <paramref name="path"/> atomically: serialize to a
    /// temp file in the same directory, then rename it over the destination. The rename is atomic
    /// on a single volume, so a concurrent reader sees either the old file or the new one - never
    /// a partially-written file. Shared by <see cref="SaveAtomic"/> and <see cref="MergeIntoFile"/>.
    /// </summary>
    static void WriteAtomic(string path, string jsonText)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        // Temp file in the SAME directory as the destination so File.Move stays on one volume
        // (cross-volume moves are a copy+delete and lose atomicity).
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(jsonText);
            using (var handle = File.OpenHandle(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                RandomAccess.Write(handle, bytes, 0);
            }
            // overwrite: true performs an atomic replace on the same volume.
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Failed to atomically save report to path: {path}.");
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (Exception cleanupException)
            {
                GitWizardLog.LogException(cleanupException, $"Failed to clean up temp file {tempPath}.");
            }
        }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitWizard;

public partial class GitWizardReport
{
    /// <summary>
    /// Targeted single-repo merge refresh used by the CLI <c>-merge</c> flag (issue #42).
    /// Reads the existing report at <paramref name="savePath"/> if present (otherwise starts
    /// from an empty report), refreshes only the supplied <paramref name="repositoryPaths"/>,
    /// upserts those entries into the existing report's repository collection by path key —
    /// leaving every other entry intact — stamps <see cref="CurrentSchemaVersion"/> on the
    /// whole report, and writes it back atomically (temp file in the same directory followed
    /// by an overwriting rename) so a concurrent reader never observes a half-written file.
    /// </summary>
    /// <remarks>
    /// Concurrency: two callers merging disjoint repos race on the file as a whole — last
    /// writer wins (a merge that started from a now-stale snapshot will clobber the other
    /// caller's entries). This is acceptable for the projdash fallback use case; there is no
    /// lockfile. See <c>docs/report-schema.md</c>.
    /// </remarks>
    /// <param name="savePath">Path to the report JSON to read and write.</param>
    /// <param name="configuration">Configuration used to refresh the supplied repos.</param>
    /// <param name="repositoryPaths">The repository paths to refresh and upsert.</param>
    /// <param name="updateHandler">Optional handler for progress updates.</param>
    /// <param name="allBranches">When true, include all branches in each refreshed repo.</param>
    /// <returns>The merged report (also written to <paramref name="savePath"/>).</returns>
    public static GitWizardReport MergeIntoFile(string savePath, GitWizardConfiguration configuration,
        ICollection<string> repositoryPaths, IUpdateHandler? updateHandler = null, bool allBranches = false)
    {
        // Refresh just the supplied repos in an isolated report so we get fresh entries for them
        // without disturbing anything else (Refresh prunes deleted paths and would otherwise
        // touch the whole collection, so it must run on a throwaway report — not the on-disk one).
        var refreshed = new GitWizardReport(configuration);
        if (repositoryPaths.Count > 0)
            refreshed.Refresh(repositoryPaths, updateHandler, allBranches: allBranches);

        // Read the existing report as a JSON DOM rather than deserializing into GitWizardReport.
        // Deserialize-then-reserialize would drop every `private set` field (e.g. CurrentBranch)
        // on the UNTOUCHED entries — defeating the "leave other entries intact" contract (#42).
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
        // private-set fields on the UNTOUCHED entries read back null — a pre-existing limitation
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
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception,
                $"Failed to read/parse report at {path}; starting merge from an empty report.");
            return null;
        }
    }

    /// <summary>
    /// Write the report to <paramref name="path"/> atomically: serialize to a temp file in the
    /// same directory, then rename it over the destination. The rename is atomic on a single
    /// volume, so a concurrent reader sees either the old file or the new one — never a
    /// partially-written file. Stamps <see cref="CurrentSchemaVersion"/> before writing.
    /// </summary>
    public void SaveAtomic(string path)
    {
        SchemaVersion = CurrentSchemaVersion;
        WriteAtomic(path, JsonSerializer.Serialize(this, SerializerOptions));
    }

    /// <summary>
    /// Write <paramref name="jsonText"/> to <paramref name="path"/> atomically: serialize to a
    /// temp file in the same directory, then rename it over the destination. The rename is atomic
    /// on a single volume, so a concurrent reader sees either the old file or the new one — never
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
            File.WriteAllText(tempPath, jsonText);
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

using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitWizard;

public partial class GitWizardReport
{
    /// <summary>
    /// Asynchronous counterpart of <see cref="MergeIntoFile"/>: identical merge contract and atomic
    /// write, but the existing report is read and the merged report written with true asynchronous
    /// file I/O so a caller on a UI or request thread is never blocked on the disk. Cancellation is
    /// honoured before any file is touched and during I/O; a cancelled call leaves the existing file
    /// untouched and removes its temporary file.
    /// </summary>
    /// <param name="savePath">Path to the report JSON to read and write.</param>
    /// <param name="configuration">Configuration used to refresh the supplied repos.</param>
    /// <param name="repositoryPaths">The repository paths to refresh and upsert.</param>
    /// <param name="updateHandler">Optional handler for progress updates.</param>
    /// <param name="options">Refresh flags for the supplied repos; a merge reads <c>AllBranches</c>
    /// and <c>ComputeLocalCommitCount</c>.</param>
    /// <param name="cancellationToken">Cancels the read, refresh wait and write.</param>
    /// <returns>The merged report (also written to <paramref name="savePath"/>).</returns>
    public static async Task<GitWizardReport> MergeIntoFileAsync(string savePath, GitWizardConfiguration configuration,
        ICollection<string> repositoryPaths, IUpdateHandler? updateHandler = null,
        GitWizardReportOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new GitWizardReportOptions();

        // The report is IAsyncDisposable, so here it is disposed properly (see the CA2000 note in MergeIntoFile).
        await using var refreshed = new GitWizardReport(configuration);
        await RefreshOnThreadPoolAsync(refreshed, repositoryPaths, updateHandler, options, cancellationToken)
            .ConfigureAwait(false);

        var root = await ReadJsonObjectFromFileAsync(savePath, cancellationToken).ConfigureAwait(false)
                   ?? new JsonObject();
        var jsonText = ApplyRefreshedEntries(root, refreshed, repositoryPaths);
        await WriteAtomicAsync(savePath, jsonText, cancellationToken).ConfigureAwait(false);
        return BuildMergeResult(jsonText, refreshed, configuration);
    }

    /// <summary>
    /// Runs the synchronous repository refresh on the thread pool: it has no asynchronous API (it
    /// is process and CPU bound), so the async merge offloads it instead of blocking the caller.
    /// The report arrives as a parameter so the queued delegate never closes over the caller's
    /// <c>await using</c> variable.
    /// </summary>
    static Task RefreshOnThreadPoolAsync(GitWizardReport refreshed, ICollection<string> repositoryPaths,
        IUpdateHandler? updateHandler, GitWizardReportOptions options, CancellationToken cancellationToken)
    {
        if (repositoryPaths.Count == 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() => refreshed.Refresh(repositoryPaths, updateHandler,
            allBranches: options.AllBranches, computeLocalCommitCount: options.ComputeLocalCommitCount),
            cancellationToken);
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="ReadJsonObjectFromFile"/>: reads through an overlapped
    /// file handle, returns null for a missing or unparseable file, and lets cancellation propagate.
    /// </summary>
    internal static async Task<JsonObject?> ReadJsonObjectFromFileAsync(string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                FileOptions.Asynchronous);
            var length = checked((int)RandomAccess.GetLength(handle));
            var buffer = new byte[length];
            var totalRead = 0;
            while (totalRead < length)
            {
                var read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(totalRead), totalRead, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return JsonNode.Parse(buffer.AsSpan(0, totalRead)) as JsonObject;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GitWizardLog.LogException(exception,
                $"Failed to read/parse report at {path}; starting merge from an empty report.");
            return null;
        }
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        if (DiscoveryInventory is { CanPublishFullInventory: false })
        {
            throw new InvalidOperationException("Incomplete discovery report cannot be saved.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            SchemaVersion = CurrentSchemaVersion;
            var snapshot = CreateSnapshotForSerialization();
            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, useAsync: true);
            await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Failed to save report to path: {path}.");
        }
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="SaveAtomic"/>: same temp-file-then-rename contract, but
    /// the report is serialized straight to the temp file with asynchronous I/O. Cancellation leaves
    /// any existing file untouched and removes the temp file.
    /// </summary>
    public Task SaveAtomicAsync(string path, CancellationToken cancellationToken = default)
    {
        if (DiscoveryInventory is { CanPublishFullInventory: false })
        {
            throw new InvalidOperationException("Incomplete discovery report cannot be saved.");
        }

        SchemaVersion = CurrentSchemaVersion;
        var snapshot = CreateSnapshotForSerialization();
        return WriteAtomicAsync(path,
            (stream, token) => JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, token),
            cancellationToken);
    }

    static Task WriteAtomicAsync(string path, string jsonText, CancellationToken cancellationToken)
    {
        return WriteAtomicAsync(path,
            (stream, token) => stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(jsonText), token).AsTask(),
            cancellationToken);
    }

    /// <summary>
    /// Asynchronous atomic write: streams to a same-directory temp file with overlapped I/O, then
    /// renames it over <paramref name="path"/>. Failures are logged and swallowed exactly like the
    /// synchronous overload; cancellation removes the temp file and propagates.
    /// </summary>
    static async Task WriteAtomicAsync(string path, Func<Stream, CancellationToken, Task> writeContent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, useAsync: true))
            {
                await writeContent(stream, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Failed to atomically save report to path: {path}.");
            TryDeleteTempFile(tempPath);
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using LibGit2Sharp;

namespace GitWizard;

/// <summary>
/// Machine-readable tag inventory for automation and fleet-wide tag auditing/reaping.
/// </summary>
[Serializable]
public sealed class TagInventoryReport
{
    public const string CurrentSchemaVersion = "1.0";

    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string MatchPattern { get; set; } = "*";
    public List<RepositoryTagItem> Repositories { get; set; } = new();

    /// <summary>
    /// Scan repositories for tags matching the pattern and report whether they exist on remotes.
    /// </summary>
    public static TagInventoryReport Scan(
        IEnumerable<string> repositoryPaths,
        string matchPattern = "*",
        Func<string, Task<IReadOnlySet<string>>>? remoteTagFetcher = null)
    {
        // Invariant: synchronous wrapper for callers without an async context.
        return ScanAsync(repositoryPaths, matchPattern, remoteTagFetcher).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously scan repositories for tags matching the pattern and report whether they exist on remotes.
    /// </summary>
    public static async Task<TagInventoryReport> ScanAsync(
        IEnumerable<string> repositoryPaths,
        string matchPattern = "*",
        Func<string, Task<IReadOnlySet<string>>>? remoteTagFetcher = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryPaths);

        if (string.IsNullOrWhiteSpace(matchPattern))
            matchPattern = "*";

        var report = new TagInventoryReport
        {
            MatchPattern = matchPattern
        };

        var remoteCache = new ConcurrentDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var effectiveFetcher = remoteTagFetcher ?? (url => FetchRemoteTagsAsync(url, cancellationToken));

        var sortedPaths = new SortedSet<string>(repositoryPaths, StringComparer.Ordinal);
        foreach (var path in sortedPaths)
        {
            var item = await ScanRepositoryAsync(path, matchPattern, effectiveFetcher, remoteCache).ConfigureAwait(false);
            if (item != null)
            {
                report.Repositories.Add(item);
            }
        }

        return report;
    }

    static async Task<RepositoryTagItem?> ScanRepositoryAsync(
        string path,
        string matchPattern,
        Func<string, Task<IReadOnlySet<string>>> remoteTagFetcher,
        ConcurrentDictionary<string, IReadOnlySet<string>> remoteCache)
    {
        var item = new RepositoryTagItem { Path = path };

        try
        {
            if (!Repository.IsValid(path))
                return null;

            using var repository = new Repository(path);

            var matchingTags = new List<Tag>();
            foreach (var tag in repository.Tags)
            {
                if (IsTagMatch(tag.FriendlyName, matchPattern))
                {
                    matchingTags.Add(tag);
                }
            }

            if (matchingTags.Count == 0)
                return null;

            var remotes = repository.Network.Remotes.ToList();
            var remoteTagMap = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

            foreach (var remote in remotes)
            {
                if (string.IsNullOrEmpty(remote.Url))
                    continue;

                if (!remoteCache.TryGetValue(remote.Url, out var remoteTags))
                {
                    remoteTags = await remoteTagFetcher(remote.Url).ConfigureAwait(false);
                    remoteCache[remote.Url] = remoteTags;
                }

                remoteTagMap[remote.Name] = remoteTags;
            }

            foreach (var tag in matchingTags)
            {
                var commitSha = tag.PeeledTarget?.Sha ?? tag.Target.Sha;
                var remotesWithTag = new List<string>();

                foreach (var (remoteName, remoteTags) in remoteTagMap)
                {
                    if (remoteTags.Contains(tag.FriendlyName))
                    {
                        remotesWithTag.Add(remoteName);
                    }
                }

                item.Tags.Add(new TagInfo
                {
                    Name = tag.FriendlyName,
                    CommitSha = commitSha,
                    ExistsOnRemote = remotesWithTag.Count > 0,
                    Remotes = remotesWithTag
                });
            }

            item.Tags = item.Tags.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
            return item;
        }
        catch (Exception exception)
        {
            item.Error = exception.Message;
            return item;
        }
    }

    internal static bool IsTagMatch(string tagName, string pattern)
    {
        if (pattern == "*")
            return true;

        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(tagName, pattern, StringComparison.Ordinal);

        var regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(tagName, regexPattern, RegexOptions.None);
    }

    static readonly char[] LineSeparators = ['\r', '\n'];

    internal static async Task<IReadOnlySet<string>> FetchRemoteTagsAsync(string remoteUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            processStartInfo.ArgumentList.Add("ls-remote");
            processStartInfo.ArgumentList.Add("--tags");
            processStartInfo.ArgumentList.Add(remoteUrl);

            using var process = Process.Start(processStartInfo);
            if (process == null)
                return new HashSet<string>(StringComparer.Ordinal);

            using var registration = cancellationToken.Register(static state =>
            {
                if (state is Process proc)
                    TryKillProcess(proc, "cancellation");
            }, process);

            var readOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var readErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var waitForExitTask = process.WaitForExitAsync(cancellationToken);

            var timeoutTask = Task.Delay(10000, cancellationToken);
            var completedTask = await Task.WhenAny(waitForExitTask, timeoutTask).ConfigureAwait(false);

            if (completedTask == timeoutTask)
            {
                TryKillProcess(process, "timeout");
                GitWizardLog.Log($"git ls-remote timed out for {remoteUrl}", GitWizardLog.LogType.Warning);
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var output = await readOutputTask.ConfigureAwait(false);
            await readErrorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new HashSet<string>(StringComparer.Ordinal);

            return ParseLsRemoteOutput(output);
        }
        catch (Exception exception)
        {
            GitWizardLog.LogException(exception, $"Failed to fetch remote tags for {remoteUrl}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    static void TryKillProcess(Process process, string reason)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException ex)
        {
            GitWizardLog.Log($"Process already exited on {reason}: {ex.Message}", GitWizardLog.LogType.Verbose);
        }
        catch (Exception ex)
        {
            GitWizardLog.Log($"Failed to kill process on {reason}: {ex.Message}", GitWizardLog.LogType.Verbose);
        }
    }

    static HashSet<string> ParseLsRemoteOutput(string output)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var tabIndex = line.IndexOf('\t');
            if (tabIndex < 0)
                continue;

            var refName = line[(tabIndex + 1)..].Trim();
            const string tagPrefix = "refs/tags/";
            if (refName.StartsWith(tagPrefix, StringComparison.Ordinal))
            {
                var rawTag = refName[tagPrefix.Length..];
                if (rawTag.EndsWith("^{}", StringComparison.Ordinal))
                    rawTag = rawTag[..^3];

                tags.Add(rawTag);
            }
        }

        return tags;
    }
}

/// <summary>
/// Tag inventory for a single repository.
/// </summary>
[Serializable]
public sealed class RepositoryTagItem
{
    public string Path { get; set; } = string.Empty;
    public List<TagInfo> Tags { get; set; } = new();
    [UsedImplicitly]
    public string? Error { get; set; }
}

/// <summary>
/// Information about a single tag in a repository.
/// </summary>
[Serializable]
public sealed class TagInfo
{
    public string Name { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public bool ExistsOnRemote { get; set; }
    public List<string> Remotes { get; set; } = new();
}

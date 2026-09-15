using GitWizard.Watch;
using MFTLib;

namespace GitWizard;

public static partial class GitWizardApi
{
    /// <summary>Discovers repositories with an owned packed index and releases it after querying.</summary>
    public static async Task<bool> TryFindAllRepositoriesUsingMftAsync(
        GitWizardConfiguration configuration, ICollection<string> paths,
        IUpdateHandler? updateHandler = null, bool noMft = false)
    {
        await using var result = await TryDiscoverRepositoriesUsingMftAsync(
            configuration, paths, updateHandler, noMft).ConfigureAwait(false);
        return result.Success;
    }

    /// <summary>Transfers a successful discovery index session to the returned result.</summary>
    public static Task<MftDiscoveryResult> TryDiscoverRepositoriesUsingMftAsync(
        GitWizardConfiguration configuration, ICollection<string> paths,
        IUpdateHandler? updateHandler = null, bool noMft = false)
    {
        if (!OperatingSystem.IsWindows() || noMft)
            return Task.FromResult(new MftDiscoveryResult(false));

        var roots = configuration.SearchPaths.Select(ExpandSearchPath).OfType<string>().ToArray();
        var ignored = ExpandIgnoredPaths(configuration.IgnoredPaths);
        return DiscoverRepositoriesWithIndexAsync(configuration, paths, roots, ignored, new IndexDiscoveryComposition(OpenWindowsDiscoveryIndexAsync, WindowsCandidateExists), updateHandler);
    }

    static Task<MftIndexSession> OpenWindowsDiscoveryIndexAsync(IReadOnlyList<string> drives)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("MFT discovery requires Windows.");
        return MftIndexSession.OpenWindowsAsync(drives, BrokerLauncher.Launch, CancellationToken.None);
    }

    static bool WindowsCandidateExists(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows candidates require Windows filesystem validation.");
        return Directory.Exists(path);
    }

    internal static async Task<MftDiscoveryResult> DiscoverRepositoriesWithIndexAsync(
        GitWizardConfiguration configuration, ICollection<string> paths,
        IReadOnlyCollection<string> expandedRoots, ICollection<string> expandedIgnoredPaths,
        IndexDiscoveryComposition composition, IUpdateHandler? updateHandler = null)
    {
        var drives = expandedRoots.Select(WatchPath.Parse)
            .Select(root => root.IsWindows && root.Volume is { Length: 1 } volume &&
                char.IsAsciiLetter(volume[0]) ? volume.ToUpperInvariant() : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (expandedRoots.Count == 0 || drives.Length == 0)
            return new MftDiscoveryResult(false);

        MftIndexSession? owner = null;
        try
        {
            owner = await composition.OpenIndex(drives).ConfigureAwait(false);
            foreach (var error in owner.ScanErrors)
            {
                var message = $"MFT scan error on {error.Key}: {error.Value}";
                GitWizardLog.Log(message, GitWizardLog.LogType.Warning);
                updateHandler?.SendUpdateMessage(message);
            }

            var entries = SelectGitEntries(owner.Index, includeGitFiles: true);
            var candidates = expandedRoots.SelectMany(root => ResolveRepositoryRootCandidates(
                owner.Index, entries, root, expandedIgnoredPaths, configuration.SkipHiddenDirectories))
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(composition.CandidateExists).ToArray();
            foreach (var candidate in candidates)
                paths.Add(candidate);
            updateHandler?.SendUpdateMessage($"MFT search found {candidates.Length} repositories");
            if (candidates.Length == 0)
                return new MftDiscoveryResult(false);
            var result = new MftDiscoveryResult(true, owner);
            owner = null;
            return result;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            GitWizardLog.Log($"MFT scan unavailable: {exception.Message}", GitWizardLog.LogType.Warning);
            return new MftDiscoveryResult(false);
        }
        finally
        {
            if (owner is not null)
                await owner.DisposeAsync().ConfigureAwait(false);
        }
    }
}

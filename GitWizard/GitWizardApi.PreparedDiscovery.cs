using GitWizard.Discovery;
using GitWizard.Watch;
using MFTLib;

namespace GitWizard;

public static partial class GitWizardApi
{
    /// <summary>Transfers a successful discovery index session to the returned result.</summary>
    public static Task<MftDiscoveryResult> TryDiscoverRepositoriesUsingMftAsync(
        GitWizardConfiguration configuration, ICollection<string> paths,
        IUpdateHandler? updateHandler = null, bool noMft = false)
    {
        return TryDiscoverRepositoriesUsingMftAsync(
            configuration, paths, null, null, updateHandler, noMft);
    }

    internal static Task<MftDiscoveryResult> TryDiscoverRepositoriesUsingMftAsync(
        GitWizardConfiguration configuration, ICollection<string> paths, Func<string, bool>? brokerLauncher,
        TimeSpan? connectTimeout = null,
        IUpdateHandler? updateHandler = null, bool noMft = false)
    {
        if (!OperatingSystem.IsWindows() || noMft)
        {
            return Task.FromResult(new MftDiscoveryResult(false));
        }

        brokerLauncher ??= BrokerLauncher.Launch;
        var roots = configuration.SearchPaths.Select(ExpandSearchPath).OfType<string>().ToArray();
        var ignored = ExpandIgnoredPaths(configuration.IgnoredPaths);
        return DiscoverRepositoriesWithIndexAsync(configuration, paths, roots, ignored, new IndexDiscoveryComposition(
            drives => OpenWindowsDiscoveryIndexAsync(drives, brokerLauncher, connectTimeout), WindowsCandidateExists), updateHandler);
    }

    static Task<MftIndexSession> OpenWindowsDiscoveryIndexAsync(
        IReadOnlyList<string> drives, Func<string, bool> brokerLauncher, TimeSpan? connectTimeout = null)
    {
        return !OperatingSystem.IsWindows()
            ? throw new PlatformNotSupportedException("MFT discovery requires Windows.")
            : MftIndexSession.OpenWindowsAsync(drives, brokerLauncher, connectTimeout, CancellationToken.None);
    }

    static bool WindowsCandidateExists(string path, RepositoryCandidateKind kind)
    {
        return !OperatingSystem.IsWindows()
            ? throw new PlatformNotSupportedException("Windows candidates require Windows filesystem validation.")
            : RepositoryCandidateProbe.Exists(path, kind);
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
        {
            return new MftDiscoveryResult(false);
        }

        MftIndexSession? owner = null;
        IReadOnlyList<MftRootStatus>? openedStatuses = null;
        try
        {
            owner = await composition.OpenIndex(drives).ConfigureAwait(false);
            openedStatuses = owner.CaptureRootStatuses();
            foreach (var error in owner.ScanErrors)
            {
                var message = $"MFT scan error on {error.Key}: {error.Value}";
                GitWizardLog.Log(message, GitWizardLog.LogType.Warning);
                updateHandler?.SendUpdateMessage(message);
            }

            var request = new DiscoveryRequest(configuration, DiscoveryPurpose.Ordinary, false, false, []);
            var scope = DiscoveryScope.CreateExpanded(request, (string[])[.. expandedRoots], [], (string[])[.. expandedIgnoredPaths]);
            var repoCandidates = SelectRepositoryCandidates(owner.IndexSet, scope);
            var candidates = (string[])[.. repoCandidates
                .Where(c => composition.CandidateExists(c.Path, c.Kind))
                .Select(c => c.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            foreach (var candidate in candidates)
            {
                paths.Add(candidate);
            }

            updateHandler?.SendUpdateMessage($"MFT search found {candidates.Length} repositories");
            if (candidates.Length == 0)
            {
                return new MftDiscoveryResult(false, scanErrors: owner.ScanErrors,
                    driveStatuses: owner.CaptureRootStatuses());
            }

            var result = new MftDiscoveryResult(true, owner);
            owner = null;
            return result;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            GitWizardLog.Log($"MFT scan unavailable: {exception.Message}", GitWizardLog.LogType.Warning);
            var scanErrors = new Dictionary<string, string>(
                owner?.ScanErrors ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
            {
                ["*"] = exception.Message
            };
            return new MftDiscoveryResult(false, scanErrors: scanErrors, driveStatuses: openedStatuses);
        }
        finally
        {
            if (owner is not null)
            {
                await owner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

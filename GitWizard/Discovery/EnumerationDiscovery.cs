using System.Text.RegularExpressions;
using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard.Discovery;

/// <summary>
/// Discovers repositories under one recursive root by walking it into an enumeration-backed
/// <see cref="FileIndex" /> with git-wizard's vocabulary passed as options, then running the same
/// <see cref="GitWizardApi.SelectRepositoryCandidates" /> query the MFT route uses. Every call
/// opens an uncached index, so each ordinary discovery rescans; age-based reuse is a separate policy.
/// </summary>
static class EnumerationDiscovery
{
    public static async Task<RecursiveDiscovery> DiscoverAsync(
        DiscoveryRequest request, string expandedRoot, CancellationToken cancellationToken,
        CandidateSelectionDiagnostics? diagnostics = null,
        Func<FileIndexOptions, FileIndexOptions>? configureOptions = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var paths = new List<string>();
        var failedSubtrees = new List<string>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(expandedRoot))
        {
            errors[expandedRoot] = $"Directory does not exist: {expandedRoot}";
            failedSubtrees.Add(expandedRoot);
            return new RecursiveDiscovery(paths, failedSubtrees, errors);
        }

        var ignoredPaths = GitWizardApi.ExpandIgnoredPaths(request.Configuration.IgnoredPaths);
        var searchRoot = WatchPath.Parse(GitWizardApi.NormalizePath(expandedRoot));
        if (!CandidateAdmissionPolicy.Create(ignoredPaths, request.Configuration).AdmitsWithin(searchRoot, searchRoot))
        {
            return new RecursiveDiscovery(paths, failedSubtrees, errors);
        }

        var scope = DiscoveryScope.CreateExpanded(request, [expandedRoot], [], ignoredPaths);

        FileIndex index;
        try
        {
            var options = CreateIndexOptions(request.Configuration, expandedRoot, ignoredPaths);
            index = await FileIndex.OpenAsync(configureOptions?.Invoke(options) ?? options,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed(paths, failedSubtrees, errors, expandedRoot, exception.Message);
        }

        await using (index.ConfigureAwait(false))
        {
            var status = index.Status;
            if (status.State != RootState.Ready)
            {
                return Failed(paths, failedSubtrees, errors, expandedRoot,
                    status.Failure?.Message ?? $"Drive is {status.State}.");
            }

            diagnostics ??= new CandidateSelectionDiagnostics();
            paths.AddRange(GitWizardApi.SelectRepositoryCandidates(new FileIndexSet([index]), scope, diagnostics, cancellationToken)
                .Where(IsConfirmed)
                .Select(candidate => candidate.Path));

            if (diagnostics.UnresolvedPathCount > 0)
            {
                return Failed(paths, failedSubtrees, errors, expandedRoot,
                    $"{diagnostics.UnresolvedPathCount} repository candidate(s) under {expandedRoot} are nested deeper than the index can name.");
            }

            switch (status.Block)
            {
                case { IsCapacityExhausted: true }:
                    return Failed(paths, failedSubtrees, errors, expandedRoot,
                        $"The index block for {expandedRoot} ran out of capacity before the walk finished.");
                case { AccessDeniedSubtreeCount: > 0 } block:
                    return Failed(paths, failedSubtrees, errors, expandedRoot,
                        $"{block.AccessDeniedSubtreeCount} subtree(s) under {expandedRoot} could not be entered.");
            }

            return new RecursiveDiscovery(paths, failedSubtrees, errors) { ResolvedRoot = index.Root(cancellationToken).Path };
        }
    }

    // The index matches names case-insensitively, so the filesystem confirms what the index selected. An
    // ordinary candidate is confirmed by the index entry itself, so only its position outside a .git tree is checked.
    internal static bool IsConfirmed(RepositoryCandidate candidate) =>
        candidate.Kind == RepositoryCandidateKind.Ordinary
            ? !RepositoryCandidateProbe.IsInsideGitDirectory(candidate.Path)
            : RepositoryCandidateProbe.Exists(candidate.Path, candidate.Kind);

    internal static FileIndexOptions CreateIndexOptions(
        GitWizardConfiguration configuration, string root, IReadOnlyList<string> ignoredPaths) =>
        new()
        {
            Root = RootKey.FromDirectory(root),
            Producer = ProducerSelection.WalkOnly,
            CacheMode = CacheMode.Disabled,
            DirectoryWalk = CreateDirectoryWalk(configuration, root, ignoredPaths)
        };

    internal static DirectoryWalkOptions CreateDirectoryWalk(
        GitWizardConfiguration configuration, string root, IReadOnlyList<string> ignoredPaths)
    {
        var skipHidden = configuration.SkipHiddenDirectories == true;
        var prunedPaths = new List<string>(ignoredPaths.Where(Path.IsPathFullyQualified));
        if (configuration.IncludeTempDirectories != true && !TempDirectories.IsTempPath(root))
        {
            prunedPaths.AddRange(TempDirectories.ExpandConcretePaths(root));
        }

        return new DirectoryWalkOptions
        {
            PrunedDirectoryNamePatterns = [GitDirectoryNamePattern],
            PruneDotDirectories = skipHidden,
            PrunedDirectoryAttributes = skipHidden ? FileAttributes.Hidden : 0,
            PrunedDirectoryPaths = prunedPaths
        };
    }

    internal static FileIndexOptions ConfigureWalk(FileIndexOptions options, GitWizardConfiguration configuration)
    {
        var root = options.Root ?? throw new ArgumentException("Discovery options require a root.", nameof(options));
        return options with
        {
            DirectoryWalk = CreateDirectoryWalk(configuration,
                root.TryGetVolume(out var volume) ? $"{volume}:\\" : root.ToString(),
                GitWizardApi.ExpandIgnoredPaths(configuration.IgnoredPaths))
        };
    }

    // The walk matches patterns against directory names, so one case-insensitive pattern prunes every casing of .git.
    static readonly Regex GitDirectoryNamePattern = new(
        $"^{Regex.Escape(GitWizardApi.GitEntryName)}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static RecursiveDiscovery Failed(
        List<string> paths, List<string> failedSubtrees, Dictionary<string, string> errors, string root, string message)
    {
        errors[root] = message;
        failedSubtrees.Add(root);
        return new RecursiveDiscovery(paths, failedSubtrees, errors);
    }
}

using GitWizard.Watch;
using MFTLib.Index;

namespace GitWizard;

/// <summary>
/// Discovers repositories under one recursive root by walking it into an enumeration-backed
/// <see cref="FileIndex" /> with git-wizard's vocabulary passed as options, then running the same
/// <see cref="GitWizardApi.SelectRepositoryCandidates" /> query the MFT route uses. Every call
/// opens an uncached index, so each ordinary discovery rescans; age-based reuse is a separate policy.
/// </summary>
static class EnumerationDiscovery
{
    // Enumeration drives take a caller-assigned key that only labels the single drive of this index.
    const char FallbackDriveLetter = 'L';

    public static async Task<RecursiveDiscovery> DiscoverAsync(
        DiscoveryRequest request, string expandedRoot, CancellationToken cancellationToken,
        CandidateSelectionDiagnostics? diagnostics = null)
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
            return new RecursiveDiscovery(paths, failedSubtrees, errors);

        var scope = DiscoveryScope.CreateExpanded(request, [expandedRoot], [], ignoredPaths,
            root => new RootClassification(root, RootFileSystem.Recursive, null, null));

        FileIndex index;
        try
        {
            index = await FileIndex.OpenAsync(CreateIndexOptions(request.Configuration, expandedRoot, ignoredPaths),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed(paths, failedSubtrees, errors, expandedRoot, exception.Message);
        }

        await using (index.ConfigureAwait(false))
        {
            var status = index.Drives.Single();
            if (status.State != DriveState.Ready)
                return Failed(paths, failedSubtrees, errors, expandedRoot,
                    status.Failure?.Message ?? $"Drive is {status.State}.");

            diagnostics ??= new CandidateSelectionDiagnostics();
            foreach (var candidate in GitWizardApi.SelectRepositoryCandidates(index, scope, diagnostics, cancellationToken))
            {
                if (IsConfirmed(candidate))
                    paths.Add(GitWizardApi.NormalizePath(candidate.Path));
            }

            if (diagnostics.UnresolvedPathCount > 0)
                return Failed(paths, failedSubtrees, errors, expandedRoot,
                    $"{diagnostics.UnresolvedPathCount} repository candidate(s) under {expandedRoot} are nested deeper than the index can name.");

            if (status.Block is { IsCapacityExhausted: true })
                return Failed(paths, failedSubtrees, errors, expandedRoot,
                    $"The index block for {expandedRoot} ran out of capacity before the walk finished.");
            if (status.Block is { AccessDeniedSubtreeCount: > 0 } block)
                return Failed(paths, failedSubtrees, errors, expandedRoot,
                    $"{block.AccessDeniedSubtreeCount} subtree(s) under {expandedRoot} could not be entered.");
        }

        return new RecursiveDiscovery(paths, failedSubtrees, errors);
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
            Drives = [new IndexedDrive(DriveLetterFor(root), root, 0, ProducerKind.Enumeration)],
            CacheMode = CacheMode.Disabled,
            RetainedFileNames = MftIndexCachePolicy.DirectoryScanFileNames,
            DirectoryWalk = CreateDirectoryWalk(configuration, root, ignoredPaths)
        };

    internal static DirectoryWalkOptions CreateDirectoryWalk(
        GitWizardConfiguration configuration, string root, IReadOnlyList<string> ignoredPaths)
    {
        var skipHidden = configuration.SkipHiddenDirectories == true;
        var prunedPaths = new List<string>(ignoredPaths.Where(Path.IsPathFullyQualified));
        if (configuration.IncludeTempDirectories != true && !TempDirectories.IsTempPath(root))
            prunedPaths.AddRange(TempDirectories.ExpandConcretePaths(root));

        return new DirectoryWalkOptions
        {
            PrunedDirectoryNames = GitDirectoryNameSpellings,
            PruneDotDirectories = skipHidden,
            PrunedDirectoryAttributes = skipHidden ? FileAttributes.Hidden : 0,
            PrunedDirectoryPaths = prunedPaths
        };
    }

    // The pinned walk compares pruned names ordinally on Unix and offers no case-insensitive hook, so every
    // casing of the three-letter name is listed and a mixed-case metadata tree is never entered.
    static readonly string[] GitDirectoryNameSpellings = CreateCaseSpellings(GitWizardApi.GitEntryName);

    static string[] CreateCaseSpellings(string name)
    {
        var letterIndexes = Enumerable.Range(0, name.Length).Where(index => char.IsAsciiLetter(name[index])).ToArray();
        var spellings = new string[1 << letterIndexes.Length];
        for (var mask = 0; mask < spellings.Length; mask++)
        {
            var characters = name.ToCharArray();
            for (var bit = 0; bit < letterIndexes.Length; bit++)
            {
                if ((mask & (1 << bit)) != 0)
                    characters[letterIndexes[bit]] = char.ToUpperInvariant(characters[letterIndexes[bit]]);
            }

            spellings[mask] = new string(characters);
        }

        return spellings;
    }

    static char DriveLetterFor(string root) =>
        WatchPath.Parse(root) is { IsWindows: true, Volume: { Length: 1 } volume } && char.IsAsciiLetter(volume[0])
            ? volume[0]
            : FallbackDriveLetter;

    static RecursiveDiscovery Failed(
        List<string> paths, List<string> failedSubtrees, Dictionary<string, string> errors, string root, string message)
    {
        errors[root] = message;
        failedSubtrees.Add(root);
        return new RecursiveDiscovery(paths, failedSubtrees, errors);
    }
}

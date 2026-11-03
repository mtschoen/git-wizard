using LibGit2Sharp;

namespace GitWizard;

internal static class RepositoryIndexStatEvidence
{
    // One stat-dirty file with unchanged content is direct evidence of a stale cache.
    internal const int MinimumStaleStatEntries = 1;

    internal static bool HasStaleEntries(Repository repository)
    {
        // IndexEntry does not expose stat fields. Git handles index versions, split
        // indexes and stat matching; --name-only does not refresh or write the index.
        var result = GitCli.RunStatus(repository, "--no-optional-locks", "diff-files",
            "--name-only", "-z", "--no-ext-diff", "--ignore-submodules=all", "--");
        if (!result.Succeeded)
        {
            return false;
        }

        var staleEntries = 0;
        foreach (var path in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = repository.Index[path];
            if (entry == null || entry.StageLevel != StageLevel.Staged ||
                entry.Mode is not (Mode.NonExecutableFile or Mode.ExecutableFile))
            {
                continue;
            }

            try
            {
                var blob = repository.Lookup<Blob>(entry.Id);
                if (blob == null)
                {
                    continue;
                }

                // Compare checkout-filtered bytes, so CRLF normalization does not
                // turn unchanged content into a real edit or write an object.
                using var indexed = blob.GetContentStream(new FilteringOptions(path));
                using var working = File.OpenRead(Path.Combine(repository.Info.WorkingDirectory, path));
                if (ContentMatches(indexed, working) && ++staleEntries >= MinimumStaleStatEntries)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LibGit2SharpException)
            {
                // A missing/unreadable file or blob is not unchanged-content evidence.
            }
        }

        return false;
    }

    static bool ContentMatches(Stream indexed, Stream working)
    {
        Span<byte> indexedBytes = stackalloc byte[4096];
        Span<byte> workingBytes = stackalloc byte[4096];
        while (true)
        {
            var indexedLength = indexed.ReadAtLeast(indexedBytes, indexedBytes.Length, throwOnEndOfStream: false);
            var workingLength = working.ReadAtLeast(workingBytes, workingBytes.Length, throwOnEndOfStream: false);
            if (indexedLength != workingLength ||
                !indexedBytes[..indexedLength].SequenceEqual(workingBytes[..workingLength]))
            {
                return false;
            }

            if (indexedLength == 0)
            {
                return true;
            }
        }
    }
}

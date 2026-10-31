namespace GitWizard;

public static partial class Pruner
{
    /// <summary>
    /// One ref per stash commit, written create-only before any drop, so a stash stays reachable
    /// whatever a drop does to the <c>refs/stash</c> reflog.
    /// </summary>
    internal const string StashArchiveNamespace = "refs/git-wizard/stash-archive/";

    /// <summary>
    /// Where the archive ref of a confirmed, dropped stash moves: an undo window that the next
    /// stash-pruning <c>--apply</c> run in the same repository deletes.
    /// </summary>
    internal const string StashDroppedNamespace = "refs/git-wizard/stash-dropped/";

    static List<PruneCandidate> PruneStashes(RepositoryContext context)
    {
        var candidates = StashMergeDetector.Inventory(context.Repository, context.BaseBranch?.Tip)
            .Select(stash => new PruneCandidate
            {
                Repository = context.Path,
                Kind = PruneCandidate.StashKind,
                Name = $"stash@{{{stash.Index}}}",
                Sha = stash.Sha,
                MergedBy = stash.MergedBy,
                Message = stash.Message,
                SkipReason = StashSkipReason(context, stash)
            })
            .ToList();

        var eligible = new List<PruneCandidate>();
        foreach (var candidate in candidates)
        {
            if (candidate.SkipReason != null)
            {
                candidate.Action = PruneCandidate.Skipped;
            }
            else if (!context.Options.Apply)
            {
                candidate.Action = PruneCandidate.WouldDelete;
            }
            else
            {
                eligible.Add(candidate);
            }
        }

        var archive = new StashArchive(context);
        if (context.Options.Apply)
        {
            archive.DeletePreviousDroppedRefs();

            // Highest index first (the inventory is newest first), so a drop never moves an
            // older candidate's position.
            for (var position = eligible.Count - 1; position >= 0; position--)
            {
                var candidate = eligible[position];
                candidate.SkipReason = archive.Drop(candidate);
                candidate.Action = candidate.SkipReason == null ? PruneCandidate.Deleted : PruneCandidate.Skipped;
            }
        }

        archive.Finish();
        return candidates;
    }

    static string? StashSkipReason(RepositoryContext context, StashInventoryInfo stash)
    {
        if (context.IsExcluded(stash.Sha))
        {
            return PruneSkipReasons.NotConfirmed;
        }

        return stash.IsMerged ? null : PruneSkipReasons.NotMerged;
    }

    /// <summary>The SHA in git's "Dropped stash@{n} (sha)" line; null when the output names none.</summary>
    internal static string? ParseDroppedStashSha(string output)
    {
        var line = output.Trim();
        var open = line.LastIndexOf('(');
        if (open < 0 || !line.EndsWith(')'))
        {
            return null;
        }

        var sha = line[(open + 1)..^1];
        return sha.Length is 40 or 64 && sha.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f')) ? sha : null;
    }

    sealed record StashEntry(string Sha, string Message);

    /// <summary>
    /// The archive refs and drop bookkeeping for one repository's stash pass. git can only drop a
    /// stash by position, so every drop is checked against the SHA git reports, and every entry a
    /// mismatched drop removed is stored back from its archived commit.
    /// </summary>
    sealed class StashArchive
    {
        readonly RepositoryContext _context;
        readonly HashSet<string> _archived = new(StringComparer.Ordinal);
        readonly HashSet<string> _created = new(StringComparer.Ordinal);
        readonly HashSet<string> _dropped = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> _messages = new(StringComparer.Ordinal);
        bool _stopped;

        public StashArchive(RepositoryContext context)
        {
            _context = context;
        }

        /// <summary>Ends the undo window of stashes the previous run dropped.</summary>
        public void DeletePreviousDroppedRefs()
        {
            foreach (var (sha, name) in ListRefs(StashDroppedNamespace))
            {
                GitCli.Run(_context.Repository, "update-ref", "-d", name, sha);
            }
        }

        /// <summary>Drop the candidate's stash; null when it was dropped, else the skip reason.</summary>
        public string? Drop(PruneCandidate candidate)
        {
            if (_stopped)
            {
                return PruneSkipReasons.StashDropsStopped;
            }

            var before = ReadStashList();
            if (before == null)
            {
                Stop("Could not list stashes; no further stash was dropped.");
                return PruneSkipReasons.StashDropsStopped;
            }

            if (!Archive(before))
            {
                return PruneSkipReasons.StashDropsStopped;
            }

            var position = before.FindIndex(entry => entry.Sha == candidate.Sha);
            if (position < 0)
            {
                return PruneSkipReasons.StashMissing;
            }

            _context.Options.BeforeDelete?.Invoke(candidate);
            var result = GitCli.Run(_context.Repository, "stash", "drop", $"stash@{{{position}}}");
            result = _context.Options.AfterStashDrop?.Invoke(result) ?? result;
            var droppedSha = result.Succeeded ? ParseDroppedStashSha(result.Output) : null;
            if (string.Equals(droppedSha, candidate.Sha, StringComparison.Ordinal))
            {
                _dropped.Add(candidate.Sha);
                _context.RestoreHints.WriteLine(
                    $"Dropped stash@{{{position}}} ({candidate.Sha}); restore with: {StoreCommand(candidate.Sha)} ({StashDroppedNamespace}{candidate.Sha} keeps it until the next prune --apply)");
                return null;
            }

            return RestoreAfterMismatch(before, droppedSha, result.Succeeded);
        }

        /// <summary>
        /// Store back every entry that was listed before the drop and is gone now, plus the entry
        /// git reported dropping when it was pushed inside the race window.
        /// </summary>
        string RestoreAfterMismatch(List<StashEntry> before, string? droppedSha, bool dropSucceeded)
        {
            var after = ReadStashList();
            if (after == null)
            {
                Stop($"Could not list stashes after a stash drop that did not report the expected SHA; every stash listed before it is kept under {StashArchiveNamespace}.");
                return PruneSkipReasons.StashMismatch;
            }

            var present = after.Select(entry => entry.Sha).ToHashSet(StringComparer.Ordinal);

            // Oldest first, so the restored entries keep their relative order at the top of the list.
            var missing = before.Where(entry => !present.Contains(entry.Sha)).DistinctBy(entry => entry.Sha).Reverse().ToList();
            if (droppedSha != null && !present.Contains(droppedSha) && missing.TrueForAll(entry => entry.Sha != droppedSha))
            {
                missing.Add(new StashEntry(droppedSha, MessageFor(droppedSha)));
            }

            if (missing.Count == 0)
            {
                return dropSucceeded ? PruneSkipReasons.StashMismatch : PruneSkipReasons.DeleteFailed;
            }

            if (Archive(missing))
            {
                foreach (var entry in missing)
                {
                    if (!Restore(entry))
                    {
                        break;
                    }
                }
            }

            return PruneSkipReasons.StashMismatch;
        }

        bool Restore(StashEntry entry)
        {
            var stored = GitCli.Run(_context.Repository, StoreArguments(entry.Sha, entry.Message)).Succeeded;
            if (stored && ReadStashList()?.Exists(listed => listed.Sha == entry.Sha) == true)
            {
                return true;
            }

            Stop($"Could not restore stash {entry.Sha} after a stash drop that did not report the expected SHA; {StashArchiveNamespace}{entry.Sha} keeps its commit. Restore with: {StoreCommand(entry.Sha)}");
            return false;
        }

        /// <summary>
        /// Create-only archive refs for <paramref name="entries"/>. An existing archive ref for
        /// the same commit (left by an earlier run) also counts as archived.
        /// </summary>
        bool Archive(IEnumerable<StashEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (_archived.Contains(entry.Sha))
                {
                    continue;
                }

                var name = StashArchiveNamespace + entry.Sha;
                if (GitCli.RunQuiet(_context.Repository, "update-ref", name, entry.Sha, string.Empty).Succeeded)
                {
                    _created.Add(entry.Sha);
                }
                else if (ResolveRef(name) != entry.Sha)
                {
                    Stop($"Could not create {name}; no further stash was dropped.");
                    return false;
                }

                _archived.Add(entry.Sha);
            }

            return true;
        }

        /// <summary>
        /// Settle every archive ref: a dropped stash's ref moves to the undo namespace, a ref this
        /// run created for a stash still in the list is deleted, and a ref whose stash is no
        /// longer in the list stays, with a restore hint.
        /// </summary>
        public void Finish()
        {
            var refs = ListRefs(StashArchiveNamespace);
            if (refs.Count == 0)
            {
                return;
            }

            // An unreadable list keeps every ref, as if no stash were listed.
            var present = ReadStashList()?.Select(entry => entry.Sha).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var (sha, name) in refs)
            {
                if (_dropped.Contains(sha) && MoveToDropped(sha, name))
                {
                    continue;
                }

                if (present.Contains(sha))
                {
                    if (_created.Contains(sha))
                    {
                        GitCli.Run(_context.Repository, "update-ref", "-d", name, sha);
                    }

                    continue;
                }

                _context.RestoreHints.WriteLine(
                    $"Stash {sha} is not in the stash list; {name} keeps it. Restore with: {StoreCommand(sha)} && git -C {ShellQuote(_context.Path)} update-ref -d {name}");
            }
        }

        bool MoveToDropped(string sha, string name) =>
            GitCli.Run(_context.Repository, "update-ref", StashDroppedNamespace + sha, sha).Succeeded
            && GitCli.Run(_context.Repository, "update-ref", "-d", name, sha).Succeeded;

        void Stop(string message)
        {
            _stopped = true;
            _context.AddError(message);
        }

        List<StashEntry>? ReadStashList()
        {
            var result = GitCli.Run(_context.Repository, "stash", "list", "--format=%H %gs");
            if (!result.Succeeded)
            {
                return null;
            }

            var entries = result.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Select(line => line.IndexOf(' ', StringComparison.Ordinal) is var separator and >= 0
                    ? new StashEntry(line[..separator], line[(separator + 1)..])
                    : new StashEntry(line, string.Empty))
                .ToList();
            foreach (var entry in entries)
            {
                _messages.TryAdd(entry.Sha, entry.Message);
            }

            return entries;
        }

        /// <summary>The stash's reflog message when it was listed during this run, else its commit subject.</summary>
        string MessageFor(string sha)
        {
            if (_messages.TryGetValue(sha, out var message))
            {
                return message;
            }

            var subject = GitCli.RunQuiet(_context.Repository, "show", "-s", "--format=%s", sha);
            message = subject.Succeeded ? subject.Output.Trim() : string.Empty;
            _messages[sha] = message;
            return message;
        }

        string? ResolveRef(string name)
        {
            var result = GitCli.RunQuiet(_context.Repository, "rev-parse", "--verify", "--quiet", name);
            return result.Succeeded ? result.Output.Trim() : null;
        }

        List<(string Sha, string Name)> ListRefs(string prefix)
        {
            var result = GitCli.Run(_context.Repository, "for-each-ref", "--format=%(objectname) %(refname)", prefix);
            if (!result.Succeeded)
            {
                return [];
            }

            return result.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ', 2))
                .Where(parts => parts.Length == 2)
                .Select(parts => (parts[0], parts[1]))
                .ToList();
        }

        string StoreCommand(string sha)
        {
            var message = MessageFor(sha);
            var messageArgument = string.IsNullOrEmpty(message) ? string.Empty : $"-m {ShellQuote(message)} ";
            return $"git -C {ShellQuote(_context.Path)} stash store {messageArgument}{sha}";
        }

        static string[] StoreArguments(string sha, string message) =>
            string.IsNullOrEmpty(message) ? ["stash", "store", sha] : ["stash", "store", "-m", message, sha];
    }
}

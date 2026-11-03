using System.Collections.Frozen;

namespace GitWizard;

internal readonly record struct RepositoryPendingChangesStatus(bool IsDirty, int Count,
    IReadOnlySet<string> UntrackedPaths)
{
    internal const int TrackedFileThreshold = 70000;
    internal const double StatusTimeThresholdSeconds = 1.0;

    internal static RepositoryPendingChangesStatus Parse(string output, string? workingDirectory = null)
    {
        var dirty = false;
        var count = 0;
        var untrackedPaths = new HashSet<string>(StringComparer.Ordinal);
        var records = output.Split('\0');
        for (var position = 0; position < records.Length; position++)
        {
            var record = records[position];
            if (record.Length == 0 || record[0] is '#' or '!')
            {
                continue;
            }

            switch (record[0])
            {
                case '?':
                    untrackedPaths.Add(record[2..]);
                    dirty = true;
                    count++;
                    break;
                case 'u':
                    // Conflicted is dirty, but RepositoryStatus has no counted conflict group.
                    dirty = true;
                    break;
                case '1':
                case '2':
                    if (record.Length < 5 || record[1] != ' ' || record[4] != ' ')
                    {
                        throw new FormatException("Invalid porcelain v2 status columns");
                    }

                    var index = record[2];
                    var workingTree = record[3];
                    dirty |= index != '.' || workingTree != '.';
                    // X: M=Staged, A=Added, D=Removed, R=RenamedInIndex.
                    // C is Added: libgit2 does not detect copies, so the destination is new.
                    // Y: M=Modified, A=IntentToAdd (Added in index, plus Modified when the
                    // working-tree file is nonempty), D=Missing, R=RenamedInWorkDir. Default
                    // git status detects staged renames only, like LibGit2Sharp's default
                    // StatusOptions. T (type change), U (conflict), and unreadable entries
                    // have no counted group. '.' is unaltered. Count each column, not each
                    // pathname.
                    count += index is 'M' or 'A' or 'D' or 'R' or 'C' ? 1 : 0;
                    count += workingTree is 'M' or 'A' or 'D' or 'R' ? 1 : 0;
                    count += workingTree == 'A' && IsNonEmptyWorkingTreeFile(workingDirectory, record) ? 1 : 0;
                    if (record[0] == '2')
                    {
                        // -z emits the original path as the next NUL record, even if it
                        // begins with a status-looking character or contains newlines.
                        position++;
                        if (position >= records.Length || records[position].Length == 0)
                        {
                            throw new FormatException("Missing porcelain v2 rename source");
                        }
                    }

                    break;
                default:
                    throw new FormatException("Unknown porcelain v2 status record");
            }
        }

        return new RepositoryPendingChangesStatus(dirty, count, untrackedPaths.ToFrozenSet(StringComparer.Ordinal));
    }

    static bool IsNonEmptyWorkingTreeFile(string? workingDirectory, string record)
    {
        var fields = record.Split(' ', 9);
        if (fields.Length < 9 || fields[8].Length == 0)
        {
            throw new FormatException("Missing porcelain v2 status path");
        }

        return !string.IsNullOrEmpty(workingDirectory) &&
            new FileInfo(Path.Combine(workingDirectory, fields[8])) is { Exists: true, Length: > 0 };
    }
}

using System.Globalization;
using System.Text;
using MFTLib.Index;

namespace GitWizard.Discovery;

/// <summary>
/// Tallies why each discovered repository did or did not take its size from the index, so one log line per
/// discovery answers "why did this run walk?" without a line per repository.
/// </summary>
sealed class RepositorySizeSummary(int requested)
{
    readonly List<string> _completeRoots = [];
    readonly List<IncompleteSizeRoot> _incompleteRoots = [];
    int _indexed;
    int _notCovered;
    int _notFound;
    int _unknownSizeResolved;
    int _unknownSizeTooMany;
    int _unknownSizeUnreadable;
    long _unknownFilesRead;
    long _unknownFilesSkipped;

    public void NoteCompleteRoot(string rootPath) => _completeRoots.Add(rootPath);

    public void NoteIncompleteRoot(RootStatus status, string rootPath) =>
        _incompleteRoots.Add(new IncompleteSizeRoot(rootPath, RepositorySizeRoots.DescribeIncompleteFields(status)));

    public void NoteUnknownSizeResolved(int files)
    {
        _unknownSizeResolved++;
        _unknownFilesRead += files;
    }

    public void NoteUnknownSizeTooMany(long files)
    {
        _unknownSizeTooMany++;
        _unknownFilesSkipped += files;
    }

    public void NoteUnknownSizeUnreadable() => _unknownSizeUnreadable++;

    public void NoteIndexed(int count) => _indexed = count;

    /// <summary>Attributes every repository no complete snapshot located to the root that explains it.</summary>
    public void NoteUnlocated(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (_incompleteRoots.FirstOrDefault(root => RepositorySizeRoots.Covers(root.Root, path)) is { } incomplete)
            {
                incomplete.Repositories++;
            }
            else if (_completeRoots.Any(root => RepositorySizeRoots.Covers(root, path)))
            {
                _notFound++;
            }
            else
            {
                _notCovered++;
            }
        }
    }

    public string Describe()
    {
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Repository sizes: {_indexed} of {requested} from the index");
        if (_unknownSizeResolved > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" ({_unknownSizeResolved} completed by reading {_unknownFilesRead} unknown-size file(s) from disk)");
        }

        text.Append(CultureInfo.InvariantCulture, $"; {requested - _indexed} walked");
        var reasons = _incompleteRoots.Where(root => root.Repositories > 0).Select(root =>
            $"{root.Repositories} on {root.Root} (index not a complete snapshot: {root.Reasons})").ToList();
        AddReason(reasons, _notFound, "not found in a complete index");
        AddReason(reasons, _notCovered, "no index covers their root");
        AddReason(reasons, _unknownSizeTooMany,
            $"more unknown-size files than the {RepositorySizeCalculator.UnknownSizeFileLimit} read limit ({_unknownFilesSkipped} file(s))");
        AddReason(reasons, _unknownSizeUnreadable, "an unknown-size file could not be read");
        if (reasons.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" ({string.Join("; ", reasons)})");
        }

        return text.Append('.').ToString();
    }

    static void AddReason(List<string> reasons, int count, string reason)
    {
        if (count > 0)
        {
            reasons.Add($"{count} {reason}");
        }
    }
}

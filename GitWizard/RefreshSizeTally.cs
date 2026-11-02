using System.Globalization;

namespace GitWizard;

readonly record struct GitWizardRepositoryRefreshContext(
    RefreshSizeTally? SizeTally, CancellationToken CancellationToken);

/// <summary>
/// Counts, for one refresh run, where each repository's size came from, so a single log line says how many
/// repositories took the discovery index size and how many walked their directory and why.
/// </summary>
sealed class RefreshSizeTally
{
    int _indexed;
    int _absentFromIndex;
    int _notOffered;

    public int Indexed => _indexed;
    public int AbsentFromIndex => _absentFromIndex;
    public int NotOffered => _notOffered;

    public void NoteIndexed() => Interlocked.Increment(ref _indexed);

    public void NoteAbsentFromIndex() => Interlocked.Increment(ref _absentFromIndex);

    public void NoteNotOffered() => Interlocked.Increment(ref _notOffered);

    /// <summary>The run's one summary line, or null when no repository reached the size phase through an offer.</summary>
    public string? Describe()
    {
        if (Indexed + AbsentFromIndex + NotOffered == 0)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"Refresh sizes: {Indexed} from the discovery index; {AbsentFromIndex + NotOffered} walked " +
            $"({AbsentFromIndex} had no index size, {NotOffered} refreshed without an offered index size because the run fetches remotes or has no discovery index).");
    }
}

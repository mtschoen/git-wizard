using GitWizard.Watch;

namespace GitWizard;

/// <summary>Reads the Hidden attribute of a candidate's ancestors from disk for routes without an index.</summary>
static class DiskHiddenAncestors
{
    /// <summary>
    /// Returns a probe answering whether any of the nearest N directories of
    /// <paramref name="candidate"/> carries the Hidden attribute, or null where the attribute does
    /// not exist (anything but a Windows path on Windows).
    /// </summary>
    public static Func<int, bool>? For(WatchPath candidate)
    {
        if (!candidate.IsWindows || !OperatingSystem.IsWindows())
        {
            return null;
        }

        return depth =>
        {
            var directory = candidate.Original;
            for (var level = 0; level < depth && !string.IsNullOrEmpty(directory); level++)
            {
                try
                {
                    if (CandidateAdmissionPolicy.HasHiddenAttribute(File.GetAttributes(directory)))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // An unreadable ancestor carries no observable Hidden attribute.
                }

                directory = Path.GetDirectoryName(directory);
            }

            return false;
        };
    }
}

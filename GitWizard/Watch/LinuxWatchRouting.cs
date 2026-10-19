namespace GitWizard.Watch;

/// <summary>
/// The routing identity shared by the Linux watch source and the prepared Linux startup. Marked
/// roots drive native arming and readiness only, so the watch service filters every Linux batch
/// under this single bucket.
/// </summary>
internal static class LinuxWatchRouting
{
    public const string Volume = "/";
}

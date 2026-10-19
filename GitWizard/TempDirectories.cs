using GitWizard.Watch;

namespace GitWizard;

/// <summary>
/// The default temp-directory exclusion shared by discovery and Live. Unless
/// <see cref="GitWizardConfiguration.IncludeTempDirectories"/> is set, a path that is, or sits
/// under, a temp directory is not treated as a repository root: every profile's
/// <c>AppData\Local\Temp</c> on Windows (any <c>&lt;drive&gt;:\Users\&lt;name&gt;\AppData\Local\Temp</c>),
/// the process's own %TEMP%/%TMP%, and <c>/tmp</c> plus $TMPDIR when set. The check is purely
/// syntactic, so it gives the same answer on any host and stays cross-platform-testable.
/// </summary>
internal static class TempDirectories
{
    static readonly string[] TempEnvironmentVariables = ["TEMP", "TMP", "TMPDIR"];

    // Captured once: environment variables do not change during a process run, and recursive
    // discovery calls IsTempPath once per directory on the volume.
    static readonly WatchPath[] ProcessTempRoots = CaptureProcessTempRoots();

    /// <summary>
    /// Replaces the temporary-directory decision for every caller, so a test can place a temporary
    /// directory inside a tree that is not itself temporary. Tests using it are non-parallelizable.
    /// </summary>
    internal static Func<string, bool>? IsTempPathOverride { get; set; }

    public static bool IsTempPath(string path)
    {
        if (IsTempPathOverride is { } decide)
            return decide(path);

        var parsed = WatchPath.Parse(path);
        if (IsWindowsProfileTempPath(parsed))
            return true;

        foreach (var root in ProcessTempRoots)
        {
            if (parsed.IsUnderOrEqual(root))
                return true;
        }

        return false;
    }

    // Syntactic match for <drive>:\Users\<name>\AppData\Local\Temp (or anywhere under it), for
    // any drive letter and any profile name, so scratch repositories under other accounts'
    // profiles (CI runners, service accounts) are covered without enumerating the filesystem.
    // WatchPath.Comparison renders a Windows path with '/' separators, so a plain split yields
    // the segments.
    static bool IsWindowsProfileTempPath(WatchPath path)
    {
        if (!path.IsWindows)
            return false;

        var segments = path.Comparison.Split('/');
        return segments.Length >= 6
            && segments[0].Length == 2
            && segments[0][1] == ':'
            && char.IsAsciiLetter(segments[0][0])
            && segments[1].Equals("users", StringComparison.OrdinalIgnoreCase)
            && segments[2].Length > 0
            && segments[3].Equals("appdata", StringComparison.OrdinalIgnoreCase)
            && segments[4].Equals("local", StringComparison.OrdinalIgnoreCase)
            && segments[5].Equals("temp", StringComparison.OrdinalIgnoreCase);
    }

    // Both syntaxes are captured unconditionally: WatchPath.IsUnderOrEqual never matches across
    // the Windows/POSIX syntax boundary, so a Windows host's temporary-directory root simply never matches a
    // POSIX path and /tmp never matches a Windows path.
    static WatchPath[] CaptureProcessTempRoots()
    {
        var roots = new List<WatchPath>();
        foreach (var variable in TempEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(value))
                roots.Add(WatchPath.Parse(value));
        }

        roots.Add(WatchPath.Parse("/tmp"));
        return roots.ToArray();
    }
}

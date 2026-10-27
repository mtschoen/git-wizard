using MFTLib.Index;

namespace GitWizard;

public sealed class MftIndexCacheInspection
{
    readonly TimeProvider _clock;
    readonly bool _isWindows;
    readonly Func<string, IReadOnlyList<CachedBlockStatus>> _inspect;

    public MftIndexCacheInspection(TimeProvider? clock = null)
        : this(clock ?? TimeProvider.System, OperatingSystem.IsWindows(), InspectDirectory) { }

    internal MftIndexCacheInspection(TimeProvider clock, bool isWindows,
        Func<string, IReadOnlyList<CachedBlockStatus>> inspect)
    {
        _clock = clock;
        _isWindows = isWindows;
        _inspect = inspect;
    }

    public MftIndexCacheSnapshot Capture(string localFilesPath,
        IReadOnlyList<MftActiveCacheSession>? sessions = null)
    {
        var directory = MftIndexCachePolicy.GetCacheDirectory(localFilesPath);
        var inventory = Array.Empty<CachedBlockStatus>();
        var errors = new List<string>();
        if (_isWindows)
        {
            try
            {
                inventory = [.. _inspect(directory).OrderBy(row => row.File.Root).ThenBy(row => row.File.VolumeSerial)];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Inspection of {directory}: {exception.Message}");
                GitWizardLog.LogException(exception, $"Index cache inspection failed: {directory}");
            }
        }
        var active = _isWindows
            ? (MftActiveCacheSession[])[.. (sessions ?? []).Where(session => SameDirectory(session.CacheDirectory, directory))
                .Select(session => session with { Drives = Array.AsReadOnly<MftActiveCacheRoot>([.. session.Drives]) })]
            : [];
        return new MftIndexCacheSnapshot
        {
            IsApplicable = _isWindows,
            CacheDirectory = directory,
            DirectoryScanFileNames = Array.AsReadOnly<string>([.. MftIndexCachePolicy.DirectoryScanFileNames]),
            RequestedCacheTag = MftIndexCachePolicy.CacheTag,
            ObservedAt = _clock.GetUtcNow(),
            Inventory = Array.AsReadOnly(inventory),
            Sessions = Array.AsReadOnly(active),
            Errors = errors.AsReadOnly()
        };
    }

    internal static IReadOnlyList<CachedBlockStatus> InspectDirectory(string directory)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(directory); }
        catch (DirectoryNotFoundException) { return []; }
        catch (FileNotFoundException) { return []; }
        return (attributes & FileAttributes.Directory) == 0
            ? throw new IOException($"Cache path is not a directory: {directory}")
            : CacheDirectory.InspectCached(directory);
    }

    static bool SameDirectory(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
}

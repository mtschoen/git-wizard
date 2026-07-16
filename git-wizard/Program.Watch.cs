using System.Runtime.Versioning;
using MFTLib;

// ReSharper disable once CheckNamespace
namespace GitWizard.CLI;

public static partial class Program
{
    /// <summary>
    /// Dispatch MFTLib's elevated journal-broker child-process mode (the <c>--broker</c>
    /// flag). Returns true when this process was the elevated broker child - production
    /// never returns from that path, the runner exits the process itself; a normal launch
    /// matches no flag and returns false so <see cref="Main"/> continues its usual startup.
    /// </summary>
    internal static bool TryHandleElevatedBrokerEntry(string[] args, IElevatedEntryRunner runner) =>
        ElevatedEntryPoint.TryHandle(args, runner);

    /// <summary>
    /// --watch: auto-detect changes in tracked repositories via MFTLib's elevated journal
    /// broker (one UAC prompt for the whole run). Groups repository roots by drive, runs one
    /// <see cref="JournalBrokerScanSession"/> that cold-scans and live-watches those drives
    /// on a single elevated process, and prints one line per affected repository as journal
    /// batches arrive. Runs until Ctrl-C or the broker dies.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static async Task RunWatchAsync(GitWizardConfiguration configuration)
    {
        var repositoryPaths = await LoadTrackedRepositoryPathsAsync(configuration).ConfigureAwait(false);
        if (repositoryPaths.Count == 0)
        {
            Console.WriteLine("No tracked repositories found; nothing to watch.");
            return;
        }

        var rootsByDrive = GroupPathsByDrive(repositoryPaths);

        using var ctrlC = new CtrlCCancellation();

        JournalBrokerScanSession session;
        try
        {
            session = await JournalBrokerScanSession
                .StartAsync(BrokerLauncher.Launch, rootsByDrive.Keys.ToList(), ctrlC.Token)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            Console.WriteLine($"Could not start the journal broker: {exception.Message}");
            Environment.Exit(1);
            return;
        }

        await using (session.ConfigureAwait(false))
        {
            await RunWatchLoopAsync(session, rootsByDrive, ctrlC).ConfigureAwait(false);
        }
    }

    // Starts the session's live watch and drains batches until Ctrl-C or the broker dies.
    // Split from RunWatchAsync so the session's lifetime (the outer `await using`) is
    // visibly separate from the loop that uses it.
    static async Task RunWatchLoopAsync(
        JournalBrokerScanSession session, Dictionary<string, List<string>> rootsByDrive, CtrlCCancellation ctrlC)
    {
        var brokerDied = false;
        session.Faulted += reason =>
        {
            brokerDied = true;
            Console.WriteLine($"Broker died: {reason}");
            ctrlC.Cancel();
        };

        var filtersByDrive = BuildFiltersByDrive(rootsByDrive, session.LatestScan.Records);

        await session.StartWatchAsync(ctrlC.Token).ConfigureAwait(false);

        var watchTasks = session.LatestScan.AdvancedCursors.Keys
            .Where(rootsByDrive.ContainsKey)
            .Select(drive => WatchDriveAsync(session, drive, filtersByDrive[drive], ctrlC.Token))
            .ToArray();

        try
        {
            await Task.WhenAll(watchTasks).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException || brokerDied)
        {
            // Expected on Ctrl-C, or on broker death - which the session surfaces both as a
            // Faulted event (cancelling the token) and as an InvalidOperationException from
            // the faulted per-drive watch enumerable.
        }

        if (brokerDied)
            Environment.Exit(1);
    }

    /// <summary>
    /// Bridges Ctrl-C to a <see cref="CancellationTokenSource"/> for the --watch command's
    /// lifetime. A small owned type - the source is an instance field the handler reaches
    /// via <c>this</c>, not a captured local - rather than a bare
    /// <see cref="CancellationTokenSource"/> plus a lambda subscribed to
    /// <see cref="Console.CancelKeyPress"/>: that shape would let the static,
    /// process-lifetime event keep the token source reachable (and callable) past this
    /// type's own <see cref="Dispose"/>.
    /// </summary>
    sealed class CtrlCCancellation : IDisposable
    {
        readonly CancellationTokenSource _source = new();

        public CtrlCCancellation() => Console.CancelKeyPress += OnCancelKeyPress;

        public CancellationToken Token => _source.Token;

        public void Cancel() => _source.Cancel();

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            _source.Cancel();
        }

        public void Dispose()
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            _source.Dispose();
        }
    }

    // Reads live journal batches for one drive until cancelled, printing a line per
    // repository root the batch touched (deduplicated by RepositoryChangeFilter). The
    // session owns the drive's resume cursor, so none is threaded in here.
    static async Task WatchDriveAsync(
        JournalBrokerScanSession session, string drive,
        RepositoryChangeFilter filter, CancellationToken cancellationToken)
    {
        await foreach (var (entries, _) in session.WatchDriveAsync(drive, cancellationToken).ConfigureAwait(false))
        {
            foreach (var root in filter.Filter(entries))
                Console.WriteLine($"changed: {root}");
        }
    }

    // Loads the tracked repository list from the cache, falling back to a fresh discovery
    // scan (and re-saving the cache) when no cached list exists yet.
    static async Task<List<string>> LoadTrackedRepositoryPathsAsync(GitWizardConfiguration configuration)
    {
        var cached = await GitWizardApi.GetCachedRepositoryPathsAsync().ConfigureAwait(false);
        if (cached is { Length: > 0 })
            return cached.ToList();

        var discovered = new SortedSet<string>();
        configuration.GetRepositoryPaths(discovered);
        var paths = discovered.ToList();
        if (paths.Count > 0)
            await GitWizardApi.SaveCachedRepositoryPathsAsync(paths).ConfigureAwait(false);

        return paths;
    }

    static Dictionary<string, List<string>> GroupPathsByDrive(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var drive = GetDriveLetter(path);
            if (drive == null)
                continue;

            if (!result.TryGetValue(drive, out var roots))
                result[drive] = roots = new List<string>();

            roots.Add(path);
        }

        return result;
    }

    // Builds one RepositoryChangeFilter per drive from that drive's slice of the combined
    // cold-scan records (BrokerScanResult.Records spans every requested drive).
    static Dictionary<string, RepositoryChangeFilter> BuildFiltersByDrive(
        Dictionary<string, List<string>> rootsByDrive, IReadOnlyList<ScanRecord> scanRecords)
    {
        var recordsByDrive = new Dictionary<string, List<ScanRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in scanRecords)
        {
            var drive = GetDriveLetter(record.Path);
            if (drive == null)
                continue;

            if (!recordsByDrive.TryGetValue(drive, out var records))
                recordsByDrive[drive] = records = new List<ScanRecord>();

            records.Add(record);
        }

        var filters = new Dictionary<string, RepositoryChangeFilter>(StringComparer.OrdinalIgnoreCase);
        foreach (var (drive, roots) in rootsByDrive)
        {
            recordsByDrive.TryGetValue(drive, out var records);
            filters[drive] = new RepositoryChangeFilter(records ?? new List<ScanRecord>(), roots);
        }

        return filters;
    }

    static string? GetDriveLetter(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? null : root[..1];
    }
}

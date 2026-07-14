using System.Runtime.Versioning;
using GitWizard.Watch;
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
    /// broker (one UAC prompt for the whole run), driven through <see cref="RepositoryWatchService"/>.
    /// Prints one line per repository event as it arrives and runs until Ctrl-C or the broker dies.
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

        var searchRoots = configuration.SearchPaths
            .Select(GitWizardApi.ExpandSearchPath)
            .OfType<string>()
            .ToList();

        using var ctrlC = new CtrlCCancellation();
        await using var source = new UsnVolumeChangeSource(BrokerLauncher.Launch);
        var service = new RepositoryWatchService(source, repositoryPaths, searchRoots);

        var stopped = await RunWatchLoopAsync(service, Console.WriteLine, Environment.Exit, ctrlC.Token)
            .ConfigureAwait(false);
        if (stopped)
            Environment.Exit(1);
    }

    // Drains service.RunAsync, printing one line per RepositoryChangeEvent kind plus one line
    // per drive that failed to arm, and reports whether the underlying source died mid-run.
    // Internal (rather than folded into RunWatchAsync) so GitWizardTests can drive it with a
    // fake-source-backed RepositoryWatchService, a capturing sink, and a fake exit, without
    // touching the real Environment.Exit or a real elevated broker. The exit callback (defaulted
    // to Environment.Exit by the production caller) fires the non-zero exit for a failed broker
    // spawn - the one terminal condition that must exit before any events flow.
    internal static async Task<bool> RunWatchLoopAsync(
        RepositoryWatchService service, Action<string> writeLine, Action<int> exit,
        CancellationToken cancellationToken)
    {
        var stopped = false;
        service.Stopped += reason =>
        {
            stopped = true;
            writeLine($"Broker died: {reason}");
        };

        // Drive GetAsyncEnumerator/MoveNextAsync by hand rather than `await foreach` ON PURPOSE:
        // ScanErrors is only populated once arming completes (during the first MoveNextAsync) and
        // is guaranteed set before the first event, so the scan-error lines must be printed
        // between the first move and the first event. Do NOT "simplify" this back to await foreach
        // - that would lose the ability to interleave the scan-error print at the right point.
        await using var enumerator = service.RunAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        bool hasNext;
        try
        {
            hasNext = await MoveNextIgnoringCancellationAsync(enumerator).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            // Broker spawn declined (UAC) or failed: UsnVolumeChangeSource.ArmAndCatchUpAsync
            // surfaces JournalBrokerClient.SpawnAndConnectAsync's throw from this first arming
            // MoveNextAsync. Match the prior CLI's clean message + non-zero exit instead of
            // crashing with an unhandled stack trace.
            writeLine($"Could not start the journal broker: {exception.Message}");
            exit(1);
            return true;
        }

        foreach (var (drive, message) in service.ScanErrors)
            writeLine($"scan error on {drive}: {message}");

        while (hasNext)
        {
            writeLine(FormatChangeEvent(enumerator.Current));
            hasNext = await MoveNextIgnoringCancellationAsync(enumerator).ConfigureAwait(false);
        }

        return stopped;
    }

    // Ctrl-C and broker death both cancel the token backing RunAsync's enumeration; both are
    // expected ways for the loop to end, not failures to propagate.
    static async Task<bool> MoveNextIgnoringCancellationAsync(IAsyncEnumerator<RepositoryChangeEvent> enumerator)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    static string FormatChangeEvent(RepositoryChangeEvent changeEvent) =>
        changeEvent.Kind switch
        {
            RepositoryChangeKind.Changed => $"changed: {changeEvent.RepoRoot}",
            RepositoryChangeKind.Created => $"created: {changeEvent.RepoRoot}",
            RepositoryChangeKind.Deleted => $"deleted: {changeEvent.RepoRoot}",
            _ => $"{changeEvent.Kind.ToString().ToLowerInvariant()}: {changeEvent.RepoRoot}",
        };

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
}

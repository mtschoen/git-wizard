using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace GitWizard;

// Log-failure fallbacks use stderr so diagnostics preserve machine-readable stdout.
/// <summary>
/// Logging class for GitWizard
/// </summary>
public static class GitWizardLog
{
    public enum LogType
    {
        Verbose = 0,
        Info = 1,
        Warning = 2,
        Error = 3,

        /// <summary>Gated by <see cref="VerboseMode"/> exactly like <see cref="Verbose"/>, but
        /// named for messages that diagnose an expected transient state (e.g. a repository
        /// directory that vanished before its refresh ran), so an enabled verbose log reads at
        /// the message's real severity. Appended to preserve existing numeric values.</summary>
        Debug = 4
    }

    const string LogFileNameFormat = "GitWizardLog_{0:yyyy-MM-dd}.log";
    static readonly CompositeFormat LogFileNameCompositeFormat = CompositeFormat.Parse(LogFileNameFormat);

    /// <summary>
    /// Format string for log messages
    /// 0 - DateTime
    /// 1 - Type
    /// 2 - Message
    /// </summary>
    const string LogMessageFormat = "[{0:yyyy-MM-dd|HH:mm:ss.ffff}] - {1}: {2}";
    static readonly CompositeFormat LogMessageCompositeFormat = CompositeFormat.Parse(LogMessageFormat);

    /// <summary>
    /// Set VerboseMode to true to enable logging verbose messages
    /// </summary>
    public static bool VerboseMode { get; set; }

    /// <summary>
    /// Suppress console and file output of ordinary log messages and console dispatch of
    /// exception context. A typed sink still receives warning and error messages and exception
    /// context so report collection survives silence. Exception body callbacks and exception
    /// file records remain enabled.
    /// </summary>
    public static bool SilentMode { get; set; }

    /// <summary>
    /// Set to override the method used to log messages
    /// Default: Console.WriteLine
    /// </summary>
    public static Action<string?> LogMethod { get; set; } = Console.WriteLine;

    public static Action<string?, LogType>? TypedLogMethod { get; set; }

    static void DispatchConsole(string? message, LogType type)
    {
        var sink = TypedLogMethod;
        if (sink == null)
        {
            LogMethod(message);
        }
        else
        {
            sink(message, type);
        }
    }

    static readonly Lock LogFileLock = new();
    static readonly TimeSpan LogFileLifetime = TimeSpan.FromDays(30);
    static int _dropWarningCount;

    // Async buffered file logging
    internal static Channel<string> _logChannel = CreateLogChannel();
    static CancellationTokenSource _writerCts = new();
    internal static Task? _writerTask;
    static StreamWriter? _logFileWriter;

    static Channel<string> CreateLogChannel() =>
        Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    static GitWizardLog()
    {
        CleanLogFolder();
        _writerTask = WriteLogToFileAsync();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushForProcessExit();
    }

    /// <summary>
    /// Drains the queued file records and flushes the writer when the process exits, so the last
    /// records (for example the final per-repository timing lines) are not lost with the process.
    /// </summary>
    internal static void FlushForProcessExit()
    {
        try
        {
            CloseCurrentLogFile();
        }
        catch (Exception exception)
        {
            // Never throw from the exit path; the log is best-effort diagnostics.
            Console.Error.WriteLine($"[GitWizardLog] Failed to flush log at exit: {exception.Message}");
        }
    }

    /// <summary>
    /// Log a message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="type">The type of log message.</param>
    public static void Log(string message, LogType type = LogType.Info)
    {
        if (type is LogType.Verbose or LogType.Debug && !VerboseMode)
        {
            return;
        }

        var formattedMessage = string.Format(CultureInfo.InvariantCulture, LogMessageCompositeFormat, DateTime.UtcNow, type, message);
        if (SilentMode)
        {
            // Silence suppresses console and file output only; a typed sink (the report
            // diagnostic collector) still receives warnings and errors so the saved
            // run.warnings survive --silent.
            if (type is LogType.Warning or LogType.Error)
            {
                TypedLogMethod?.Invoke(formattedMessage, type);
            }

            return;
        }

        DispatchConsole(formattedMessage, type);
        LogToFile(formattedMessage);
    }

    /// <summary>
    /// Queue the exception's full text and optional context as one file record.
    /// File text uses Exception.ToString(), including aggregate children and available stacks.
    /// Console callbacks retain separate message, stack, and inner-exception label outputs.
    /// </summary>
    /// <remarks>
    /// Silent mode suppresses console dispatch of the context callback for this method; a
    /// typed sink still receives it.
    /// The bounded best-effort file queue may drop whole records; this method does not flush it.
    /// </remarks>
    /// <param name="exception">The exception to log.</param>
    /// <param name="message">Optional context preceding the exception diagnostics.</param>
    public static void LogException(Exception exception, string? message = null)
    {
        string? context = null;
        if (message != null)
        {
            context = string.Format(CultureInfo.InvariantCulture, LogMessageCompositeFormat,
                DateTime.UtcNow, LogType.Error, $"{message} Exception follows:");
        }

        var body = exception.ToString();
        LogToFile(context == null ? body : $"{context}{Environment.NewLine}{body}");

        if (context != null)
        {
            // Silent mode suppresses the context's console dispatch only; a typed sink
            // (the report diagnostic collector) still receives it.
            if (SilentMode)
            {
                TypedLogMethod?.Invoke(context, LogType.Error);
            }
            else
            {
                DispatchConsole(context, LogType.Error);
            }
        }
        LogExceptionToConsole(exception);
    }

    static void LogExceptionToConsole(Exception exception)
    {
        while (true)
        {
            DispatchConsole(exception.Message, LogType.Error);
            DispatchConsole(exception.StackTrace, LogType.Error);

            var innerException = exception.InnerException;
            if (innerException is null)
            {
                return;
            }

            DispatchConsole("Inner exception:", LogType.Error);
            exception = innerException;
        }
    }

    public static void CloseCurrentLogFile()
    {
        // Drain and dispose the current writer, then start a fresh one (fully idempotent).
        // This ensures that even after CloseCurrentLogFile is called by tests
        // or app shutdown, subsequent Log() calls still work.
        Channel<string> oldChannel;
        CancellationTokenSource oldCts;
        Task? oldTask;

        lock (LogFileLock)
        {
            oldChannel = _logChannel;
            oldCts = _writerCts;
            oldTask = _writerTask;

            // Records logged from now on queue on the new channel until its writer starts below.
            _logChannel = CreateLogChannel();
            _writerCts = new CancellationTokenSource();
            _writerTask = null;
        }

        // Complete the channel so the writer drains every queued record and then ends on its
        // own. Cancelling first would abandon the records still queued. The old writer task
        // flushes and disposes its own file writer when it finishes.
        oldChannel.Writer.Complete();

        // Wait for old writer task to finish draining
        // Invariant: _writerTask is a fire-and-forget background task that never throws;
        // blocking here is safe because there is no synchronization context to deadlock.
        oldTask?.GetAwaiter().GetResult();

        // Start the replacement writer only after the old one released the log file, because the
        // file is opened without write sharing.
        lock (LogFileLock)
        {
            _writerTask = WriteLogToFileAsync();
            oldCts.Dispose();
        }
    }

    static async Task WriteLogToFileAsync()
    {
        var reader = _logChannel.Reader;
        try
        {
            await foreach (var message in reader.ReadAllAsync(_writerCts.Token))
            {
                await WriteMessageAsync(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when _writerCts is cancelled
        }
        finally
        {
            // Flush any remaining buffered data
            // Hold LogFileLock while reading and nulling to prevent use-after-dispose
            // if CloseCurrentLogFile swaps the writer concurrently.
            StreamWriter? localWriter;
            lock (LogFileLock)
            {
                localWriter = _logFileWriter;
                _logFileWriter = null;
            }
            if (localWriter != null)
            {
                await localWriter.DisposeAsync();
            }
        }
    }

    static async ValueTask WriteMessageAsync(string message)
    {
        StreamWriter? localWriter;
        lock (LogFileLock)
        {
            localWriter = _logFileWriter ??= CreateLogFile();
        }

        if (localWriter == null)
        {
            return;
        }

        try
        {
            await localWriter.WriteLineAsync(message);
        }
        catch
        {
            // If writing fails, the writer will be recreated on next message
            lock (LogFileLock)
            {
                _logFileWriter = null;
            }
        }
    }

    static StreamWriter? CreateLogFile()
    {
        try
        {
            var fileName = string.Format(CultureInfo.InvariantCulture, LogFileNameCompositeFormat, DateTime.UtcNow);
            var logFolderPath = GitWizardApi.GetLogFolderPath();
            if (!Directory.Exists(logFolderPath))
            {
                Directory.CreateDirectory(logFolderPath);
            }

            var logFilePath = Path.Combine(logFolderPath, fileName);
            var writer = File.Exists(logFilePath)
                ? new StreamWriter(logFilePath, true)
                : File.CreateText(logFilePath);

            writer.AutoFlush = false;
            return writer;
        }
        catch (Exception exception)
        {
            // Log to console only to avoid recursion
            try
            {
                Console.Error.WriteLine($"[GitWizardLog] Failed to create log file: {exception.Message}");
            }
            catch
            {
                // Silently ignore console write failures during log initialization
            }
            return null;
        }
    }

    /// <summary>
    /// Writes a pre-formatted message to the log file via the async channel.
    /// Non-blocking: if the channel is full, the oldest message is dropped.
    /// </summary>
    static void LogToFile(string formattedMessage)
    {
        if (_logChannel.Writer.TryWrite(formattedMessage))
        {
            return;
        }

        // Channel is full - message was dropped.
        // Rate-limit the warning to avoid console spam under load.
        if (Interlocked.Increment(ref _dropWarningCount) % 100 != 0)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine("[GitWizardLog] Log channel full, message dropped.");
        }
        catch
        {
            // Ignore console failures
        }
    }

    internal static void CleanLogFolder()
    {
        new Thread(() =>
        {
            var logFolder = GitWizardApi.GetLogFolderPath();
            if (!Directory.Exists(logFolder))
            {
                return;
            }

            var now = DateTime.UtcNow;
            Parallel.ForEach(Directory.EnumerateFiles(logFolder), path =>
            {
                if (now - File.GetCreationTimeUtc(path) > LogFileLifetime)
                {
                    File.Delete(path);
                }
            });
        }).Start();
    }
}

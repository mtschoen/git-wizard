using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace GitWizard;

// aislop-worker - this library file uses Console.WriteLine for log-failure fallbacks
/// <summary>
/// Logging class for GitWizard
/// </summary>
public static class GitWizardLog
{
    public enum LogType
    {
        Verbose,
        Info,
        Warning,
        Error
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
    /// Set SilentMode to true to disable all console logging
    /// </summary>
    public static bool SilentMode { get; set; }

    /// <summary>
    /// Set to override the method used to log messages
    /// Default: Console.WriteLine
    /// </summary>
    public static Action<string?> LogMethod { get; set; } = Console.WriteLine;

    static readonly object LogFileLock = new();
    static readonly TimeSpan LogFileLifetime = TimeSpan.FromDays(30);
    static int _dropWarningCount;

    // Async buffered file logging
    static Channel<string> _logChannel = CreateLogChannel();
    static CancellationTokenSource _writerCts = new();
    static Task? _writerTask;
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
    }

    /// <summary>
    /// Log a message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="type">The type of log message.</param>
    public static void Log(string message, LogType type = LogType.Info)
    {
        if (SilentMode)
            return;

        if (type == LogType.Verbose && !VerboseMode)
            return;

        var formattedMessage = string.Format(CultureInfo.InvariantCulture, LogMessageCompositeFormat, DateTime.UtcNow, type, message);
        LogMethod(formattedMessage);
        LogToFile(formattedMessage);
    }

    public static void LogException(Exception exception, string? message = null)
    {
        if (message != null)
            Log($"{message} Exception follows:", LogType.Error);

        LogMethod(exception.Message);
        LogMethod(exception.StackTrace);

        var innerException = exception.InnerException;
        if (innerException != null)
        {
            LogMethod("Inner exception:");
            LogException(innerException);
        }
    }

    public static void CloseCurrentLogFile()
    {
        // Dispose old resources and recreate fresh ones (fully idempotent).
        // This ensures that even after CloseCurrentLogFile is called by tests
        // or app shutdown, subsequent Log() calls still work.
        Channel<string> oldChannel;
        CancellationTokenSource oldCts;
        Task? oldTask;
        StreamWriter? oldWriter;

        lock (LogFileLock)
        {
            oldChannel = _logChannel;
            oldCts = _writerCts;
            oldTask = _writerTask;
            oldWriter = _logFileWriter;

            _logChannel = CreateLogChannel();
            _writerCts = new CancellationTokenSource();
            _writerTask = WriteLogToFileAsync();
            _logFileWriter = null;
        }

        // Signal old writer to stop
        oldCts.Cancel();
        oldChannel.Writer.Complete();

        // Wait for old writer task to finish draining
        if (oldTask != null)
        {
            // Invariant: _writerTask is a fire-and-forget background task that never throws;
            // blocking here is safe because there is no synchronization context to deadlock.
            oldTask.GetAwaiter().GetResult();
        }

        // Dispose old resources
        lock (LogFileLock)
        {
            oldWriter?.Dispose();
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
            return;

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
                Directory.CreateDirectory(logFolderPath);

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
                Console.WriteLine($"[GitWizardLog] Failed to create log file: {exception.Message}");
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
        if (!_logChannel.Writer.TryWrite(formattedMessage))
        {
            // Channel is full - message was dropped.
            // Rate-limit the warning to avoid console spam under load.
            if (Interlocked.Increment(ref _dropWarningCount) % 100 == 0)
            {
                try
                {
                    Console.WriteLine("[GitWizardLog] Log channel full, message dropped.");
                }
                catch
                {
                    // Ignore console failures
                }
            }
        }
    }

    static void CleanLogFolder()
    {
        new Thread(() =>
        {
            var logFolder = GitWizardApi.GetLogFolderPath();
            if (!Directory.Exists(logFolder))
                return;

            var now = DateTime.UtcNow;
            Parallel.ForEach(Directory.EnumerateFiles(logFolder), path =>
            {
                if (now - File.GetCreationTimeUtc(path) > LogFileLifetime)
                    File.Delete(path);
            });
        }).Start();
    }
}

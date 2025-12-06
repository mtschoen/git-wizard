namespace GitWizard;

/// <summary>
/// Logging class for GitWizard
/// TODO: Make file logging async
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

    const string k_LogFileNameFormat = "GitWizardLog_{0:yyyy-MM-dd}.log";

    /// <summary>
    /// Format string for log messages
    /// 0 - DateTime
    /// 1 - Type
    /// 2 - Message
    /// </summary>
    const string k_LogMessageFormat = "[{0:yyyy-MM-dd|HH:mm:ss.ffff}] - {1}: {2}";

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

    static readonly object k_LogFileLock = new();
    static readonly TimeSpan k_LogFileLifetime = TimeSpan.FromDays(30);
    static bool _createLogFileFailed;
    static StreamWriter? _currentLogFile;

    static GitWizardLog()
    {
        CleanLogFolder();
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

        var formattedMessage = string.Format(k_LogMessageFormat, DateTime.UtcNow, type, message);
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
        lock (k_LogFileLock)
        {
            if (_currentLogFile == null)
                return;

            _currentLogFile.Close();
            _currentLogFile = null;
        }
    }

    static StreamWriter? GetOrCreateLogFile()
    {
        if (_createLogFileFailed)
            return null;

        if (_currentLogFile != null)
            return _currentLogFile;

        try
        {
            var fileName = string.Format(k_LogFileNameFormat, DateTime.UtcNow);
            var logFolderPath = GitWizardApi.GetLogFolderPath();
            if (!Directory.Exists(logFolderPath))
                Directory.CreateDirectory(logFolderPath);

            var logFilePath = Path.Combine(logFolderPath, fileName);
            _currentLogFile = File.Exists(logFilePath)
                ? new StreamWriter(logFilePath, true)
                : File.CreateText(logFilePath);

            _currentLogFile.AutoFlush = true;
        }
        catch (Exception exception)
        {
            LogException(exception);
        }

        _createLogFileFailed = _currentLogFile == null;
        return _currentLogFile;
    }

    /// <summary>
    /// Writes a pre-formatted message to the currently open log file.
    /// This method will call GetOrCreateLogFile if no log file stream exists.
    /// </summary>
    /// <param name="formattedMessage">The message to be written to the log file. No extra
    /// info (such as date/time or log type) will be appended.</param>
    static void LogToFile(string formattedMessage)
    {
        if (_createLogFileFailed)
            return;

        lock (k_LogFileLock)
        {
            _currentLogFile ??= GetOrCreateLogFile();
            if (_currentLogFile == null)
                return;

            _currentLogFile.WriteLine(formattedMessage);
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
                if (now - File.GetCreationTimeUtc(path) > k_LogFileLifetime)
                    File.Delete(path);
            });
        }).Start();
    }
}

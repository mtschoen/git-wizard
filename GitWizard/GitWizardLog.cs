namespace GitWizard;

/// <summary>
/// Logging class for GitWizard
/// TODO: Log to a file
/// TODO: User-configurable loggers
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

    /// <summary>
    /// Set VerboseMode to true to enable logging verbose messages
    /// </summary>
    public static bool VerboseMode { get; set; }

    /// <summary>
    /// Set SilentMode to true to disable all console logging
    /// </summary>
    public static bool SilentMode { get; set; }

    /// <summary>
    /// Log a message.
    /// </summary>
    /// <param name="message">The message to log.</param>
    /// <param name="type"></param>
    public static void Log(string message, LogType type = LogType.Info)
    {
        if (SilentMode)
            return;

        switch (type)
        {
            case LogType.Verbose:
                if (!VerboseMode)
                    return;

                Console.WriteLine($"Verbose: {message}");
                break;
            case LogType.Info:
                Console.WriteLine($"Info: {message}");
                break;
            case LogType.Warning:
                Console.WriteLine($"Warning: {message}");
                break;
            case LogType.Error:
                Console.WriteLine($"Error: {message}");
                break;
        }
    }

    public static void LogException(Exception exception, string? message = null)
    {
        if (message != null)
            Log($"{message} Exception follows:", LogType.Error);

        Console.WriteLine(exception.Message);
        Console.WriteLine(exception.StackTrace);
    }
}

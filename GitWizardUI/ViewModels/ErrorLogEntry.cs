namespace GitWizardUI.ViewModels;

/// <summary>
/// A single captured error: when it happened, which repository (if any) it came from, and
/// the message that would previously have gone to output/log only.
/// </summary>
public sealed record ErrorLogEntry(DateTime Timestamp, string? RepositoryPath, string Message)
{
    /// <summary>Single-line, view-ready rendering of the entry.</summary>
    public string DisplayText =>
        string.IsNullOrEmpty(RepositoryPath)
            ? $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] {Message}"
            : $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] {RepositoryPath}: {Message}";
}

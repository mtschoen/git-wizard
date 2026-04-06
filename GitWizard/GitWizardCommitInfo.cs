namespace GitWizard;

[Serializable]
public class GitWizardCommitInfo
{
    public string Hash { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }
    public string AuthorEmail { get; set; } = string.Empty;
}

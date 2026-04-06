namespace GitWizard;

[Serializable]
public class GitWizardSummary
{
    public string SchemaVersion { get; set; } = "1.0";
    public int TotalRepositories { get; set; }
    public int Dirty { get; set; }
    public int Unpushed { get; set; }
    public int Stale { get; set; }
    public List<AttentionItem> NeedingAttention { get; set; } = new();

    public static GitWizardSummary FromReport(GitWizardReport report)
    {
        var summary = new GitWizardSummary
        {
            SchemaVersion = report.SchemaVersion,
            TotalRepositories = report.Repositories.Count
        };

        foreach (var kvp in report.Repositories)
        {
            var repository = kvp.Value;
            var reasons = new List<string>();

            if (repository.HasPendingChanges)
            {
                summary.Dirty++;
                reasons.Add("dirty");
            }

            if (repository.LocalOnlyCommits)
            {
                summary.Unpushed++;
                reasons.Add("unpushed");
            }

            if (repository.DaysSinceLastCommit > 30)
            {
                summary.Stale++;
            }

            if (reasons.Count > 0)
            {
                summary.NeedingAttention.Add(new AttentionItem
                {
                    Path = kvp.Key,
                    Reasons = reasons
                });
            }
        }

        return summary;
    }
}

[Serializable]
public class AttentionItem
{
    public string Path { get; set; } = string.Empty;
    public List<string> Reasons { get; set; } = new();
}

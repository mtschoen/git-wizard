namespace GitWizard.CLI;

static class ReportTextFormatter
{
    internal static void Write(TextWriter writer, GitWizardReport report, bool cached)
    {
        var summary = GitWizardSummary.FromReport(report);
        var heading = cached ? "Cached report" : "Repositories";
        writer.WriteLine($"{heading}: {summary.TotalRepositories}; dirty={summary.Dirty} unpushed={summary.Unpushed} stale={summary.Stale} merged-branches={summary.MergedBranches} behind-remote={summary.BehindRemote}");
        foreach (var item in summary.NeedingAttention)
            writer.WriteLine($"{item.Path}: {string.Join(", ", item.Reasons)}");
        WriteRun(writer, report.Run ?? new ReportRunSummary());
    }

    internal static void WriteRun(TextWriter writer, ReportRunSummary run)
    {
        writer.WriteLine($"Run: created={run.Created} refreshed={run.Refreshed} skipped={run.Skipped}");
        foreach (var warning in run.Warnings) writer.WriteLine(warning);
    }
}

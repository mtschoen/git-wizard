namespace GitWizardUI.ViewModels;

public partial class RepositoryNodeViewModel
{
    /// <summary>Local branches the last refresh found merged into the base (cheap tiers only).</summary>
    public int MergedBranchCount => Repository.Branches?.Count(branch => branch.IsMerged) ?? 0;

    /// <summary>Merged branches plus merged stashes: what the Clean button could remove.</summary>
    public int PrunableCount => MergedBranchCount + Repository.MergedStashCount;

    /// <summary>
    /// Compact row badge such as "3 merged · 2 stashes · 1 local-only"; empty when every count
    /// is zero (the view hides it then).
    /// </summary>
    public string PruneBadgeText
    {
        get
        {
            if (IsGroupHeader)
            {
                return string.Empty;
            }

            var parts = new List<string>(3);
            if (MergedBranchCount > 0)
            {
                parts.Add($"{MergedBranchCount} merged");
            }

            var stashCount = Repository.StashCount;
            if (stashCount > 0)
            {
                parts.Add(stashCount == 1 ? "1 stash" : $"{stashCount} stashes");
            }

            if (Repository.LocalOnlyBranchCount > 0)
            {
                parts.Add($"{Repository.LocalOnlyBranchCount} local-only");
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasPruneBadge => PruneBadgeText.Length > 0;

    /// <summary>Spells out the badge's counts, including how many stashes are safe to drop.</summary>
    public string PruneBadgeTooltip =>
        $"{MergedBranchCount} branch(es) merged into the base, {Repository.StashCount} stash(es) " +
        $"({Repository.MergedStashCount} already on the base, safe to drop), " +
        $"{Repository.LocalOnlyBranchCount} unmerged branch(es) with commits on no remote";

    void NotifyPruneBadgeChanged()
    {
        OnPropertyChanged(nameof(PruneBadgeText));
        OnPropertyChanged(nameof(HasPruneBadge));
        OnPropertyChanged(nameof(PruneBadgeTooltip));
    }
}

using GitWizard;

namespace GitWizardUI.ViewModels;

public sealed partial class RepositoryNodeViewModel
{
    public bool CanRefreshIndex => !IsGroupHeader && Repository.IndexRefreshReason.HasValue;

    public string? IndexRefreshTooltip => Repository.IndexRefreshReason switch
    {
        RepositoryIndexRefreshReason.DeadIndexLock =>
            "Index needs refresh: index.lock is older than 10 minutes. Refresh index removes it only if it is still old, no git process is running, and an exclusive open succeeds, then updates cached file information.",
        RepositoryIndexRefreshReason.StaleIndex =>
            "Index needs refresh: status took over 1 second and unchanged files have stale cached file information. Refresh index may speed up status by updating that information.",
        _ => null
    };

    void NotifyIndexRefreshChanged()
    {
        OnPropertyChanged(nameof(CanRefreshIndex));
        OnPropertyChanged(nameof(IndexRefreshTooltip));
    }
}

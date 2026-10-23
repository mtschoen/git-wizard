using System.ComponentModel;

namespace GitWizardUI.ViewModels;

public sealed class StatusHeaderViewModel : INotifyPropertyChanged
{
    string _headerText = "GitWizard";

    public string HeaderText
    {
        get => _headerText;
        private set
        {
            if (_headerText != value)
            {
                _headerText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderText)));
            }
        }
    }

    internal string? LastRefreshMessage { get; set; }

    internal void ShowRefreshMessage(string message) => HeaderText = message;

    internal void ShowLiveMessage(string message) => HeaderText = message;

    internal void ShowFilterInfo(FilterType filter, GroupMode groupMode, int repositoryCount,
        IReadOnlyCollection<RepositoryNodeViewModel> visibleRows)
    {
        if (filter == FilterType.None && groupMode == GroupMode.None)
        {
            HeaderText = LastRefreshMessage ?? $"{repositoryCount} repositories";
        }
        else if (groupMode != GroupMode.None)
        {
            // Repositories can also hold an expanded group header's children as flat rows
            // (ToggleGroupExpand / AddToGroups insert them there for display), so the group
            // count must count headers specifically, not every row (issue #241 addendum: the
            // header read "953 repositories in 2 groups" while six groups were listed).
            var groupCount = visibleRows.Count(row => row.IsGroupHeader);
            HeaderText = $"{repositoryCount} repositories in {groupCount} groups";
        }
        else
        {
            HeaderText = $"Showing {visibleRows.Count} of {repositoryCount} repositories";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

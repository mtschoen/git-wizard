using System.Collections.Specialized;
using GitWizard;

namespace GitWizardUI.ViewModels;

public partial class RepositoryNodeViewModel
{
    readonly record struct ChildState(RefreshStatus Status, long? MeasuredSize);

    readonly Dictionary<RepositoryNodeViewModel, ChildState> _childStates = new();
    readonly List<RepositoryNodeViewModel> _parentHeaders = [];

    int _errorCount;
    int _warningCount;
    long _totalMeasuredBytes;
    int _measuredChildCount;

    internal IReadOnlyList<RepositoryNodeViewModel> ParentHeaders => _parentHeaders;

    internal long? MeasuredSize => Repository.HasComputedSizeOnDisk ? Repository.SizeOnDisk : null;

    internal void AddParentHeader(RepositoryNodeViewModel header)
    {
        if (!_parentHeaders.Contains(header))
        {
            _parentHeaders.Add(header);
        }
    }

    internal void RemoveParentHeader(RepositoryNodeViewModel header)
    {
        _parentHeaders.Remove(header);
    }

    internal void ClearParentHeaders()
    {
        _parentHeaders.Clear();
    }

    void NotifyParentHeaders()
    {
        if (_parentHeaders.Count == 0)
        {
            return;
        }

        foreach (var header in _parentHeaders)
        {
            header.OnChildUpdated(this);
        }
    }

    public string SizeText
    {
        get
        {
            if (_cachedSizeText != null)
            {
                return _cachedSizeText;
            }

            if (IsGroupHeader)
            {
                return _cachedSizeText = ByteSizeFormatter.FormatForDisplay(_measuredChildCount > 0 ? _totalMeasuredBytes : null);
            }

            return _cachedSizeText = ByteSizeFormatter.FormatForDisplay(MeasuredSize);
        }
    }

    void OnChildrenCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArguments)
    {
        switch (eventArguments.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (eventArguments.NewItems != null)
                {
                    foreach (RepositoryNodeViewModel child in eventArguments.NewItems)
                    {
                        AddChildTracking(child);
                    }
                }
                break;

            case NotifyCollectionChangedAction.Remove:
                if (eventArguments.OldItems != null)
                {
                    foreach (RepositoryNodeViewModel child in eventArguments.OldItems)
                    {
                        RemoveChildTracking(child);
                    }
                }
                break;

            case NotifyCollectionChangedAction.Replace:
                if (eventArguments.OldItems != null)
                {
                    foreach (RepositoryNodeViewModel child in eventArguments.OldItems)
                    {
                        RemoveChildTracking(child);
                    }
                }
                if (eventArguments.NewItems != null)
                {
                    foreach (RepositoryNodeViewModel child in eventArguments.NewItems)
                    {
                        AddChildTracking(child);
                    }
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                ResetAllChildrenTracking();
                break;
            case NotifyCollectionChangedAction.Move:
                // Moving a child changes order, not its tracked status, size or parent.
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(eventArguments), eventArguments.Action, "Unknown collection change action.");
        }

        if (IsGroupHeader)
        {
            UpdateGroupDisplayAndSize();
        }
    }

    void AddChildTracking(RepositoryNodeViewModel child)
    {
        if (_childStates.ContainsKey(child))
        {
            return;
        }

        var status = child.Status;
        var size = child.MeasuredSize;
        _childStates[child] = new ChildState(status, size);

        switch (status)
        {
            case RefreshStatus.Error:
                _errorCount++;
                break;
            case RefreshStatus.Timeout:
            case RefreshStatus.SubmoduleIssue:
                _warningCount++;
                break;
            case RefreshStatus.Refreshing:
            case RefreshStatus.Success:
            default:
                break;
        }

        if (size is { } measuredSize)
        {
            _totalMeasuredBytes += measuredSize;
            _measuredChildCount++;
        }

        child.AddParentHeader(this);
    }

    void RemoveChildTracking(RepositoryNodeViewModel child)
    {
        if (!_childStates.Remove(child, out var state))
        {
            return;
        }

        switch (state.Status)
        {
            case RefreshStatus.Error:
                _errorCount--;
                break;
            case RefreshStatus.Timeout:
            case RefreshStatus.SubmoduleIssue:
                _warningCount--;
                break;
            case RefreshStatus.Refreshing:
            case RefreshStatus.Success:
            default:
                break;
        }

        if (state.MeasuredSize is { } measuredSize)
        {
            _totalMeasuredBytes -= measuredSize;
            _measuredChildCount--;
        }

        child.RemoveParentHeader(this);
    }

    internal void ResetAllChildrenTracking()
    {
        foreach (var child in _childStates.Keys)
        {
            child.RemoveParentHeader(this);
        }
        _childStates.Clear();
        _errorCount = 0;
        _warningCount = 0;
        _totalMeasuredBytes = 0;
        _measuredChildCount = 0;

        foreach (var child in Children)
        {
            AddChildTracking(child);
        }
    }

    internal void OnChildUpdated(RepositoryNodeViewModel child)
    {
        if (!_childStates.TryGetValue(child, out var oldState))
        {
            return;
        }

        var newStatus = child.Status;
        var newSize = child.MeasuredSize;

        if (oldState.Status == newStatus && oldState.MeasuredSize == newSize)
        {
            return;
        }

        switch (oldState.Status)
        {
            case RefreshStatus.Error:
                _errorCount--;
                break;
            case RefreshStatus.Timeout:
            case RefreshStatus.SubmoduleIssue:
                _warningCount--;
                break;
            case RefreshStatus.Refreshing:
            case RefreshStatus.Success:
            default:
                break;
        }

        switch (newStatus)
        {
            case RefreshStatus.Error:
                _errorCount++;
                break;
            case RefreshStatus.Timeout:
            case RefreshStatus.SubmoduleIssue:
                _warningCount++;
                break;
            case RefreshStatus.Refreshing:
            case RefreshStatus.Success:
            default:
                break;
        }

        if (oldState.MeasuredSize is { } oldMeasuredSize)
        {
            _totalMeasuredBytes -= oldMeasuredSize;
            _measuredChildCount--;
        }

        if (newSize is { } newMeasuredSize)
        {
            _totalMeasuredBytes += newMeasuredSize;
            _measuredChildCount++;
        }

        _childStates[child] = new ChildState(newStatus, newSize);
        _cachedSizeText = null;
    }

    void UpdateGroupDisplayAndSize()
    {
        _cachedSizeText = ByteSizeFormatter.FormatForDisplay(_measuredChildCount > 0 ? _totalMeasuredBytes : null);
        OnPropertyChanged(nameof(SizeText));

        var suffix = "";
        if (_errorCount > 0)
        {
            suffix += $" {_errorCount} error{(_errorCount > 1 ? "s" : "")}";
        }

        if (_warningCount > 0)
        {
            suffix += $" {_warningCount} warning{(_warningCount > 1 ? "s" : "")}";
        }

        if (suffix.Length > 0)
        {
            suffix = " -" + suffix;
        }

        DisplayText = $"{GroupKey} ({Children.Count}){suffix}";
        OnPropertyChanged(nameof(ExpandIndicator));
    }
}

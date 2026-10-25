using Avalonia;
using GitWizard;

namespace GitWizardUI.Views;

public partial class MainWindow
{
    sealed class ScrollAnchorState(int index, object anchorItem, double above)
    {
        public int Index { get; } = index;
        public object AnchorItem { get; } = anchorItem;
        public double Above { get; } = above;
        public int Pass { get; set; }
    }

    void ConvergeScrollAnchor(ScrollAnchorState state)
    {
        if (++state.Pass > 8)
        {
            _scrollRestoreTimer?.Stop();
            _scrollRestoreTimer = null;
            return;
        }
        if (ListScrollViewer is not { } scrollViewer)
        {
            return;
        }

        // If the anchor virtualized away between passes, re-materialize and retry.
        if (RepositoryList.ContainerFromIndex(state.Index) is not { } container
            || container.TranslatePoint(default, RepositoryList) is not { } point)
        {
            RepositoryList.ScrollIntoView(state.AnchorItem);
            return;
        }

        var delta = point.Y + state.Above;   // converged when point.Y == -above
        if (Math.Abs(delta) < 1.0)
        {
            _scrollRestoreTimer?.Stop();
            _scrollRestoreTimer = null;
            if (GitWizardLog.VerboseMode)
            {
                GitWizardLog.Log($"[scroll] restored index={state.Index} above={state.Above:F0} in {state.Pass} pass(es)");
            }

            return;
        }

        var targetOffsetY = Math.Max(0, scrollViewer.Offset.Y + delta);
        scrollViewer.Offset = scrollViewer.Offset.WithY(targetOffsetY);
        if (GitWizardLog.VerboseMode)
        {
            GitWizardLog.Log($"[scroll] pass {state.Pass} index={state.Index} containerY={point.Y:F0} delta={delta:F0} -> offsetY={targetOffsetY:F0}");
        }
    }
}

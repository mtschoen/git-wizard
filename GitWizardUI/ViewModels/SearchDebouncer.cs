namespace GitWizardUI.ViewModels;

internal sealed class SearchDebouncer : IDisposable
{
    CancellationTokenSource? _cancellationTokenSource;

    public CancellationToken Restart()
    {
        var previous = _cancellationTokenSource;
        var next = new CancellationTokenSource();
        _cancellationTokenSource = next;
        if (previous != null)
        {
            _ = CancellationTokenSourceDisposal.CancelAndDisposeAsync(previous);
        }
        return next.Token;
    }

    public void Dispose()
    {
        var cancellationTokenSource = _cancellationTokenSource;
        _cancellationTokenSource = null;
        if (cancellationTokenSource != null)
        {
            _ = CancellationTokenSourceDisposal.CancelAndDisposeAsync(cancellationTokenSource);
        }
    }
}

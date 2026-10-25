namespace GitWizardTests.TestSupport;

sealed class CountingOwner : IAsyncDisposable
{
    public int Disposals { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}

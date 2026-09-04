namespace GitWizardUI.ViewModels;

public partial class MainViewModel
{
    internal static Task CancelAndDisposeAsync(CancellationTokenSource source)
        => source.CancelAsync().ContinueWith(
            static (_, state) => (state as CancellationTokenSource)?.Dispose(),
            source,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}

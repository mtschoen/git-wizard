namespace GitWizard.Watch;

public sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

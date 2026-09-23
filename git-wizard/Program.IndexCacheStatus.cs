namespace GitWizard.CLI;

public static partial class Program
{
    static async Task<bool> TryHandleIndexCacheStatusAsync(RunConfiguration configuration)
    {
        if (!configuration.IndexCacheStatus)
            return false;
        GitWizardLog.LogMethod = Console.Error.WriteLine;
        var snapshot = new MftIndexCacheInspection().Capture(GitWizardApi.GetLocalFilesPath());
        Environment.ExitCode = await WriteIndexCacheStatusAsync(snapshot, Console.Out, Console.Error);
        return true;
    }

    internal static async Task<int> WriteIndexCacheStatusAsync(MftIndexCacheSnapshot snapshot,
        TextWriter output, TextWriter error)
    {
        await output.WriteLineAsync(MftIndexCacheStatusFormatter.Format(snapshot)).ConfigureAwait(false);
        foreach (var message in snapshot.Errors)
            await error.WriteLineAsync(message).ConfigureAwait(false);
        return snapshot.Succeeded ? 0 : 1;
    }
}

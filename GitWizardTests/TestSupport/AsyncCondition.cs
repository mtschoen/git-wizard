using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace GitWizardTests.TestSupport;

internal static class AsyncCondition
{
    public static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var elapsed = Stopwatch.StartNew();
        var polls = 0;
        while (true)
        {
            polls++;
            bool satisfied;
            try
            {
                satisfied = condition();
            }
            catch (Exception exception)
            {
                throw new AssertionException(
                    $"Condition '{expression}' threw after {polls} poll(s); " +
                    $"elapsed {elapsed.Elapsed}: {exception}", exception);
            }

            if (satisfied)
                return;
            if (elapsed.Elapsed >= limit)
                throw new AssertionException(
                    $"Timed out waiting for '{expression}' after {polls} poll(s); " +
                    $"elapsed {elapsed.Elapsed}; limit {limit}.");

            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}

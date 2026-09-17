using System.Diagnostics;

namespace GitWizardTests.TestSupport;

internal static class UiBrokerLauncher
{
    internal static bool LaunchUiBroker(string brokerArguments)
    {
        var testDirectory = TestContext.CurrentContext.TestDirectory;
        var configuration = Directory.GetParent(testDirectory)?.Name
            ?? throw new InvalidOperationException("Could not determine the test build configuration.");
        var repositoryRoot = Path.GetFullPath(Path.Combine(testDirectory, "..", "..", "..", ".."));
        var executablePath = Path.Combine(
            repositoryRoot, "GitWizardUI", "bin", configuration, "net10.0", "GitWizardUI.exe");

        using var process = Process.Start(new ProcessStartInfo(executablePath, brokerArguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        return process is not null;
    }
}

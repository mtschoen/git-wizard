using System.Security.Principal;

namespace GitWizardUI;

static class DesktopInstance
{
    internal const string AlreadyRunningMessage = "git-wizard is already running";
    internal const int AlreadyRunningExitCode = 1;

    internal static string CreateName()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User
                ?? throw new InvalidOperationException("Cannot identify the desktop user.");
            return $"GitWizardUI.Desktop.{user.Value}";
        }

        // Unix user-scoped namespaces isolate this fixed name by OS user identity.
        return "GitWizardUI.Desktop.User";
    }

    internal static Mutex CreateMutex(string name) => new(false, name,
        new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false });

    internal static int Run(string name, Func<int> startDesktop, Action<string> showNotice)
    {
        using var mutex = CreateMutex(name);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // An abandoned admission gate has no shared state to repair.
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                showNotice(AlreadyRunningMessage);
                return AlreadyRunningExitCode;
            }

            return startDesktop();
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}

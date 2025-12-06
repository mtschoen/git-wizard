using System.Diagnostics;
using System.Threading.Tasks;

namespace GitWizard
{
    public static class WindowsDefenderException
    {
        public static void AddException()
        {
            Task.Run(() =>
            {
                var processName = Process.GetCurrentProcess().ProcessName;
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-Command Add-MpPreference -ExclusionProcess '{processName}'",
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                Process.Start(startInfo);
            });
        }
    }
}

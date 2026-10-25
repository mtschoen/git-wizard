using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitWizardUI;

static partial class StartupNotice
{
    internal static void Show(string message)
    {
        Console.Error.WriteLine(message);
        if (OperatingSystem.IsWindows())
        {
            _ = MessageBox(IntPtr.Zero, message, "GitWizard", 0x00000040);
        }
    }

    [SupportedOSPlatform("windows")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr owner, string text, string caption, uint type);
}

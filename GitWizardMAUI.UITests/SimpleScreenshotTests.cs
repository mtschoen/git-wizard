using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GitWizardMAUI.UITests;

[TestClass]
public class SimpleScreenshotTests
{
    private static readonly string _repoRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\..\.."));
    private static readonly string _appPath = Path.Combine(_repoRoot, @"GitWizardMAUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64\GitWizardMAUI.exe");

    // Win32 API imports for window capture
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static IntPtr FindWindowByProcessId(int processId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowProcessId);
            if (windowProcessId == processId && IsWindowVisible(hWnd))
            {
                var text = new System.Text.StringBuilder(256);
                GetWindowText(hWnd, text, text.Capacity);

                // Make sure it's a main window, not a child window
                if (text.Length > 0)
                {
                    Console.WriteLine($"Found window: '{text}' (PID: {windowProcessId})");
                    result = hWnd;
                    return false; // Stop enumeration
                }
            }
            return true; // Continue enumeration
        }, IntPtr.Zero);

        return result;
    }

    [TestMethod]
    public void CaptureMainWindowScreenshot()
    {
        // Verify app exists
        if (!File.Exists(_appPath))
        {
            Assert.Fail($"MAUI app not found at: {_appPath}\n\n" +
                       "Please build the GitWizardMAUI project first:\n" +
                       "  dotnet build GitWizardMAUI\\GitWizardMAUI.csproj -f net10.0-windows10.0.19041.0");
        }

        Console.WriteLine($"Launching app: {_appPath}");

        Process? appProcess = null;
        try
        {
            // Launch the app
            appProcess = Process.Start(new ProcessStartInfo
            {
                FileName = _appPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            });

            Assert.IsNotNull(appProcess, "Failed to start application");

            // Wait for the app to fully initialize and render
            Console.WriteLine("Waiting for app to initialize...");

            // Wait and periodically check if process is still alive
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(500);
                appProcess.Refresh();

                if (appProcess.HasExited)
                {
                    Assert.Fail(
                        $"Application exited with code {appProcess.ExitCode} before creating a window.\n\n" +
                        "This usually means Windows App SDK runtime is not installed.\n" +
                        "Please install it from: https://aka.ms/windowsappsdk/1.6/latest/windowsappruntimeinstall-x64.exe\n\n" +
                        "Or install via winget:\n" +
                        "  winget install Microsoft.WindowsAppRuntime.1.6");
                }
            }

            // Find the window by process ID
            Console.WriteLine($"Looking for window with process ID: {appProcess.Id}");
            IntPtr windowHandle = FindWindowByProcessId(appProcess.Id);

            if (windowHandle == IntPtr.Zero)
            {
                Assert.Fail($"Could not find window for process {appProcess.Id}. The app may not have created a window yet.");
            }

            // Get window dimensions
            if (!GetWindowRect(windowHandle, out RECT rect))
            {
                Assert.Fail("Failed to get window dimensions");
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            Console.WriteLine($"Window size: {width}x{height}");

            // Capture the window
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();

            try
            {
                PrintWindow(windowHandle, hdc, 0);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            // Save screenshot
            var screenshotPath = Path.Combine(_repoRoot, "maui-ui.png");
            bitmap.Save(screenshotPath, ImageFormat.Png);

            Console.WriteLine($"Screenshot saved to: {screenshotPath}");
            Assert.IsTrue(File.Exists(screenshotPath), "Screenshot file should exist");

            // Also save info about the screenshot
            var info = new FileInfo(screenshotPath);
            Console.WriteLine($"Screenshot size: {info.Length / 1024} KB");
        }
        finally
        {
            // Clean up - close the app
            if (appProcess != null && !appProcess.HasExited)
            {
                Console.WriteLine("Closing application...");
                try
                {
                    appProcess.Kill();
                    appProcess.WaitForExit(5000);
                }
                catch
                {
                    // Ignore errors during cleanup
                }
            }
            appProcess?.Dispose();
        }
    }

    [TestMethod]
    public void CaptureAfterRefreshScreenshot()
    {
        // This is trickier without UI automation, but we can capture after a longer delay
        // Verify app exists
        if (!File.Exists(_appPath))
        {
            Assert.Fail($"MAUI app not found at: {_appPath}");
        }

        Console.WriteLine($"Launching app: {_appPath}");

        Process? appProcess = null;
        try
        {
            // Launch the app
            appProcess = Process.Start(new ProcessStartInfo
            {
                FileName = _appPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            });

            Assert.IsNotNull(appProcess, "Failed to start application");

            // Wait longer for data to load (assumes auto-refresh on startup)
            Console.WriteLine("Waiting for app to load data...");
            Thread.Sleep(10000); // Wait 10 seconds for refresh

            // Find the window by process ID
            Console.WriteLine($"Looking for window with process ID: {appProcess.Id}");
            IntPtr windowHandle = FindWindowByProcessId(appProcess.Id);

            if (windowHandle == IntPtr.Zero)
            {
                Assert.Fail($"Could not find window for process {appProcess.Id}");
            }

            // Capture window
            GetWindowRect(windowHandle, out RECT rect);
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();

            try
            {
                PrintWindow(windowHandle, hdc, 0);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            var screenshotPath = Path.Combine(_repoRoot, "maui-ui-refreshed.png");
            bitmap.Save(screenshotPath, ImageFormat.Png);

            Console.WriteLine($"Refreshed screenshot saved to: {screenshotPath}");
        }
        finally
        {
            if (appProcess != null && !appProcess.HasExited)
            {
                appProcess.Kill();
                appProcess.WaitForExit(5000);
            }
            appProcess?.Dispose();
        }
    }
}

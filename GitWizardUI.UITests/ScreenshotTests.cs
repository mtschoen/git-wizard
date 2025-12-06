using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GitWizardUI.UITests
{
    [TestClass]
    public sealed class ScreenshotTests
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // TODO: Get path from configuration/environment somehow
        static readonly string k_AppPath = Path.GetFullPath("../../../../GitWizardUI/bin/Debug/net10.0-windows10.0.19041.0/win-x64/GitWizardUI.exe");
        static readonly string k_ScreenshotPath = Path.GetFullPath("../../../../Screenshots");

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, int nFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        static IntPtr FindWindowByProcessId(int processId)
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
            Console.WriteLine($"Launching app: {k_AppPath}");

            Process? appProcess = null;
            try
            {
                // Launch the app
                appProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = k_AppPath,
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

                if (!Directory.Exists(k_ScreenshotPath))
                    Directory.CreateDirectory(k_ScreenshotPath);

                // Save screenshot
                var screenshotPath = Path.Combine(k_ScreenshotPath, "maui-ui.png");
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
    }
}

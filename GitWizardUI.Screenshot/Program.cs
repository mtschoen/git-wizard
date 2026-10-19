using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;

namespace GitWizardUI.Screenshot;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            // SetupWithClassicDesktopLifetime initializes the application without running the
            // blocking main loop (StartWithClassicDesktopLifetime never returns until shutdown).
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithClassicDesktopLifetime(args);

            var app = Application.Current as App;
            var lifetime = app?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var window = lifetime?.MainWindow;

            if (window == null)
            {
                Console.Error.WriteLine("ERROR: MainWindow is null");
                return 1;
            }

            window.Show();

            // Headless Avalonia has no running main loop here: pump the UI thread by hand so layout,
            // bindings and the first render settle before the frame is captured.
            for (var pump = 0; pump < 60; pump++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(100);
            }

            // The first argument overrides the output path; the default is the file the docs reference.
            var screenshotPath = Path.GetFullPath(
                args.Length > 0 ? args[0] : Path.Combine("Screenshots", "GitWizardUI.png"));
            var screenshotDir = Path.GetDirectoryName(screenshotPath);
            if (!string.IsNullOrEmpty(screenshotDir) && !Directory.Exists(screenshotDir))
                Directory.CreateDirectory(screenshotDir);

            // Trigger rendering and capture the rendered frame
            using var bitmap = window.CaptureRenderedFrame();

            if (bitmap == null)
            {
                Console.Error.WriteLine("ERROR: CaptureRenderedFrame returned null");
                return 1;
            }

            // The previous hand-rolled SKBitmap copy freed its pixel buffer before encoding (a crash);
            // WriteableBitmap.Save encodes the PNG itself.
            bitmap.Save(screenshotPath);

            var fileInfo = new FileInfo(screenshotPath);
            Console.WriteLine($"Screenshot captured: {screenshotPath} ({fileInfo.Length / 1024} KB)");

            window.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }
}

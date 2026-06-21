using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia;
using SkiaSharp;
using System;
using System.IO;
using System.Threading.Tasks;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            var builder = AppBuilder.Configure<GitWizardUI.App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .StartWithClassicDesktopLifetime(args);

            await Task.Delay(2000);

            var app = Application.Current as GitWizardUI.App;
            var lifetime = app?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var window = lifetime?.MainWindow;

            if (window == null)
            {
                Console.Error.WriteLine("ERROR: MainWindow is null");
                return 1;
            }

            var screenshotPath = Path.GetFullPath(
                Path.Combine("Screenshots", "GitWizardUI.png"));
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

            // Get pixel data using Lock() and copy to SKBitmap
            int width = (int)bitmap.Size.Width;
            int height = (int)bitmap.Size.Height;

            using var locked = bitmap.Lock();
            var pixelData = new byte[locked.RowBytes * locked.Size.Height];
            System.Runtime.InteropServices.Marshal.Copy(locked.Address, pixelData, 0, pixelData.Length);

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var skBitmap = new SKBitmap(info);
            var address = System.Runtime.InteropServices.Marshal.AllocHGlobal(pixelData.Length);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(pixelData, 0, address, pixelData.Length);
                skBitmap.InstallPixels(info, address, width * 4, (ref_, _) => { });
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(address);
            }

            using var image = SKImage.FromBitmap(skBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(screenshotPath);
            data.SaveTo(fs);

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

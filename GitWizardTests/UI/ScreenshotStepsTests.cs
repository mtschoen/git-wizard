using System.Text.Json;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using GitWizardTests.TestSupport;
using GitWizardUI.Screenshot;

namespace GitWizardTests;

public class ScreenshotStepsTests
{
    [TestCase("", "Screenshots/GitWizardUI.png", null)]
    [TestCase("frame.png", "frame.png", null)]
    [TestCase("frame.png --steps steps.json", "frame.png", "steps.json")]
    [TestCase("--steps steps.json frame.png", "frame.png", "steps.json")]
    [TestCase("--steps steps.json", "Screenshots/GitWizardUI.png", "steps.json")]
    public void Options_ParseOutputAndOptionalSteps(string arguments, string outputPath, string? stepsPath)
    {
        var options = ScreenshotOptions.Parse(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(options.OutputPath.Replace('\\', '/'), Is.EqualTo(outputPath));
            Assert.That(options.StepsPath, Is.EqualTo(stepsPath));
        }
    }

    [TestCase("--steps")]
    [TestCase("--unknown")]
    [TestCase("one.png two.png")]
    [TestCase("--steps one.json --steps two.json")]
    public void Options_RejectInvalidArguments(string arguments)
    {
        Assert.Throws<ArgumentException>(() => ScreenshotOptions.Parse(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Test]
    public void Load_PreservesOrderedSteps()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "steps.json");
        File.WriteAllText(path, """
            [{"action":"setText","watermark":"Search","text":"beta"},
             {"action":"click","name":"Sort"},
             {"action":"capture","name":"filtered"}]
            """);

        var steps = ScreenshotSteps.Load(path);

        Assert.That(steps, Is.EqualTo(new ScreenshotStep[]
        {
            new("setText", null, "Search", "beta"), new("click", "Sort"), new("capture", "filtered"),
        }));
    }

    [TestCase("{")]
    [TestCase("{}")]
    [TestCase("null")]
    [TestCase("[null]")]
    [TestCase("[{\"action\":\"unknown\"}]")]
    [TestCase("[{\"action\":\"click\"}]")]
    [TestCase("[{\"action\":\"setText\",\"name\":\"Search\"}]")]
    [TestCase("[{\"action\":\"setText\",\"name\":\"Search\",\"watermark\":\"Search\",\"text\":\"\"}]")]
    [TestCase("[{\"action\":\"setText\",\"name\":\"\",\"watermark\":\"Search\",\"text\":\"\"}]")]
    [TestCase("[{\"action\":\"capture\",\"name\":\"../escape\"}]")]
    [TestCase("[{\"action\":\"capture\",\"name\":\"frame\",\"typo\":true}]")]
    public void Load_RejectsMalformedFile(string json)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "steps.json");
        File.WriteAllText(path, json);

        Assert.Throws<JsonException>(() => ScreenshotSteps.Load(path));
    }

    [AvaloniaTest]
    public void Run_SetsTextClicksAndCapturesSettledState()
    {
        var search = new TextBox { Name = "Search", Watermark = "Search repositories" };
        var sort = new Button { Name = "Sort" };
        var status = new TextBlock();
        var commandStatus = new TextBlock();
        sort.Command = new CaptureCommand(commandStatus);
        sort.Click += (_, _) => Dispatcher.UIThread.Post(() => status.Text = search.Text);
        var window = new Window { Content = new StackPanel { Children = { search, sort, status } } };
        window.Show();
        try
        {
            var frames = new List<string>();
            ScreenshotSteps.Run(window,
            [
                new("setText", "Search", null, "alpha"),
                new("capture", "named"),
                new("setText", null, "Search repositories", "beta"),
                new("click", "Sort"),
                new("capture", "filtered"),
            ], () => Dispatcher.UIThread.RunJobs(), name => frames.Add($"{name}:{search.Text}:{status.Text}"));

            using (Assert.EnterMultipleScope())
            {
                string[] expectedFrames = ["named:alpha:", "filtered:beta:beta"];
                Assert.That(frames, Is.EqualTo(expectedFrames));
                Assert.That(commandStatus.Text, Is.EqualTo("command executed"));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase("click", "Missing", null)]
    [TestCase("setText", "Missing", null)]
    [TestCase("setText", null, "Missing watermark")]
    public void Run_ReportsMissingControl(string action, string? name, string? watermark)
    {
        var window = new Window { Content = new TextBox() };
        window.Show();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ScreenshotSteps.Run(window,
                [new(action, name, watermark, "beta")], () => Dispatcher.UIThread.RunJobs(), _ => Assert.Fail("No frame expected")));

            Assert.That(exception!.Message, Does.Contain(name ?? watermark));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public void Run_RejectsAmbiguousWatermark()
    {
        var window = new Window
        {
            Content = new StackPanel
            {
                Children = { new TextBox { Watermark = "Search" }, new TextBox { Watermark = "Search" } },
            },
        };
        window.Show();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ScreenshotSteps.Run(window,
                [new("setText", null, "Search", "beta")], () => Dispatcher.UIThread.RunJobs(), _ => Assert.Fail("No frame expected")));

            Assert.That(exception!.Message, Does.Contain("watermark 'Search', found 2"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    [TestCase("unknown", "Unknown screenshot action")]
    [TestCase("capture", "Capture step requires a name")]
    public void Run_RejectsInvalidDirectSteps(string action, string message)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ScreenshotSteps.Run(new Window(),
            [new(action)], () => Dispatcher.UIThread.RunJobs(), _ => Assert.Fail("No frame expected")));

        Assert.That(exception!.Message, Does.Contain(message));
    }

    sealed class CaptureCommand(TextBlock status) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => status.Text = "command executed";
    }
}

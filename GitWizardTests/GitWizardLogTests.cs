using GitWizard;

namespace GitWizardTests;

public class GitWizardLogTests
{
    [SetUp]
    public void SetUp()
    {
        GitWizardLog.VerboseMode = false;
        GitWizardLog.SilentMode = false;
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.VerboseMode = false;
        GitWizardLog.SilentMode = false;
        GitWizardLog.LogMethod = Console.WriteLine;
    }

    [Test]
    public void Log_SilentMode_DoesNotOutput()
    {
        GitWizardLog.SilentMode = true;
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("Test message");

        Assert.That(output, Is.Empty);
    }

    [Test]
    public void Log_VerboseMode_DoesNotOutputVerbose()
    {
        GitWizardLog.VerboseMode = false;
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("Verbose message", GitWizardLog.LogType.Verbose);

        Assert.That(output, Is.Empty);
    }

    [Test]
    public void Log_VerboseMode_OutputsVerbose()
    {
        GitWizardLog.VerboseMode = true;
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("Verbose message", GitWizardLog.LogType.Verbose);

        Assert.That(output, Is.Not.Empty);
        Assert.That(output, Does.Contain("Verbose message"));
    }

    [Test]
    public void Log_OutputsFormattedMessage()
    {
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("Hello world");

        Assert.That(output, Does.Contain("Hello world"));
        Assert.That(output, Does.Contain("Info"));
        Assert.That(output, Does.Contain("["));
    }

    [Test]
    public void Log_ErrorType_OutputsError()
    {
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("Error occurred", GitWizardLog.LogType.Error);

        Assert.That(output, Does.Contain("Error"));
    }

    [Test]
    public void Log_WarningType_OutputsWarning()
    {
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("Warning message", GitWizardLog.LogType.Warning);

        Assert.That(output, Does.Contain("Warning"));
    }

    [Test]
    public void LogException_OutputsExceptionMessage()
    {
        var output = string.Empty;
        GitWizardLog.LogMethod = msg =>
        {
            if (output.Length == 0 && msg is not null)
                output = msg;
        };

        var exception = new InvalidOperationException("Something went wrong");
        GitWizardLog.LogException(exception);

        Assert.That(output, Does.Contain("Something went wrong"));
    }

    [Test]
    public void LogException_WithMessage_OutputsMessage()
    {
        var outputs = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg is not null) outputs.Add(msg); };

        var exception = new InvalidOperationException("Inner error");
        GitWizardLog.LogException(exception, "Context message");

        Assert.That(outputs, Has.Count.GreaterThan(0));
        Assert.That(outputs[0], Does.Contain("Context message"));
    }

    [Test]
    public void LogException_WithInnerException_OutputsInner()
    {
        var outputs = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg is not null) outputs.Add(msg); };

        var inner = new InvalidOperationException("Inner detail");
        var outer = new AggregateException("Outer", inner);
        GitWizardLog.LogException(outer);

        Assert.That(string.Join("\n", outputs), Does.Contain("Inner detail"));
    }

    [Test]
    public void LogException_InnerExceptionLabel_ShowsInnerExceptionText()
    {
        var outputs = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg is not null) outputs.Add(msg); };

        var inner = new InvalidOperationException("Inner exception text");
        var outer = new InvalidOperationException("Outer exception", inner);
        GitWizardLog.LogException(outer);

        Assert.That(outputs, Does.Contain("Inner exception:"));
    }

    [Test]
    public void LogMethod_CanBeOverridden()
    {
        var outputs = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg is not null) outputs.Add(msg); };

        GitWizardLog.Log("Custom output");

        Assert.That(outputs, Has.Count.EqualTo(1));
        Assert.That(outputs[0], Does.Contain("Custom output"));
    }

    [Test]
    public void LogMethod_LogsDateTimeFormat()
    {
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("Format test");

        // Should contain timestamp pattern [yyyy-MM-dd|HH:mm:ss.ffff]
        Assert.That(output, Does.Match(@"\[\d{4}-\d{2}-\d{2}\|"));
    }

    [Test]
    public void VerboseMode_PropertyDefaultIsFalse()
    {
        Assert.That(GitWizardLog.VerboseMode, Is.False);
    }

    [Test]
    public void SilentMode_PropertyDefaultIsFalse()
    {
        Assert.That(GitWizardLog.SilentMode, Is.False);
    }

    [Test]
    public void LogMethod_PropertyHasDefaultImplementation()
    {
        Assert.That(GitWizardLog.LogMethod, Is.Not.Null);
    }

    [Test]
    public void LogType_ValuesAreDistinct()
    {
        var values = new[] {
            GitWizardLog.LogType.Verbose,
            GitWizardLog.LogType.Info,
            GitWizardLog.LogType.Warning,
            GitWizardLog.LogType.Error
        };
        Assert.That(values.Distinct().Count(), Is.EqualTo(values.Length));
    }

    [Test]
    public void Log_DoesNotThrowWithCustomLogMethod()
    {
        var captured = string.Empty;
        GitWizardLog.LogMethod = msg => { captured = msg!; };

        GitWizardLog.Log("Test");

        Assert.That(captured, Does.Contain("Test"));
    }

    [Test]
    public void Log_MultipleMessages_CanBeCaptured()
    {
        var outputs = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg is not null) outputs.Add(msg); };

        GitWizardLog.Log("First");
        GitWizardLog.Log("Second");
        GitWizardLog.Log("Third");

        Assert.That(outputs, Has.Count.EqualTo(3));
        Assert.That(outputs[0], Does.Contain("First"));
        Assert.That(outputs[1], Does.Contain("Second"));
        Assert.That(outputs[2], Does.Contain("Third"));
    }

    [Test]
    public void Log_SilentMode_DisablesAllTypes()
    {
        GitWizardLog.SilentMode = true;
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("Info");
        Assert.That(output, Is.Empty);

        GitWizardLog.Log("Error", GitWizardLog.LogType.Error);
        Assert.That(output, Is.Empty);

        GitWizardLog.Log("Verbose", GitWizardLog.LogType.Verbose);
        Assert.That(output, Is.Empty);

        GitWizardLog.Log("Warning", GitWizardLog.LogType.Warning);
        Assert.That(output, Is.Empty);
    }

    [Test]
    public void CloseCurrentLogFile_DoesNotThrow()
    {
        // Should not throw even when no log file is open
        GitWizardLog.CloseCurrentLogFile();
        Assert.Pass();
    }

    [Test]
    public void CloseCurrentLogFile_IsIdempotent()
    {
        // Calling CloseCurrentLogFile multiple times should not throw
        GitWizardLog.CloseCurrentLogFile();
        GitWizardLog.CloseCurrentLogFile();
        GitWizardLog.CloseCurrentLogFile();
        Assert.Pass();
    }

    [Test]
    public void Log_LogsMessage_ToLogFile()
    {
        GitWizardLog.SilentMode = false;
        GitWizardLog.LogMethod = _ => { }; // suppress console

        GitWizardLog.Log("async file log test message");

        // Give the async writer a moment to process
        Thread.Sleep(50);

        GitWizardLog.CloseCurrentLogFile();

        var logFolder = GitWizardApi.GetLogFolderPath();
        var files = Directory.GetFiles(logFolder, "GitWizardLog_*.log");
        Assert.That(files.Length, Is.GreaterThan(0));
        var content = File.ReadAllText(files[0]);
        Assert.That(content, Does.Contain("async file log test message"));
    }

    [Test]
    public void Log_OutputAppearsInLogMethod()
    {
        GitWizardLog.SilentMode = false;
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { if (msg is not null) output = msg; };

        GitWizardLog.Log("Test message for log file");

        Assert.That(output, Does.Contain("Test message for log file"));
    }
}

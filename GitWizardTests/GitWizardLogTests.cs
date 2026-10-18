using System.Threading.Channels;
using GitWizard;

namespace GitWizardTests;

[NonParallelizable]
public class GitWizardLogTests
{
    Action<string?> _originalLogMethod = Console.WriteLine;
    bool _originalSilentMode;
    bool _originalVerboseMode;

    [SetUp]
    public void SetUp()
    {
        _originalLogMethod = GitWizardLog.LogMethod;
        _originalSilentMode = GitWizardLog.SilentMode;
        _originalVerboseMode = GitWizardLog.VerboseMode;
        GitWizardLog.CloseCurrentLogFile();
        GitWizardLog.VerboseMode = false;
        GitWizardLog.SilentMode = false;
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            GitWizardLog.CloseCurrentLogFile();
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            GitWizardLog.VerboseMode = _originalVerboseMode;
            GitWizardLog.SilentMode = _originalSilentMode;
            GitWizardLog.LogMethod = _originalLogMethod;
        }
    }

    static async Task CompleteFileWriterAsync()
    {
        var channel = GitWizardLog._logChannel;
        var writerTask = GitWizardLog._writerTask!;
        Assert.That(channel.Writer.TryComplete(), Is.True,
            "Test must own a live channel before completing it.");
        await writerTask.WaitAsync(TimeSpan.FromSeconds(30));
        GitWizardLog._logChannel = Channel.CreateBounded<string>(1);
    }

    static async Task<string> ReadLogFilesAsync()
    {
        var files = Directory.GetFiles(GitWizardApi.GetLogFolderPath(), "GitWizardLog_*.log");
        Assert.That(files, Is.Not.Empty, "The real file sink created no log file.");
        var contents = await Task.WhenAll(files.Order(StringComparer.Ordinal)
            .Select(path => File.ReadAllTextAsync(path)));
        return string.Join(Environment.NewLine, contents);
    }

    static InvalidOperationException CaptureNestedException(string token)
    {
        try
        {
            try
            {
                throw new ArgumentException($"inner-{token}");
            }
            catch (ArgumentException inner)
            {
                throw new InvalidOperationException($"outer-{token}", inner);
            }
        }
        catch (InvalidOperationException outer)
        {
            return outer;
        }
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
    public async Task Log_LogsMessage_ToLogFile()
    {
        var marker = $"ordinary-{Guid.NewGuid():N}";
        GitWizardLog.LogMethod = _ => { };
        GitWizardLog.Log(marker);
        await CompleteFileWriterAsync();
        Assert.That(await ReadLogFilesAsync(), Does.Contain(marker),
            "Ordinary marker missing after writer completion; investigate the sink independently.");
    }

    [TestCase("nested", false)]
    [TestCase("nested", true)]
    [TestCase("no-context", false)]
    [TestCase("no-context", true)]
    [TestCase("never-thrown", false)]
    [TestCase("never-thrown", true)]
    [TestCase("aggregate", false)]
    [TestCase("aggregate", true)]
    public async Task LogException_PersistsCompleteRecord(string scenario, bool silent)
    {
        var token = Guid.NewGuid().ToString("N");
        var first = CaptureNestedException($"first-{token}");
        var second = CaptureNestedException($"second-{token}");
        Exception exception = scenario switch
        {
            "never-thrown" => new InvalidOperationException($"unthrown-{token}"),
            "aggregate" => new AggregateException($"aggregate-{token}", first, second),
            _ => first
        };
        var context = scenario == "no-context" ? null : $"context-{token}";
        var before = $"before-{token}";
        var after = $"after-{token}";
        var suppressed = $"suppressed-{token}";
        var outputs = new List<string?>();
        GitWizardLog.LogMethod = _ => { };
        GitWizardLog.Log(before);
        GitWizardLog.LogMethod = outputs.Add;
        GitWizardLog.SilentMode = silent;
        GitWizardLog.LogException(exception, context);

        var expected = new List<string?>();
        if (context != null && !silent)
        {
            Assert.That(outputs[0], Does.Contain($"Error: {context} Exception follows:"));
            expected.Add(outputs[0]);
        }
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current != exception)
                expected.Add("Inner exception:");
            expected.Add(current.Message);
            expected.Add(current.StackTrace);
        }
        Assert.That(outputs, Is.EqualTo(expected));
        GitWizardLog.LogMethod = _ => { };
        if (silent)
            GitWizardLog.Log(suppressed);
        GitWizardLog.SilentMode = false;
        GitWizardLog.Log(after);
        await CompleteFileWriterAsync();
        var content = await ReadLogFilesAsync();
        var start = content.IndexOf(before, StringComparison.Ordinal);
        var end = content.IndexOf(after, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), "Ordinary leading marker missing.");
        Assert.That(end, Is.GreaterThan(start), "Ordinary trailing marker missing or reordered.");
        var record = content[(start + before.Length)..end];
        Assert.That(record, Does.Contain(exception.ToString()));
        Assert.That(record, Does.Not.Contain(suppressed));
        if (context == null)
            Assert.That(record, Does.Not.Contain("Exception follows:"));
        else
        {
            var header = record.IndexOf($"{context} Exception follows:", StringComparison.Ordinal);
            Assert.That(header, Is.GreaterThanOrEqualTo(0));
            Assert.That(record.IndexOf(exception.Message, StringComparison.Ordinal), Is.GreaterThan(header));
        }
        Exception[] details = scenario switch
        {
            "aggregate" => [exception, first, first.InnerException!, second, second.InnerException!],
            "never-thrown" => [exception],
            _ => [first, first.InnerException!]
        };
        foreach (var detail in details)
        {
            Assert.That(record, Does.Contain(detail.GetType().FullName!));
            Assert.That(record, Does.Contain(detail.Message));
            if (detail.StackTrace != null)
            {
                Assert.That(detail.StackTrace, Does.Contain(nameof(CaptureNestedException)));
                Assert.That(record, Does.Contain(detail.StackTrace));
            }
        }
        if (scenario == "never-thrown")
            Assert.That(exception.StackTrace, Is.Null);
        else
        {
            Assert.That(first.StackTrace, Is.Not.Null);
            Assert.That(first.InnerException!.StackTrace, Is.Not.Null);
            if (scenario == "aggregate")
            {
                Assert.That(second.StackTrace, Is.Not.Null);
                Assert.That(second.InnerException!.StackTrace, Is.Not.Null);
            }
        }
    }

    [Test]
    public async Task LogException_CallbackLogging_DoesNotSplitFileRecord()
    {
        var token = Guid.NewGuid().ToString("N");
        var context = $"context-{token}";
        var marker = $"intervening-{token}";
        var exception = CaptureNestedException(token);
        GitWizardLog.LogMethod = text =>
        {
            if (text == exception.Message)
                GitWizardLog.Log(marker);
        };
        GitWizardLog.LogException(exception, context);
        await CompleteFileWriterAsync();
        var content = await ReadLogFilesAsync();
        var header = content.IndexOf($"{context} Exception follows:", StringComparison.Ordinal);
        var end = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.That(end, Is.GreaterThanOrEqualTo(0), "Intervening ordinary marker missing.");
        Assert.That(header, Is.GreaterThanOrEqualTo(0), "Exception context missing.");
        Assert.That(end, Is.GreaterThan(header));
        Assert.That(content[header..end], Does.Contain(exception.ToString()));
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

    [Test]
    public void Log_VerboseMode_DoesNotOutputDebug()
    {
        GitWizardLog.VerboseMode = false;
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("Debug message", GitWizardLog.LogType.Debug);

        Assert.That(output, Is.Empty);
    }

    [Test]
    public void Log_VerboseMode_OutputsDebug()
    {
        GitWizardLog.VerboseMode = true;
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("Debug message", GitWizardLog.LogType.Debug);

        Assert.That(output, Does.Contain("Debug message"));
        Assert.That(output, Does.Contain("Debug"));
    }
}


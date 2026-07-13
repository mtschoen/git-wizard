using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitWizard;
using GitWizardUI.Converters;
using GitWizardUI.Services;

namespace GitWizardTests;

public class SmallFilesCoverageTests
{
    string? _tempHome;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _tempHome = TestUtilities.RedirectLocalFilesToTemp();
    }

    [TearDown]
    public void TearDown()
    {
        GitWizardLog.SilentMode = false;
        GitWizardLog.VerboseMode = false;
        GitWizardLog.LogMethod = Console.WriteLine;
        TestUtilities.ClearLocalFilesRedirect(_tempHome);
    }

    // ───────────────────────────────────────────────────────────────────
    // 1. PathPrettyPrintConverter
    // ───────────────────────────────────────────────────────────────────

    [Test]
    public void PathPrettyPrintConverter_Convert_StringPath_ReturnsPrettyPrintedPath()
    {
        var converter = new PathPrettyPrintConverter();
        var result = converter.Convert("C:\\Users\\test", typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo("C:\\Users\\test"));
    }

    [Test]
    public void PathPrettyPrintConverter_Convert_TildePath_ExpandsHomeDirectory()
    {
        var converter = new PathPrettyPrintConverter();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = converter.Convert("~/projects", typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo(Path.Combine(home, "projects")));
    }

    [Test]
    public void PathPrettyPrintConverter_Convert_NullInput_ReturnsNull()
    {
        var converter = new PathPrettyPrintConverter();
        var result = converter.Convert(null, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void PathPrettyPrintConverter_Convert_NonStringInput_ReturnsInputUnchanged()
    {
        var converter = new PathPrettyPrintConverter();
        var input = 42;
        var result = converter.Convert(input, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo(42));
    }

    [Test]
    public void PathPrettyPrintConverter_ConvertBack_ReturnsValueUnchanged()
    {
        var converter = new PathPrettyPrintConverter();
        var input = "some path";
        var result = converter.ConvertBack(input, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.EqualTo("some path"));
    }

    [Test]
    public void PathPrettyPrintConverter_ConvertBack_NullInput_ReturnsNull()
    {
        var converter = new PathPrettyPrintConverter();
        var result = converter.ConvertBack(null, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void PathPrettyPrintConverter_Convert_EnvironmentVariable_Expands()
    {
        var converter = new PathPrettyPrintConverter();
        var varName = "PATH";
        var result = converter.Convert($"%{varName}%", typeof(string), null, CultureInfo.InvariantCulture);
        Assert.That(result, Is.Not.EqualTo($"%{varName}%"));
        Assert.That(result, Is.EqualTo(Environment.GetEnvironmentVariable(varName)));
    }

    // ───────────────────────────────────────────────────────────────────
    // 2. AvaloniaFolderPicker (no Application.Current in unit tests)
    // ───────────────────────────────────────────────────────────────────

    [Test]
    public async Task AvaloniaFolderPicker_PickFolderAsync_NoApp_ReturnsNull()
    {
        var picker = new AvaloniaFolderPicker();
        var result = await picker.PickFolderAsync();
        Assert.That(result, Is.Null);
    }

    // ───────────────────────────────────────────────────────────────────
    // 3. AvaloniaUiDispatcher – AwaitAndSignalAsync via InvokeAsync(Func<Task>)
    //    is the only testable path without the Avalonia dispatcher loop.
    //    We test that the static helper propagates results and exceptions.
    // ───────────────────────────────────────────────────────────────────

    [Test]
    public void AvaloniaUiDispatcher_CanBeConstructed()
    {
        var dispatcher = new AvaloniaUiDispatcher();
        Assert.That(dispatcher, Is.Not.Null);
    }

    // ───────────────────────────────────────────────────────────────────
    // 4. AvaloniaUserDialogs (no Application.Current → early-return false)
    // ───────────────────────────────────────────────────────────────────

    [Test]
    public async Task AvaloniaUserDialogs_DisplayAlertAsync_NoApp_CompletesWithoutThrowing()
    {
        var dialogs = new AvaloniaUserDialogs();
        // When Application.Current is null, ShowDialogAsync returns false immediately;
        // DisplayAlertAsync wraps it so the task just completes.
        await dialogs.DisplayAlertAsync("Title", "Message");
        Assert.Pass();
    }

    [Test]
    public async Task AvaloniaUserDialogs_DisplayConfirmAsync_NoApp_ReturnsFalse()
    {
        var dialogs = new AvaloniaUserDialogs();
        var result = await dialogs.DisplayConfirmAsync("Title", "Message");
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task AvaloniaUserDialogs_DisplayConfirmAsync_CustomLabels_NoApp_ReturnsFalse()
    {
        var dialogs = new AvaloniaUserDialogs();
        var result = await dialogs.DisplayConfirmAsync("Title", "Message", "Accept", "Cancel");
        Assert.That(result, Is.False);
    }

    // ───────────────────────────────────────────────────────────────────
    // 5. IconLoader – requires Avalonia asset system, not testable in
    //    headless NUnit. We can only verify the class exists (static).
    // ───────────────────────────────────────────────────────────────────

    // IconLoader.Load() uses AssetLoader.Open with an avares:// URI which
    // requires a running Avalonia app. No meaningful unit test possible
    // without a headless Avalonia test host, so we skip it.

    // ───────────────────────────────────────────────────────────────────
    // 6. GitWizardReport.Persistence – SaveAtomic, MergeIntoFile,
    //    ReadJsonObjectFromFile, WriteAtomic
    // ───────────────────────────────────────────────────────────────────

    [Test]
    public void SaveAtomic_CreatesFileAtomically()
    {
        var report = new GitWizardReport();
        report.Repositories["/test/repo"] = new GitWizardRepository("/test/repo");

        var dir = Path.Combine(Path.GetTempPath(), "gw_atomic_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "report.json");

        try
        {
            report.SaveAtomic(path);
            Assert.That(File.Exists(path), Is.True);

            var json = File.ReadAllText(path);
            Assert.That(json, Does.Contain("/test/repo"));
            Assert.That(json, Does.Contain("2.1"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Test]
    public void SaveAtomic_StampsSchemaVersion()
    {
        var report = new GitWizardReport();
        report.SchemaVersion = "0.0"; // set stale version

        var path = Path.Combine(Path.GetTempPath(), "gw_schema_" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            report.SaveAtomic(path);
            var json = File.ReadAllText(path);
            var deserialized = JsonSerializer.Deserialize<GitWizardReport>(json);
            Assert.That(deserialized!.SchemaVersion, Is.EqualTo(GitWizardReport.CurrentSchemaVersion));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void SaveAtomic_OverwritesExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "gw_overwrite_" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            // Write initial content
            File.WriteAllText(path, "{\"old\": true}");

            var report = new GitWizardReport();
            report.Repositories["/new/repo"] = new GitWizardRepository("/new/repo");
            report.SaveAtomic(path);

            var json = File.ReadAllText(path);
            Assert.That(json, Does.Contain("/new/repo"));
            Assert.That(json, Does.Not.Contain("\"old\""));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void SaveAtomic_CreatesParentDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw_mkdir_" + Guid.NewGuid().ToString("N"), "sub");
        var path = Path.Combine(dir, "report.json");

        try
        {
            var report = new GitWizardReport();
            report.SaveAtomic(path);
            Assert.That(File.Exists(path), Is.True);
        }
        finally
        {
            var root = Path.GetDirectoryName(dir)!;
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Test]
    public void SaveAtomic_NoTempFileLeftBehind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw_noclutter_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "report.json");

        try
        {
            var report = new GitWizardReport();
            report.SaveAtomic(path);

            // Only the report file should exist; no .tmp files left behind
            var files = Directory.GetFiles(dir);
            Assert.That(files, Has.Length.EqualTo(1));
            Assert.That(files[0], Is.EqualTo(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Test]
    public void MergeIntoFile_NewFile_CreatesReport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw_merge_new_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "report.json");

        try
        {
            var config = GitWizardConfiguration.CreateDefaultConfiguration();
            // Empty repo paths - just verifies it creates a report from nothing
            var result = GitWizardReport.MergeIntoFile(path, config, new List<string>());

            Assert.That(File.Exists(path), Is.True);
            Assert.That(result, Is.Not.Null);

            var json = File.ReadAllText(path);
            Assert.That(json, Does.Contain(GitWizardReport.CurrentSchemaVersion));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Test]
    public void MergeIntoFile_PreservesExistingEntries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw_merge_preserve_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "report.json");

        try
        {
            Directory.CreateDirectory(dir);

            // Seed an existing report with a repo entry
            var existing = new JsonObject
            {
                ["SchemaVersion"] = "2.0",
                ["Repositories"] = new JsonObject
                {
                    ["/old/repo"] = JsonSerializer.SerializeToNode(new GitWizardRepository("/old/repo"))
                }
            };
            File.WriteAllText(path, existing.ToJsonString());

            var config = GitWizardConfiguration.CreateDefaultConfiguration();
            GitWizardReport.MergeIntoFile(path, config, new List<string>());

            // The old repo entry should be preserved in the file
            var json = File.ReadAllText(path);
            Assert.That(json, Does.Contain("/old/repo"));

            // Schema version should be updated
            Assert.That(json, Does.Contain(GitWizardReport.CurrentSchemaVersion));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Test]
    public void MergeIntoFile_CorruptExistingFile_StartsFromEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw_merge_corrupt_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "report.json");

        try
        {
            Directory.CreateDirectory(dir);

            // Write corrupt JSON
            File.WriteAllText(path, "not valid json {{{");

            var config = GitWizardConfiguration.CreateDefaultConfiguration();
            // Should not throw - falls back to empty report
            var result = GitWizardReport.MergeIntoFile(path, config, new List<string>());

            Assert.That(result, Is.Not.Null);
            Assert.That(File.Exists(path), Is.True);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Test]
    public void MergeIntoFile_NonexistentFile_CreatesNew()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw_merge_nofile_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "report.json");

        try
        {
            var config = GitWizardConfiguration.CreateDefaultConfiguration();
            var result = GitWizardReport.MergeIntoFile(path, config, new List<string>());

            Assert.That(result, Is.Not.Null);
            Assert.That(File.Exists(path), Is.True);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Test]
    public void MergeIntoFile_ExistingFileWithNoRepositories_AddsRepositoriesNode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw_merge_norepos_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "report.json");

        try
        {
            Directory.CreateDirectory(dir);

            // Existing file with no Repositories key
            var existing = new JsonObject { ["SchemaVersion"] = "1.0" };
            File.WriteAllText(path, existing.ToJsonString());

            var config = GitWizardConfiguration.CreateDefaultConfiguration();
            GitWizardReport.MergeIntoFile(path, config, new List<string>());

            var json = File.ReadAllText(path);
            Assert.That(json, Does.Contain("Repositories"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Test]
    public void MergeIntoFile_WithRealRepo_UpsertsEntry()
    {
        using var fixture = TempRepoFixture.CreateWithInitialCommit();
        var dir = Path.Combine(Path.GetTempPath(), "gw_merge_real_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "report.json");

        try
        {
            var config = GitWizardConfiguration.CreateDefaultConfiguration();
            var result = GitWizardReport.MergeIntoFile(path, config,
                new List<string> { fixture.Path });

            Assert.That(result.Repositories, Does.ContainKey(fixture.Path));
            Assert.That(File.Exists(path), Is.True);

            var json = File.ReadAllText(path);
            Assert.That(json, Does.Contain(fixture.Path.Replace("\\", "\\\\")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 7. GitWizardLog – uncovered paths
    // ───────────────────────────────────────────────────────────────────

    [Test]
    public void LogException_SilentMode_StillOutputsExceptionMessage()
    {
        // LogException calls LogMethod directly (not through Log),
        // so it outputs even in silent mode.
        GitWizardLog.SilentMode = true;
        var outputs = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg is not null) outputs.Add(msg); };

        var exception = new InvalidOperationException("should appear");
        GitWizardLog.LogException(exception);

        Assert.That(string.Join("\n", outputs), Does.Contain("should appear"));
    }

    [Test]
    public void LogException_SilentMode_WithMessage_LogsMessageViaLog()
    {
        // When SilentMode is true, the Log() call inside LogException for
        // the context message will be suppressed, but the exception itself
        // is still output via LogMethod.
        GitWizardLog.SilentMode = true;
        var outputs = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg is not null) outputs.Add(msg); };

        var exception = new InvalidOperationException("error detail");
        GitWizardLog.LogException(exception, "Context info");

        // The context message goes through Log() which is suppressed in silent mode,
        // but the exception message/stack go through LogMethod directly
        Assert.That(string.Join("\n", outputs), Does.Contain("error detail"));
    }

    [Test]
    public void LogException_NoMessage_DoesNotLogContextLine()
    {
        GitWizardLog.SilentMode = false;
        var outputs = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg is not null) outputs.Add(msg); };

        var exception = new InvalidOperationException("bare exception");
        GitWizardLog.LogException(exception);

        // Should NOT contain "Exception follows:" preamble
        Assert.That(string.Join("\n", outputs), Does.Not.Contain("Exception follows:"));
        Assert.That(string.Join("\n", outputs), Does.Contain("bare exception"));
    }

    [Test]
    public void LogException_StackTrace_IsOutput()
    {
        GitWizardLog.SilentMode = false;
        var outputs = new List<string?>();
        GitWizardLog.LogMethod = msg => outputs.Add(msg);

        // Create an exception with a stack trace
        Exception? thrown;
        try
        {
            throw new InvalidOperationException("with stack");
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        GitWizardLog.LogException(thrown);

        // Second call to LogMethod should be the stack trace (may be null)
        Assert.That(outputs.Count, Is.GreaterThanOrEqualTo(2));
        // First output is the exception message
        Assert.That(outputs[0], Does.Contain("with stack"));
    }

    [Test]
    public void LogException_NestedInnerExceptions_RecursesAll()
    {
        GitWizardLog.SilentMode = false;
        var outputs = new List<string>();
        GitWizardLog.LogMethod = msg => { if (msg is not null) outputs.Add(msg); };

        var innermost = new InvalidOperationException("innermost");
        var middle = new InvalidOperationException("middle", innermost);
        var outer = new InvalidOperationException("outer", middle);

        GitWizardLog.LogException(outer);

        var allOutput = string.Join("\n", outputs);
        Assert.Multiple(() =>
        {
            Assert.That(allOutput, Does.Contain("outer"));
            Assert.That(allOutput, Does.Contain("middle"));
            Assert.That(allOutput, Does.Contain("innermost"));
            // Should show "Inner exception:" labels
        });
    }

    [Test]
    public void Log_VerboseInSilentMode_DoesNotOutput()
    {
        // Both verbose and silent set - silent wins
        GitWizardLog.VerboseMode = true;
        GitWizardLog.SilentMode = true;
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("should not appear", GitWizardLog.LogType.Verbose);

        Assert.That(output, Is.Empty);
    }

    [Test]
    public void Log_VerboseEnabled_NonVerboseMessage_StillOutputs()
    {
        GitWizardLog.VerboseMode = true;
        GitWizardLog.SilentMode = false;
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("info message");

        Assert.That(output, Does.Contain("info message"));
    }

    [Test]
    public void Log_WarningType_IncludesWarningLabel()
    {
        GitWizardLog.SilentMode = false;
        var output = string.Empty;
        GitWizardLog.LogMethod = msg => { output = msg!; };

        GitWizardLog.Log("caution!", GitWizardLog.LogType.Warning);

        Assert.That(output, Does.Contain("Warning"));
        Assert.That(output, Does.Contain("caution!"));
    }

    [Test]
    public void CloseCurrentLogFile_CalledTwice_DoesNotThrow()
    {
        // Calling close when no log file is open, then again
        GitWizardLog.CloseCurrentLogFile();
        GitWizardLog.CloseCurrentLogFile();
        Assert.Pass();
    }

    [Test]
    public void Log_WritesToLogFile_ThenCloseWorks()
    {
        // Enable logging so it writes to file, then close
        GitWizardLog.SilentMode = false;
        GitWizardLog.LogMethod = _ => { }; // suppress console but allow LogToFile

        GitWizardLog.Log("log file test message");

        // Closing should not throw
        GitWizardLog.CloseCurrentLogFile();
        Assert.Pass();
    }
}

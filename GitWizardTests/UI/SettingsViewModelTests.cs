using GitWizard;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;

namespace GitWizardTests;

[NonParallelizable]
public class SettingsViewModelTests
{
    LocalFilesSandbox? _sandbox;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        // Redirect the data dir to temp: these tests construct SettingsViewModel (which loads
        // config) and SaveAsync/Save write it back. Without the redirect they read/write
        // the real ~/.GitWizard/config.json, which caused order-dependent failures.
        _sandbox = new LocalFilesSandbox();
    }

    [TearDown]
    public void TearDown()
    {
        _sandbox?.Dispose();
        _sandbox = null;
    }

    [Test]
    public void Construction_LoadsConfiguration()
    {
        var vm = TestViewModels.CreateSettings();

        Assert.That(vm.ForkPath, Is.Empty);
    }

    [Test]
    public void Construction_LoadsSkipHiddenDirectoriesFromConfiguration()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        Assert.That(vm.SkipHiddenDirectories, Is.False);
    }

    [Test]
    public void Construction_PopulatesSearchPathsFromConfiguration()
    {
        var picker = new StubFolderPicker();

        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        Assert.That(vm.SearchPaths, Has.Count.GreaterThan(0));
    }

    static IEnumerable<TestCaseData> PropertyNotificationCases()
    {
        yield return new TestCaseData("NewSearchPath",
            (Action<SettingsViewModel, object>)((viewModel, value) => viewModel.NewSearchPath = (string)value),
            (Func<SettingsViewModel, object>)(viewModel => viewModel.NewSearchPath),
            "/new/path");
        yield return new TestCaseData("NewIgnoredPath",
            (Action<SettingsViewModel, object>)((viewModel, value) => viewModel.NewIgnoredPath = (string)value),
            (Func<SettingsViewModel, object>)(viewModel => viewModel.NewIgnoredPath),
            "/ignored/new");
        yield return new TestCaseData("ForkPath",
            (Action<SettingsViewModel, object>)((viewModel, value) => viewModel.ForkPath = (string)value),
            (Func<SettingsViewModel, object>)(viewModel => viewModel.ForkPath),
            "/usr/local/bin/fork");
        yield return new TestCaseData("SkipHiddenDirectories",
            (Action<SettingsViewModel, object>)((viewModel, value) => viewModel.SkipHiddenDirectories = (bool)value),
            (Func<SettingsViewModel, object>)(viewModel => viewModel.SkipHiddenDirectories),
            false);
        yield return new TestCaseData("IncludeTempDirectories",
            (Action<SettingsViewModel, object>)((viewModel, value) => viewModel.IncludeTempDirectories = (bool)value),
            (Func<SettingsViewModel, object>)(viewModel => viewModel.IncludeTempDirectories),
            true);
    }

    [TestCaseSource(nameof(PropertyNotificationCases))]
    public void PropertySetter_SetsValueAndTriggersNotification(
        string propertyName,
        Action<SettingsViewModel, object> setProperty,
        Func<SettingsViewModel, object> getProperty,
        object value)
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        var changedNames = new List<string?>();

        vm.PropertyChanged += (_, e) => changedNames.Add(e.PropertyName);

        setProperty(vm, value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(getProperty(vm), Is.EqualTo(value));
            Assert.That(changedNames, Is.Not.Empty);
            Assert.That(changedNames, Is.All.EqualTo(propertyName));
        }
    }

    [Test]
    public void AddSearchPath_AddsNonEmptyPath()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        var initialCount = vm.SearchPaths.Count;
        vm.NewSearchPath = "/added/path";
        vm.AddSearchPath();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount + 1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.SearchPaths, Does.Contain("/added/path"));
            Assert.That(vm.NewSearchPath, Is.Empty);
        }
    }

    // A null candidate selects the duplicate case: an already-listed path is reused.
    [TestCase("search", "")]
    [TestCase("search", "   ")]
    [TestCase("search", null)]
    [TestCase("ignored", "")]
    public void AddPath_IgnoresRejectedCandidate(string targetCollection, string? candidatePath)
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        var isSearchCollection = targetCollection == "search";
        var paths = isSearchCollection ? vm.SearchPaths : vm.IgnoredPaths;
        var initialCount = paths.Count;

        if (isSearchCollection)
        {
            vm.NewSearchPath = candidatePath ?? vm.SearchPaths[0];
            vm.AddSearchPath();
        }
        else
        {
            vm.NewIgnoredPath = candidatePath ?? vm.IgnoredPaths[0];
            vm.AddIgnoredPath();
        }

        Assert.That(paths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void AddIgnoredPath_AddsNonEmptyPath()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        var initialCount = vm.IgnoredPaths.Count;
        vm.NewIgnoredPath = "/ignored/added";
        vm.AddIgnoredPath();

        Assert.That(vm.IgnoredPaths, Has.Count.EqualTo(initialCount + 1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.IgnoredPaths, Does.Contain("/ignored/added"));
            Assert.That(vm.NewIgnoredPath, Is.Empty);
        }
    }

    [Test]
    public async Task AddSearchPathAsync_AddsPathFromPicker()
    {
        var picker = new StubFolderPicker { NextResult = "/picked/path" };
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        var initialCount = vm.SearchPaths.Count;
        await vm.AddSearchPathAsync();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount + 1));
        Assert.That(vm.SearchPaths, Does.Contain("/picked/path"));
    }

    [Test]
    public async Task AddSearchPathAsync_IgnoresNullPickerResult()
    {
        var picker = new StubFolderPicker { NextResult = null };
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        var initialCount = vm.SearchPaths.Count;
        await vm.AddSearchPathAsync();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public async Task AddSearchPathAsync_IgnoresEmptyPickerResult()
    {
        var picker = new StubFolderPicker { NextResult = "" };
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        var initialCount = vm.SearchPaths.Count;
        await vm.AddSearchPathAsync();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public async Task AddIgnoredPathAsync_AddsPathFromPicker()
    {
        var picker = new StubFolderPicker { NextResult = "/picked/ignored" };
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        var initialCount = vm.IgnoredPaths.Count;
        await vm.AddIgnoredPathAsync();

        Assert.That(vm.IgnoredPaths, Has.Count.EqualTo(initialCount + 1));
        Assert.That(vm.IgnoredPaths, Does.Contain("/picked/ignored"));
    }

    [Test]
    public void RemoveSelectedSearchPath_RemovesSelectedPath()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        vm.SelectedSearchPath = vm.SearchPaths[0];
        var initialCount = vm.SearchPaths.Count;

        vm.RemoveSelectedSearchPath();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount - 1));
    }

    [Test]
    public void RemoveSelectedSearchPath_IgnoresNullSelection()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        vm.SelectedSearchPath = null;
        var initialCount = vm.SearchPaths.Count;

        vm.RemoveSelectedSearchPath();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void RemoveSelectedIgnoredPath_RemovesSelectedPath()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        // Default IgnoredPaths are platform-specific (none on Linux/macOS); seed one so this is
        // cross-platform-deterministic rather than indexing [0] on a possibly-empty collection.
        vm.IgnoredPaths.Add("/seed/ignored");

        vm.SelectedIgnoredPath = vm.IgnoredPaths[0];
        var initialCount = vm.IgnoredPaths.Count;

        vm.RemoveSelectedIgnoredPath();

        Assert.That(vm.IgnoredPaths, Has.Count.EqualTo(initialCount - 1));
    }

    [Test]
    public void RemoveSelectedIgnoredPath_IgnoresNullSelection()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        vm.SelectedIgnoredPath = null;
        var initialCount = vm.IgnoredPaths.Count;

        vm.RemoveSelectedIgnoredPath();

        Assert.That(vm.IgnoredPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void Save_ExecutesSave()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        vm.SearchPaths.Add("/new/search");
        vm.ForkPath = "/usr/local/bin/fork";

        vm.Save();

        Assert.That(vm.ForkPath, Is.EqualTo("/usr/local/bin/fork"));
        var loaded = GitWizardConfiguration.GetGlobalConfiguration();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded.SearchPaths, Does.Contain("/new/search"));
            Assert.That(loaded.ForkPath, Is.EqualTo("/usr/local/bin/fork"));
        }
    }

    [Test]
    public async Task SaveAsync_SavesConfiguration()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        vm.SearchPaths.Add("/saved/search");
        vm.ForkPath = "/usr/bin/fork";

        await vm.SaveAsync();

        // Configuration should have been saved; reload and verify
        var loaded = await GitWizardConfiguration.GetGlobalConfigurationAsync();
        Assert.That(loaded.SearchPaths, Does.Contain("/saved/search"));
    }

    [Test]
    public void ResetToDefaults_RestoresDefaultSearchAndIgnoredPaths()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        vm.SearchPaths.Clear();
        vm.SearchPaths.Add("/custom/search");
        vm.IgnoredPaths.Clear();
        vm.IgnoredPaths.Add("/custom/ignored");

        vm.ResetToDefaults();

        var defaults = GitWizardConfiguration.CreateDefaultConfiguration();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.SearchPaths, Is.EquivalentTo(defaults.SearchPaths));
            Assert.That(vm.IgnoredPaths, Is.EquivalentTo(defaults.IgnoredPaths));
        }
    }

    static IEnumerable<TestCaseData> ResetToDefaultsCases()
    {
        yield return new TestCaseData("ForkPath",
            (Action<SettingsViewModel>)(viewModel => viewModel.ForkPath = "/custom/fork"),
            (Func<SettingsViewModel, object>)(viewModel => viewModel.ForkPath),
            string.Empty);
        yield return new TestCaseData("SkipHiddenDirectories",
            (Action<SettingsViewModel>)(viewModel => viewModel.SkipHiddenDirectories = true),
            (Func<SettingsViewModel, object>)(viewModel => viewModel.SkipHiddenDirectories),
            false);
        yield return new TestCaseData("IncludeTempDirectories",
            (Action<SettingsViewModel>)(viewModel => viewModel.IncludeTempDirectories = true),
            (Func<SettingsViewModel, object>)(viewModel => viewModel.IncludeTempDirectories),
            false);
    }

    [TestCaseSource(nameof(ResetToDefaultsCases))]
    public void ResetToDefaults_RestoresPropertyDefaults(
        string propertyName,
        Action<SettingsViewModel> deviateFromDefault,
        Func<SettingsViewModel, object> readProperty,
        object expectedDefault)
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        deviateFromDefault(vm);
        Assert.That(readProperty(vm), Is.Not.EqualTo(expectedDefault), "precondition: the property must deviate from its default");

        vm.ResetToDefaults();

        Assert.That(readProperty(vm), Is.EqualTo(expectedDefault), propertyName);
    }

    [Test]
    public void ResetToDefaults_PersistsAcrossReload()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        vm.ForkPath = "/custom/fork";
        vm.SearchPaths.Add("/custom/search");

        vm.ResetToDefaults();

        var reloaded = GitWizardConfiguration.GetGlobalConfiguration();
        var defaults = GitWizardConfiguration.CreateDefaultConfiguration();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reloaded.SearchPaths, Is.EquivalentTo(defaults.SearchPaths));
            Assert.That(reloaded.IgnoredPaths, Is.EquivalentTo(defaults.IgnoredPaths));
            Assert.That(reloaded.ForkPath, Is.Null.Or.Empty);
        }
    }

    [Test]
    public void Construction_LoadsIncludeTempDirectoriesDefaultUnchecked()
    {
        var vm = TestViewModels.CreateSettings();

        Assert.That(vm.IncludeTempDirectories, Is.False);
    }
}

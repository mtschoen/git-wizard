using System.Collections.ObjectModel;
using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

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
        var picker = new StubFolderPicker { NextResult = "/tmp/test" };

        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        Assert.That(vm, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.ForkPath, Is.Empty);
            Assert.That(vm.SearchPaths, Is.Not.Null);
            Assert.That(vm.IgnoredPaths, Is.Not.Null);
        }
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

    [Test]
    public void NewSearchPath_SetsValueAndTriggersNotification()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        var propertyChangedFired = false;
        var changedName = string.Empty;

        vm.PropertyChanged += (_, e) =>
        {
            propertyChangedFired = true;
            changedName = e.PropertyName;
        };

        vm.NewSearchPath = "/new/path";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.NewSearchPath, Is.EqualTo("/new/path"));
            Assert.That(propertyChangedFired, Is.True);
            Assert.That(changedName, Is.EqualTo("NewSearchPath"));
        }
    }

    [Test]
    public void NewIgnoredPath_SetsValueAndTriggersNotification()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        var propertyChangedFired = false;
        var changedName = string.Empty;

        vm.PropertyChanged += (_, e) =>
        {
            propertyChangedFired = true;
            changedName = e.PropertyName;
        };

        vm.NewIgnoredPath = "/ignored/new";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.NewIgnoredPath, Is.EqualTo("/ignored/new"));
            Assert.That(propertyChangedFired, Is.True);
            Assert.That(changedName, Is.EqualTo("NewIgnoredPath"));
        }
    }

    [Test]
    public void ForkPath_SetsValueAndTriggersSave()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        var propertyChangedFired = false;

        vm.PropertyChanged += (_, _) => propertyChangedFired = true;

        vm.ForkPath = "/usr/local/bin/fork";

        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.ForkPath, Is.EqualTo("/usr/local/bin/fork"));
            Assert.That(propertyChangedFired, Is.True);
        }
    }

    [Test]
    public void SkipHiddenDirectories_SetsValueAndTriggersNotification()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        var propertyChangedFired = false;
        var changedName = string.Empty;

        vm.PropertyChanged += (_, e) =>
        {
            propertyChangedFired = true;
            changedName = e.PropertyName ?? string.Empty;
        };

        vm.SkipHiddenDirectories = false;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.SkipHiddenDirectories, Is.False);
            Assert.That(propertyChangedFired, Is.True);
            Assert.That(changedName, Is.EqualTo("SkipHiddenDirectories"));
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

    [Test]
    public void AddSearchPath_IgnoresEmptyPath()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        var initialCount = vm.SearchPaths.Count;
        vm.NewSearchPath = string.Empty;
        vm.AddSearchPath();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void AddSearchPath_IgnoresWhitespacePath()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        var initialCount = vm.SearchPaths.Count;
        vm.NewSearchPath = "   ";
        vm.AddSearchPath();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void AddSearchPath_IgnoresDuplicatePath()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        var initialCount = vm.SearchPaths.Count;
        vm.NewSearchPath = vm.SearchPaths[0];
        vm.AddSearchPath();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
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
    public void AddIgnoredPath_IgnoresEmptyPath()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        var initialCount = vm.IgnoredPaths.Count;
        vm.NewIgnoredPath = string.Empty;
        vm.AddIgnoredPath();

        Assert.That(vm.IgnoredPaths, Has.Count.EqualTo(initialCount));
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
    public void OnPropertyChanged_FiresPropertyChange()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        var fired = false;

        vm.PropertyChanged += (_, e) =>
        {
            fired = true;
            Assert.That(e.PropertyName, Is.EqualTo("NewSearchPath"));
        };

        vm.NewSearchPath = "test";
        Assert.That(fired, Is.True);
    }

    [Test]
    public void OnPropertyChanged_WithCallerMemberName()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        string? firedProperty = null;

        vm.PropertyChanged += (_, e) => firedProperty = e.PropertyName;

        vm.ForkPath = "/fork/path";
        Assert.That(firedProperty, Is.EqualTo("ForkPath"));
    }

    [Test]
    public void SearchPaths_IsObservableCollection()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        Assert.That(vm.SearchPaths, Is.InstanceOf<ObservableCollection<string>>());
    }

    [Test]
    public void IgnoredPaths_IsObservableCollection()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);

        Assert.That(vm.IgnoredPaths, Is.InstanceOf<ObservableCollection<string>>());
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

    [Test]
    public void ResetToDefaults_ClearsForkPath()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        vm.ForkPath = "/custom/fork";

        vm.ResetToDefaults();

        Assert.That(vm.ForkPath, Is.Empty);
    }

    [Test]
    public void ResetToDefaults_ResetsSkipHiddenDirectories()
    {
        var picker = new StubFolderPicker();
        var vm = TestViewModels.CreateSettings(folderPicker: picker);
        vm.SkipHiddenDirectories = true;

        vm.ResetToDefaults();

        Assert.That(vm.SkipHiddenDirectories, Is.False);
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

    [Test]
    public void IncludeTempDirectories_SetsValueAndTriggersNotification()
    {
        var vm = TestViewModels.CreateSettings();
        string? changedName = null;
        vm.PropertyChanged += (_, e) => changedName = e.PropertyName;

        vm.IncludeTempDirectories = true;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(vm.IncludeTempDirectories, Is.True);
            Assert.That(changedName, Is.EqualTo("IncludeTempDirectories"));
        }
    }

    [Test]
    public void ResetToDefaults_ResetsIncludeTempDirectories()
    {
        var vm = TestViewModels.CreateSettings();
        vm.IncludeTempDirectories = true;

        vm.ResetToDefaults();

        Assert.That(vm.IncludeTempDirectories, Is.False);
    }
}

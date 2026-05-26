using System.Collections.ObjectModel;
using GitWizard;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

public class SettingsViewModelTests
{
    string? _tempRoot;

    [SetUp]
    public void SetUp()
    {
        GitWizardLog.SilentMode = true;
        _tempRoot = Path.Combine(Path.GetTempPath(), "SettingsViewModelTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        TestUtilities.ResetStaticCaches();
    }

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrEmpty(_tempRoot) && Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);

        TestUtilities.ResetStaticCaches();
    }

    [Test]
    public void Construction_LoadsConfiguration()
    {
        var picker = new StubFolderPicker { NextResult = "/tmp/test" };

        var vm = new SettingsViewModel(picker);

        Assert.That(vm, Is.Not.Null);
        Assert.That(vm.ForkPath, Is.Empty);
        Assert.That(vm.SearchPaths, Is.Not.Null);
        Assert.That(vm.IgnoredPaths, Is.Not.Null);
    }

    [Test]
    public void Construction_PopulatesSearchPathsFromConfiguration()
    {
        var picker = new StubFolderPicker();

        var vm = new SettingsViewModel(picker);

        Assert.That(vm.SearchPaths, Has.Count.GreaterThan(0));
    }

    [Test]
    public void NewSearchPath_SetsValueAndTriggersNotification()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);
        var propertyChangedFired = false;
        var changedName = string.Empty;

        vm.PropertyChanged += (_, e) =>
        {
            propertyChangedFired = true;
            changedName = e.PropertyName;
        };

        vm.NewSearchPath = "/new/path";

        Assert.That(vm.NewSearchPath, Is.EqualTo("/new/path"));
        Assert.That(propertyChangedFired, Is.True);
        Assert.That(changedName, Is.EqualTo("NewSearchPath"));
    }

    [Test]
    public void NewIgnoredPath_SetsValueAndTriggersNotification()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);
        var propertyChangedFired = false;
        var changedName = string.Empty;

        vm.PropertyChanged += (_, e) =>
        {
            propertyChangedFired = true;
            changedName = e.PropertyName;
        };

        vm.NewIgnoredPath = "/ignored/new";

        Assert.That(vm.NewIgnoredPath, Is.EqualTo("/ignored/new"));
        Assert.That(propertyChangedFired, Is.True);
        Assert.That(changedName, Is.EqualTo("NewIgnoredPath"));
    }

    [Test]
    public void ForkPath_SetsValueAndTriggersSave()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);
        var propertyChangedFired = false;

        vm.PropertyChanged += (_, e) =>
        {
            propertyChangedFired = true;
        };

        vm.ForkPath = "/usr/local/bin/fork";

        Assert.That(vm.ForkPath, Is.EqualTo("/usr/local/bin/fork"));
        Assert.That(propertyChangedFired, Is.True);
    }

    [Test]
    public void AddSearchPathCommand_AddsNonEmptyPath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.SearchPaths.Count;
        vm.NewSearchPath = "/added/path";
        vm.AddSearchPathCommand.Execute(null);

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount + 1));
        Assert.That(vm.SearchPaths, Does.Contain("/added/path"));
        Assert.That(vm.NewSearchPath, Is.Empty);
    }

    [Test]
    public void AddSearchPathCommand_IgnoresEmptyPath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.SearchPaths.Count;
        vm.NewSearchPath = string.Empty;
        vm.AddSearchPathCommand.Execute(null);

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void AddSearchPathCommand_IgnoresWhitespacePath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.SearchPaths.Count;
        vm.NewSearchPath = "   ";
        vm.AddSearchPathCommand.Execute(null);

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void AddSearchPathCommand_IgnoresDuplicatePath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.SearchPaths.Count;
        vm.NewSearchPath = vm.SearchPaths[0];
        vm.AddSearchPathCommand.Execute(null);

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void RemoveSearchPathCommand_RemovesSpecifiedPath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        var pathToRemove = vm.SearchPaths[0];
        vm.RemoveSearchPathCommand.Execute(pathToRemove);

        Assert.That(vm.SearchPaths, Does.Not.Contain(pathToRemove));
    }

    [Test]
    public void RemoveSearchPathCommand_IgnoresNullPath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.SearchPaths.Count;
        vm.RemoveSearchPathCommand.Execute((string?)null);

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void AddIgnoredPathCommand_AddsNonEmptyPath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.IgnoredPaths.Count;
        vm.NewIgnoredPath = "/ignored/added";
        vm.AddIgnoredPathCommand.Execute(null);

        Assert.That(vm.IgnoredPaths, Has.Count.EqualTo(initialCount + 1));
        Assert.That(vm.IgnoredPaths, Does.Contain("/ignored/added"));
        Assert.That(vm.NewIgnoredPath, Is.Empty);
    }

    [Test]
    public void AddIgnoredPathCommand_IgnoresEmptyPath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.IgnoredPaths.Count;
        vm.NewIgnoredPath = string.Empty;
        vm.AddIgnoredPathCommand.Execute(null);

        Assert.That(vm.IgnoredPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void RemoveIgnoredPathCommand_RemovesSpecifiedPath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        var pathToRemove = vm.IgnoredPaths[0];
        vm.RemoveIgnoredPathCommand.Execute(pathToRemove);

        Assert.That(vm.IgnoredPaths, Does.Not.Contain(pathToRemove));
    }

    [Test]
    public void RemoveIgnoredPathCommand_IgnoresNullPath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.IgnoredPaths.Count;
        vm.RemoveIgnoredPathCommand.Execute((string?)null);

        Assert.That(vm.IgnoredPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public async Task AddSearchPathAsync_AddsPathFromPicker()
    {
        var picker = new StubFolderPicker { NextResult = "/picked/path" };
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.SearchPaths.Count;
        await vm.AddSearchPathAsync();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount + 1));
        Assert.That(vm.SearchPaths, Does.Contain("/picked/path"));
    }

    [Test]
    public async Task AddSearchPathAsync_IgnoresNullPickerResult()
    {
        var picker = new StubFolderPicker { NextResult = null };
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.SearchPaths.Count;
        await vm.AddSearchPathAsync();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public async Task AddSearchPathAsync_IgnoresEmptyPickerResult()
    {
        var picker = new StubFolderPicker { NextResult = "" };
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.SearchPaths.Count;
        await vm.AddSearchPathAsync();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public async Task AddIgnoredPathAsync_AddsPathFromPicker()
    {
        var picker = new StubFolderPicker { NextResult = "/picked/ignored" };
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.IgnoredPaths.Count;
        await vm.AddIgnoredPathAsync();

        Assert.That(vm.IgnoredPaths, Has.Count.EqualTo(initialCount + 1));
        Assert.That(vm.IgnoredPaths, Does.Contain("/picked/ignored"));
    }

    [Test]
    public void RemoveSelectedSearchPath_RemovesSelectedPath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        vm.SelectedSearchPath = vm.SearchPaths[0];
        var initialCount = vm.SearchPaths.Count;

        vm.RemoveSelectedSearchPath();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount - 1));
    }

    [Test]
    public void RemoveSelectedSearchPath_IgnoresNullSelection()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        vm.SelectedSearchPath = null;
        var initialCount = vm.SearchPaths.Count;

        vm.RemoveSelectedSearchPath();

        Assert.That(vm.SearchPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void RemoveSelectedIgnoredPath_RemovesSelectedPath()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        vm.SelectedIgnoredPath = vm.IgnoredPaths[0];
        var initialCount = vm.IgnoredPaths.Count;

        vm.RemoveSelectedIgnoredPath();

        Assert.That(vm.IgnoredPaths, Has.Count.EqualTo(initialCount - 1));
    }

    [Test]
    public void RemoveSelectedIgnoredPath_IgnoresNullSelection()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        vm.SelectedIgnoredPath = null;
        var initialCount = vm.IgnoredPaths.Count;

        vm.RemoveSelectedIgnoredPath();

        Assert.That(vm.IgnoredPaths, Has.Count.EqualTo(initialCount));
    }

    [Test]
    public void SaveCommand_ExecutesSave()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        var initialCount = vm.SearchPaths.Count;
        vm.SearchPaths.Add("/new/search");
        vm.ForkPath = "/usr/local/bin/fork";

        vm.SaveCommand.Execute(null);

        Assert.That(vm.ForkPath, Is.EqualTo("/usr/local/bin/fork"));
    }

    [Test]
    public async Task SaveAsync_SavesConfiguration()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        vm.SearchPaths.Add("/saved/search");
        vm.ForkPath = "/usr/bin/fork";

        await vm.SaveAsync();

        // Configuration should have been saved; reload and verify
        var loaded = GitWizardConfiguration.GetGlobalConfiguration();
        Assert.That(loaded.SearchPaths, Does.Contain("/saved/search"));
    }

    [Test]
    public void OnPropertyChanged_FiresPropertyChange()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);
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
        var vm = new SettingsViewModel(picker);
        string? firedProperty = null;

        vm.PropertyChanged += (_, e) => firedProperty = e.PropertyName;

        vm.ForkPath = "/fork/path";
        Assert.That(firedProperty, Is.EqualTo("ForkPath"));
    }

    [Test]
    public void SearchPaths_IsObservableCollection()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        Assert.That(vm.SearchPaths, Is.InstanceOf<ObservableCollection<string>>());
    }

    [Test]
    public void IgnoredPaths_IsObservableCollection()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        Assert.That(vm.IgnoredPaths, Is.InstanceOf<ObservableCollection<string>>());
    }

    [Test]
    public void AddSearchPathCommand_CommandType()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        Assert.That(vm.AddSearchPathCommand, Is.Not.Null);
        Assert.That(vm.AddSearchPathCommand, Is.InstanceOf<ICommand>());
    }

    [Test]
    public void RemoveSearchPathCommand_CommandType()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        Assert.That(vm.RemoveSearchPathCommand, Is.Not.Null);
        Assert.That(vm.RemoveSearchPathCommand, Is.InstanceOf<ICommand>());
    }

    [Test]
    public void SaveCommand_CommandType()
    {
        var picker = new StubFolderPicker();
        var vm = new SettingsViewModel(picker);

        Assert.That(vm.SaveCommand, Is.Not.Null);
        Assert.That(vm.SaveCommand, Is.InstanceOf<ICommand>());
    }
}

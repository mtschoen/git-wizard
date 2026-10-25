using System.ComponentModel;
using System.Reflection;
using GitWizardTests.TestSupport;
using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests.UI;

// Covers MainViewModel.cs and MainWindow.axaml: composition and compiled-binding contracts.
public class MainViewModelCompositionTests
{
    static readonly Type[] AllowedShellFieldTypes =
    [
        typeof(StatusHeaderViewModel), typeof(PreparedDiscoveryIndexStore),
        typeof(RepositoryListViewModel), typeof(RefreshCoordinator),
        typeof(LiveWatchViewModel), typeof(IndexCacheViewModel),
        typeof(RepositoryCommandsViewModel), typeof(ErrorLogViewModel),
        typeof(IUiDispatcher), typeof(IUserDialogs), typeof(IClipboardService),
        typeof(TimeProvider), typeof(PropertyChangedEventHandler)
    ];

    [Test]
    public void Shell_DeclaresOnlyComponentAndServiceFields()
    {
        var unexpected = typeof(MainViewModel)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(field => !AllowedShellFieldTypes.Contains(field.FieldType))
            .Select(field => field.Name).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(unexpected, Is.Empty);
            Assert.That(typeof(MainViewModel).IsSealed, Is.True);
        }
    }

    [Test]
    public void ViewModelsDirectory_HoldsNoMainViewModelPartials()
    {
        var directory = Path.Combine(SourceTree.RepositoryRoot, "GitWizardUI", "ViewModels");
        Assert.That(Directory.GetFiles(directory, "MainViewModel.*.cs"), Is.Empty);
    }

    [Test]
    public void ShellFile_StaysWithinLineBudget()
    {
        var path = Path.Combine(SourceTree.RepositoryRoot, "GitWizardUI", "ViewModels", "MainViewModel.cs");
        Assert.That(File.ReadAllLines(path), Has.Length.LessThanOrEqualTo(450));
    }

    [Test]
    public void MainWindowXaml_UsesOnlyCompiledBindings()
    {
        var path = Path.Combine(SourceTree.RepositoryRoot, "GitWizardUI", "Views", "MainWindow.axaml");
        var text = File.ReadAllText(path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(text, Does.Not.Contain("ReflectionBinding"));
            Assert.That(text, Does.Not.Contain("x:CompileBindings=\"False\""));
        }
    }
}

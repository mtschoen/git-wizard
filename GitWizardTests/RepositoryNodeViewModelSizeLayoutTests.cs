using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitWizard;
using GitWizardUI.ViewModels;
using GitWizardUI.Views;

namespace GitWizardTests;

public class RepositoryNodeViewModelSizeLayoutTests
{
    [AvaloniaTest]
    [SetCulture("en-US")]
    public async Task RepositoryRow_SizeArrives_UpdatesBindingWithoutMovingColumns()
    {
        await using var mainWindow = new MainWindow();
        var repositoryList = mainWindow.FindControl<ListBox>("RepositoryList")!;
        var repository = new GitWizardRepository("repository");
        var node = new RepositoryNodeViewModel(repository);
        var row = repositoryList.ItemTemplate!.Build(node)!;
        row.DataContext = node;
        var host = new Window { Width = 900, Height = 60, Content = row };
        try
        {
            host.Show();
            Dispatcher.UIThread.RunJobs();
            var sizeLabel = row.GetVisualDescendants().OfType<TextBlock>()
                .Single(label => ToolTip.GetTip(label) as string == "Size on disk");
            var previousBounds = sizeLabel.Bounds;
            var labelGrid = (Grid)sizeLabel.Parent!;
            var previousGridBounds = labelGrid.Bounds;
            Assert.That(sizeLabel.Text, Is.Empty);
            Assert.That(previousBounds.Width, Is.GreaterThan(0));

            typeof(GitWizardRepository).GetProperty(nameof(GitWizardRepository.SizeOnDisk))!
                .SetValue(repository, 1572864L);
            node.Update();
            Dispatcher.UIThread.RunJobs();
            host.UpdateLayout();

            Assert.Multiple(() =>
            {
                Assert.That(sizeLabel.Text, Is.EqualTo("1.5 MB"));
                Assert.That(sizeLabel.TextAlignment, Is.EqualTo(TextAlignment.Right));
                Assert.That(sizeLabel.Bounds, Is.EqualTo(previousBounds));
                Assert.That(labelGrid.Bounds, Is.EqualTo(previousGridBounds));
            });
        }
        finally
        {
            host.Close();
        }
    }
}

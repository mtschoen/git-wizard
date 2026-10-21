using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using GitWizard;
using GitWizardUI.ViewModels;
using GitWizardUI.Views;

namespace GitWizardTests;

public class MainWindowInteractionTests
{
    [AvaloniaTest]
    public void RepositoryList_SingleTapOnGroupHeader_ExpandsGroup()
    {
        var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        viewModel.ToggleGroupMode(GroupMode.Drive);
        viewModel.AddRepository(new GitWizardRepository("C:/repos/alpha"));
        var header = viewModel.Repositories[0];
        var repositoryList = window.FindControl<ListBox>("RepositoryList")!;
        repositoryList.DataContext = header;
        RaiseTaps(repositoryList, Gestures.TappedEvent);

        Assert.That(header.IsExpanded, Is.True,
            "A single primary tap on a group row must expand the collapsed group.");
    }

    [AvaloniaTest]
    public void RepositoryList_DoubleTapOnGroupHeader_DoesNotUndoSingleTap()
    {
        var window = new MainWindow();
        var viewModel = (MainViewModel)window.DataContext!;
        viewModel.ToggleGroupMode(GroupMode.Drive);
        viewModel.AddRepository(new GitWizardRepository("C:/repos/alpha"));
        var header = viewModel.Repositories[0];
        var repositoryList = window.FindControl<ListBox>("RepositoryList")!;
        repositoryList.DataContext = header;

        RaiseTaps(repositoryList,
            Gestures.TappedEvent,
            Gestures.TappedEvent,
            Gestures.DoubleTappedEvent);

        Assert.That(header.IsExpanded, Is.True,
            "The second tap in a double-tap sequence must not undo the first group toggle.");
    }

    static void RaiseTaps(ListBox repositoryList, params RoutedEvent[] routedEvents)
    {
        var pointerReleasedEvents = new List<PointerReleasedEventArgs>();
        var pointerTarget = new Border { Background = Brushes.Transparent };
        pointerTarget.PointerReleased += (_, e) => pointerReleasedEvents.Add(e);
        var pointerHost = new Window
        {
            Width = 100,
            Height = 100,
            Content = pointerTarget,
        };
        pointerHost.Show();
        foreach (var _ in routedEvents)
        {
            pointerHost.MouseDown(new Point(50, 50), MouseButton.Left);
            pointerHost.MouseUp(new Point(50, 50), MouseButton.Left);
        }
        pointerHost.Close();
        Assert.That(pointerReleasedEvents, Has.Count.EqualTo(routedEvents.Length),
            "Every headless mouse tap must raise PointerReleased.");

        for (var i = 0; i < routedEvents.Length; i++)
        {
            var tappedEvent = new TappedEventArgs(routedEvents[i], pointerReleasedEvents[i])
            {
                Source = repositoryList,
            };

            repositoryList.RaiseEvent(tappedEvent);
        }
    }
}

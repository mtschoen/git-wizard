using GitWizardUI.ViewModels;
using GitWizardUI.ViewModels.Services;

namespace GitWizardTests.TestSupport;

internal static class TestViewModels
{
    public static MainViewModel Create(IUiDispatcher? dispatcher = null,
        IUserDialogs? dialogs = null, IClipboardService? clipboard = null,
        TimeProvider? timeProvider = null) =>
        new(dispatcher ?? new StubUiDispatcher(), dialogs ?? new StubUserDialogs(),
            clipboard ?? new StubClipboardService(), timeProvider);

    public static SettingsViewModel CreateSettings(IFolderPicker? folderPicker = null,
        MainViewModel? mainViewModel = null, IUiDispatcher? dispatcher = null) =>
        new(folderPicker ?? new StubFolderPicker(), mainViewModel,
            dispatcher ?? new StubUiDispatcher());
}

using GitWizardUI.ViewModels.Services;
#if WINDOWS
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;
#endif

namespace GitWizardUI.Services;

public sealed class MauiFolderPicker : IFolderPicker
{
    public async Task<string?> PickFolderAsync()
    {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = ((MauiWinUIWindow)Application.Current!.Windows[0].Handler!.PlatformView!).WindowHandle;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
#else
        await Task.Yield();
        return null;
#endif
    }
}

using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using GitWizardUI.ViewModels.Services;

namespace GitWizardAvalonia.Services;

public sealed class AvaloniaFolderPicker : IFolderPicker
{
    public async Task<string?> PickFolderAsync()
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner?.StorageProvider is null) return null;

        var result = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select repository folder",
            AllowMultiple = false,
        });
        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }
}

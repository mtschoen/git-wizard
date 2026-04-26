namespace GitWizardUI.ViewModels.Services;

/// <summary>Native folder-picker dialog. Returns the absolute path the user chose, or null if cancelled.</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync();
}

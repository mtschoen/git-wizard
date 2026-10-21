using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

public sealed class StubFolderPicker : IFolderPicker
{
    public string? NextResult { get; set; }
    public int PickCount { get; private set; }

    public Task<string?> PickFolderAsync()
    {
        PickCount++;
        return Task.FromResult(NextResult);
    }
}

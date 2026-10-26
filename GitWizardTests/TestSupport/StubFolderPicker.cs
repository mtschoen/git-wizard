using GitWizardUI.ViewModels.Services;

namespace GitWizardTests;

public sealed class StubFolderPicker : IFolderPicker
{
    public string? NextResult { get; init; }
    int PickCount { get; set; }

    public Task<string?> PickFolderAsync()
    {
        PickCount++;
        return Task.FromResult(NextResult);
    }
}

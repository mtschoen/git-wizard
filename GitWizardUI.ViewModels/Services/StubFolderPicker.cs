namespace GitWizardUI.ViewModels.Services;

/// <summary>Test stub: returns a scripted result and tracks invocation count.</summary>
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

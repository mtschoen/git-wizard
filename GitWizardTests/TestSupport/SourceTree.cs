namespace GitWizardTests.TestSupport;

internal static class SourceTree
{
    internal static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "git-wizard.slnx")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Cannot locate git-wizard.slnx.");
        }
    }
}

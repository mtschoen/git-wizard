namespace GitWizardTests.TestSupport;

static class PackIndexFixture
{
    public static void Create(string repositoryPath, long length) =>
        CreateAtGitDirectory(Path.Combine(repositoryPath, ".git"), length);

    public static void CreateBare(string repositoryPath, long length) =>
        CreateAtGitDirectory(repositoryPath, length);

    static void CreateAtGitDirectory(string gitDirectory, long length)
    {
        var packDirectory = Path.Combine(gitDirectory, "objects", "pack");
        Directory.CreateDirectory(packDirectory);
        using var stream = new FileStream(Path.Combine(packDirectory, "synthetic.idx"),
            FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.SetLength(length);
    }
}

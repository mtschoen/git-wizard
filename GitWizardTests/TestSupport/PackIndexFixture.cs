namespace GitWizardTests.TestSupport;

static class PackIndexFixture
{
    public static void Create(string repositoryPath, long length)
    {
        var packDirectory = Path.Combine(repositoryPath, ".git", "objects", "pack");
        Directory.CreateDirectory(packDirectory);
        using var stream = new FileStream(Path.Combine(packDirectory, "synthetic.idx"),
            FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.SetLength(length);
    }
}

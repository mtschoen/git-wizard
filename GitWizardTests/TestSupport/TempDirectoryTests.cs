namespace GitWizardTests.TestSupport;

[TestFixture]
sealed class TempDirectoryTests
{
    [Test]
    public void DisposeRemovesOwnedTreeContainingReadOnlyFiles()
    {
        var owned = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(owned.Path, "child"));
        var file = Path.Combine(owned.Path, "child", "read-only.txt");
        File.WriteAllText(file, "owned");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        owned.Dispose();

        // Dispose swallows IOException and UnauthorizedAccessException, so a cleanup that
        // leaves a read-only tree behind is silent unless the absence is asserted here.
        Assert.That(Directory.Exists(owned.Path), Is.False);
    }
}

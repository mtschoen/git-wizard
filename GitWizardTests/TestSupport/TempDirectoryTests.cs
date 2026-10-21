namespace GitWizardTests.TestSupport;

[TestFixture]
internal sealed class TempDirectoryTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void OwnedPathIsUniqueAndCleanupRemovesReadOnlyFiles(bool create)
    {
        using var second = new TempDirectory();
        var first = new TempDirectory(create: create);
        try
        {
            Assert.That(first.Path, Is.Not.EqualTo(second.Path));
            Assert.That(Directory.Exists(first.Path), Is.EqualTo(create));
            Directory.CreateDirectory(Path.Combine(first.Path, "child"));
            var file = Path.Combine(first.Path, "child", "read-only.txt");
            File.WriteAllText(file, "owned");
            File.SetAttributes(file, FileAttributes.ReadOnly);
            first.Dispose();
            Assert.That(Directory.Exists(first.Path), Is.False);
            first.Dispose();
            Assert.That(Directory.Exists(second.Path), Is.True);
        }
        finally
        {
            first.Dispose();
        }
    }
}

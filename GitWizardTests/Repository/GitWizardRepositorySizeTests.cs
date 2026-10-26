using GitWizard;
using GitWizardTests.TestSupport;

namespace GitWizardTests;

public class GitWizardRepositorySizeTests
{
    [Test]
    public void ComputeDirectorySize_EmptyDirectory_ReturnsZero()
    {
        using var directory = new TempDirectory("gw-empty-");
        var size = GitWizardRepository.ComputeDirectorySize(directory.Path);
        Assert.That(size, Is.Zero);
    }

    [Test]
    public void ComputeDirectorySize_DirectoryWithFiles_ReturnsSumOfFileSizes()
    {
        using var directory = new TempDirectory("gw-size-");
        var dir = directory.Path;
        File.WriteAllBytes(Path.Combine(dir, "a.txt"), "hello"u8);
        File.WriteAllBytes(Path.Combine(dir, "b.txt"), "world!"u8);

        var size = GitWizardRepository.ComputeDirectorySize(dir);
        Assert.That(size, Is.EqualTo(11));
    }

    [Test]
    public void ComputeDirectorySize_NestedDirectories_IncludesAllFiles()
    {
        using var directory = new TempDirectory("gw-nested-");
        var dir = directory.Path;
        var subDir = Path.Combine(dir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllBytes(Path.Combine(dir, "root.txt"), "root"u8);
        File.WriteAllBytes(Path.Combine(subDir, "nested.txt"), "nested"u8);

        var size = GitWizardRepository.ComputeDirectorySize(dir);
        Assert.That(size, Is.EqualTo(10));
    }
}

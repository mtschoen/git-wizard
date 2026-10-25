using System.Text;
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
        File.WriteAllBytes(Path.Combine(dir, "a.txt"), Encoding.UTF8.GetBytes("hello"));
        File.WriteAllBytes(Path.Combine(dir, "b.txt"), Encoding.UTF8.GetBytes("world!"));

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
        File.WriteAllBytes(Path.Combine(dir, "root.txt"), Encoding.UTF8.GetBytes("root"));
        File.WriteAllBytes(Path.Combine(subDir, "nested.txt"), Encoding.UTF8.GetBytes("nested"));

        var size = GitWizardRepository.ComputeDirectorySize(dir);
        Assert.That(size, Is.EqualTo(10));
    }
}

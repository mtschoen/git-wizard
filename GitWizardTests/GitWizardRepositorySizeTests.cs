using System.Text;
using GitWizard;

namespace GitWizardTests;

public class GitWizardRepositorySizeTests
{
    [Test]
    public void ComputeDirectorySize_EmptyDirectory_ReturnsZero()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "gw-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var size = GitWizardRepository.ComputeDirectorySize(emptyDir);
            Assert.That(size, Is.EqualTo(0));
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    [Test]
    public void ComputeDirectorySize_DirectoryWithFiles_ReturnsSumOfFileSizes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.txt"), Encoding.UTF8.GetBytes("hello"));
            File.WriteAllBytes(Path.Combine(dir, "b.txt"), Encoding.UTF8.GetBytes("world!"));

            var size = GitWizardRepository.ComputeDirectorySize(dir);
            Assert.That(size, Is.EqualTo(11));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void ComputeDirectorySize_NestedDirectories_IncludesAllFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var subDir = Path.Combine(dir, "sub");
        Directory.CreateDirectory(subDir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "root.txt"), Encoding.UTF8.GetBytes("root"));
            File.WriteAllBytes(Path.Combine(subDir, "nested.txt"), Encoding.UTF8.GetBytes("nested"));

            var size = GitWizardRepository.ComputeDirectorySize(dir);
            Assert.That(size, Is.EqualTo(10));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

using GitWizard;
using GitWizard.Watch;

namespace GitWizardTests.Watch;

// Covers the lock-only noise rule of RepositoryChangeFilter: a transient `*.lock` file inside a
// tracked repository's .git directory is not a change, while everything else still is.
[TestFixture]
public class RepositoryChangeFilterGitLockTests
{
    const string RepoRoot = @"C:\repos\repo-a";
    const string OtherRoot = @"C:\repos\repo-b";

    static RepositoryChangeFilter CreateFilter() => new([RepoRoot, OtherRoot], [@"C:\repos"]);

    static VolumeChangeEntry Entry(string path, VolumeEntryKind kind = VolumeEntryKind.Modified) => new(path, kind);

    [TestCase(@"C:\repos\repo-a\.git\index.lock", TestName = "GitDirectoryLock_IndexLock")]
    [TestCase(@"C:\repos\repo-a\.git\HEAD.lock", TestName = "GitDirectoryLock_HeadLock")]
    [TestCase(@"C:\repos\repo-a\.git\packed-refs.lock", TestName = "GitDirectoryLock_PackedRefsLock")]
    [TestCase(@"C:\repos\repo-a\.git\refs\heads\x.lock", TestName = "GitDirectoryLock_BranchRefLock")]
    [TestCase(@"C:\repos\repo-a\.git\worktrees\wt\index.lock", TestName = "GitDirectoryLock_LinkedWorktreeIndexLock")]
    [TestCase(@"C:\repos\repo-a\.git\modules\sub\index.lock", TestName = "GitDirectoryLock_SubmoduleIndexLock")]
    [TestCase(@"C:\repos\repo-a\.GIT\INDEX.LOCK", TestName = "GitDirectoryLock_WindowsCaseIsIgnored")]
    [TestCase("C:/repos/repo-a/.git/index.lock", TestName = "GitDirectoryLock_ForwardSlashes")]
    public void Classify_GitDirectoryLockAlone_IsNotAChange(string lockPath)
    {
        foreach (var kind in new[] { VolumeEntryKind.Created, VolumeEntryKind.Modified, VolumeEntryKind.Deleted })
        {
            var result = CreateFilter().Classify([Entry(lockPath, kind)]);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Changed, Is.Empty, kind.ToString());
                Assert.That(result.Created, Is.Empty, kind.ToString());
                Assert.That(result.Deleted, Is.Empty, kind.ToString());
            }
        }
    }

    [Test]
    public void Classify_WholeLockLifecycleBatch_IsNotAChange()
    {
        var result = CreateFilter().Classify([
            Entry(@"C:\repos\repo-a\.git\index.lock", VolumeEntryKind.Created),
            Entry(@"C:\repos\repo-a\.git\index.lock"),
            Entry(@"C:\repos\repo-a\.git\index.lock", VolumeEntryKind.Deleted)
        ]);

        Assert.That(result.Changed, Is.Empty);
    }

    [Test]
    public void Classify_IndexRewriteRename_IsAChange()
    {
        // The journal maps `index.lock` renamed to `index` as Deleted(previous) plus Created(new):
        // the Created entry names `.git\index`, which is not a lock file.
        var result = CreateFilter().Classify([
            Entry(@"C:\repos\repo-a\.git\index.lock", VolumeEntryKind.Deleted),
            Entry(@"C:\repos\repo-a\.git\index", VolumeEntryKind.Created)
        ]);

        Assert.That(result.Changed, Is.EquivalentTo([RepoRoot]));
    }

    [Test]
    public void Classify_LockWithGitIndexModified_IsAChange()
    {
        var result = CreateFilter().Classify([
            Entry(@"C:\repos\repo-a\.git\index.lock"),
            Entry(@"C:\repos\repo-a\.git\index")
        ]);

        Assert.That(result.Changed, Is.EquivalentTo([RepoRoot]));
    }

    [Test]
    public void Classify_LockWithWorkingTreeFile_IsAChange()
    {
        var result = CreateFilter().Classify([
            Entry(@"C:\repos\repo-a\.git\index.lock"),
            Entry(@"C:\repos\repo-a\src\Program.cs")
        ]);

        Assert.That(result.Changed, Is.EquivalentTo([RepoRoot]));
    }

    [TestCase(@"C:\repos\repo-a\.git\refs\heads\main", TestName = "LockWithOtherGitEntry_Ref")]
    [TestCase(@"C:\repos\repo-a\.git\HEAD", TestName = "LockWithOtherGitEntry_Head")]
    [TestCase(@"C:\repos\repo-a\.git\objects\ab\cdef", TestName = "LockWithOtherGitEntry_Object")]
    public void Classify_LockWithOtherGitEntry_IsAChange(string otherPath)
    {
        var result = CreateFilter().Classify([Entry(@"C:\repos\repo-a\.git\index.lock"), Entry(otherPath)]);

        Assert.That(result.Changed, Is.EquivalentTo([RepoRoot]));
    }

    [TestCase(@"C:\repos\repo-a\foo.lock", TestName = "LockOutsideGitDirectory_WorkingTreeRoot")]
    [TestCase(@"C:\repos\repo-a\sub\Cargo.lock", TestName = "LockOutsideGitDirectory_Nested")]
    [TestCase(@"C:\repos\repo-a\.git.lock", TestName = "LockOutsideGitDirectory_GitNamePrefix")]
    [TestCase(@"C:\repos\repo-a\.gitx\index.lock", TestName = "LockOutsideGitDirectory_GitNameLookalike")]
    [TestCase(@"C:\repos\repo-a\sub\.git\index.lock", TestName = "LockOutsideGitDirectory_UntrackedNestedRepository")]
    public void Classify_LockOutsideGitDirectory_IsAChange(string lockPath)
    {
        var result = CreateFilter().Classify([Entry(lockPath)]);

        Assert.That(result.Changed, Is.EquivalentTo([RepoRoot]));
    }

    [TestCase(@"C:\repos\repo-a\.git\index.locked", TestName = "NotLockSuffix_Locked")]
    [TestCase(@"C:\repos\repo-a\.git\lock\index", TestName = "NotLockSuffix_LockDirectoryAncestor")]
    [TestCase(@"C:\repos\repo-a\.git\refs\x.lock\HEAD", TestName = "NotLockSuffix_LockNamedAncestorDirectory")]
    public void Classify_GitDirectoryPathWithoutLockSuffix_IsAChange(string path)
    {
        var result = CreateFilter().Classify([Entry(path)]);

        Assert.That(result.Changed, Is.EquivalentTo([RepoRoot]));
    }

    [Test]
    public void Classify_SecondRepositoryWithRealChange_IsStillReported()
    {
        var result = CreateFilter().Classify([
            Entry(@"C:\repos\repo-a\.git\index.lock", VolumeEntryKind.Created),
            Entry(@"C:\repos\repo-b\notes.txt"),
            Entry(@"C:\repos\repo-a\.git\index.lock", VolumeEntryKind.Deleted)
        ]);

        Assert.That(result.Changed, Is.EquivalentTo([OtherRoot]));
    }

    [Test]
    public void Classify_PosixPaths_FollowCaseSensitivity()
    {
        var filter = new RepositoryChangeFilter(["/repos/a"], ["/repos"]);

        var lockOnly = filter.Classify([Entry("/repos/a/.git/index.lock")]);
        var differentCase = filter.Classify([Entry("/repos/a/.git/index.LOCK")]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lockOnly.Changed, Is.Empty);
            Assert.That(differentCase.Changed, Is.EquivalentTo(["/repos/a"]));
        }
    }

    [Test]
    public void Classify_GitDirectoryDeletion_IsStillReportedAsDeleted()
    {
        var result = CreateFilter().Classify([Entry(@"C:\repos\repo-a\.git", VolumeEntryKind.Deleted)]);

        Assert.That(result.Deleted, Is.EquivalentTo([RepoRoot]));
    }
}

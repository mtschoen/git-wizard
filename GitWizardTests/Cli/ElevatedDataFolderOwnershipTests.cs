using System.Runtime.Versioning;
using GitWizard.CLI;
using GitWizardTests.TestSupport;

namespace GitWizardTests.Cli;

[TestFixture]
public partial class ElevatedDataFolderOwnershipTests
{
    [Test]
    public void Begin_WithInvokingUser_ReassignsOnEntryAndOnDisposal()
    {
        var calls = new List<(string Folder, uint UserId)>();

        using (ElevatedDataFolderOwnership.Begin(
                   "/data/GitWizard",
                   "1000",
                   (folder, userId) =>
                   {
                       calls.Add((folder, userId));
                       return 0;
                   },
                   _ => { },
                   _ => new NoRegistration()))
        {
            Assert.That(calls, Has.Count.EqualTo(1));
        }

        Assert.That(calls, Is.EqualTo([
            ("/data/GitWizard", 1000u),
            ("/data/GitWizard", 1000u)
        ]));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-number")]
    [TestCase("-5")]
    [TestCase("0")]
    public void Begin_WithoutUsableInvokingUser_DoesNothing(string? invokingUserText)
    {
        using var ownership = ElevatedDataFolderOwnership.Begin(
            "/data/GitWizard",
            invokingUserText,
            (_, _) => throw new AssertionException("No invoking user means nothing to reassign."),
            _ => { },
            _ => throw new AssertionException("No invoking user means no termination handler."));

        Assert.That(ownership, Is.Null);
    }

    [Test]
    public void Dispose_WhenEntriesCouldNotBeReassigned_LogsTheCount()
    {
        var logged = new List<string>();
        using (ElevatedDataFolderOwnership.Begin("/data", "1000", (_, _) => 2, logged.Add, _ => new NoRegistration()))
        {
        }

        Assert.That(logged, Has.Count.EqualTo(2));
        Assert.That(logged[1], Does.Contain("2 file(s)").And.Contain("/data").And.Contain("1000"));
    }

    [Test]
    public void Termination_ReassignsAgainAndDisposalReleasesTheRegistration()
    {
        var reassignments = 0;
        Action? onTermination = null;
        var registration = new NoRegistration();

        using (ElevatedDataFolderOwnership.Begin(
                   "/data/GitWizard",
                   "1000",
                   (_, _) =>
                   {
                       reassignments++;
                       return 0;
                   },
                   _ => { },
                   action =>
                   {
                       onTermination = action;
                       return registration;
                   }))
        {
            onTermination!();

            Assert.That(reassignments, Is.EqualTo(2));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(registration.Disposed, Is.True);
            Assert.That(reassignments, Is.EqualTo(3));
        }
    }

    [Test]
    public void TerminationSignals_RegisterBothSignalsAndDisposeTheirRegistrations()
    {
        var signals = new List<System.Runtime.InteropServices.PosixSignal>();
        var registrations = new List<NoRegistration>();
        Action<System.Runtime.InteropServices.PosixSignalContext>? onSignal = null;
        var repairs = 0;

        var composite = ElevatedDataFolderOwnership.RegisterTerminationSignals(
            () => repairs++,
            (signal, handler) =>
            {
                signals.Add(signal);
                onSignal = handler;
                var registration = new NoRegistration();
                registrations.Add(registration);
                return registration;
            });

        onSignal!(null!);
        composite.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(signals, Is.EqualTo([
                System.Runtime.InteropServices.PosixSignal.SIGTERM,
                System.Runtime.InteropServices.PosixSignal.SIGHUP
            ]));
            Assert.That(repairs, Is.EqualTo(1));
            Assert.That(registrations.Select(registration => registration.Disposed), Is.All.True);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public void Walk_VisitsNestedEntriesAndNeverFollowsSymlinks(bool relativeRoot)
    {
        using var sandbox = new TempDirectory("OwnershipWalk-");
        var outside = Path.Combine(sandbox.Path, "outside");
        var data = Path.Combine(sandbox.Path, "data");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
        Directory.CreateDirectory(Path.Combine(data, "Logs"));
        File.WriteAllText(Path.Combine(data, "Logs", "today.log"), "x");
        File.WriteAllText(Path.Combine(data, "report.json"), "x");
        File.CreateSymbolicLink(Path.Combine(data, "link"), outside);
        var visited = new List<string>();
        var rootReassigned = false;

        var failures = LinuxOwnershipRepair.Walk(
            relativeRoot ? Path.GetRelativePath(Environment.CurrentDirectory, data) : data,
            (_, _, relativePath) =>
            {
                visited.Add(relativePath);
                return true;
            },
            _ =>
            {
                rootReassigned = true;
                return true;
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failures, Is.Zero);
            Assert.That(rootReassigned, Is.True);
            Assert.That(visited, Is.EquivalentTo([
                "Logs",
                Path.Combine("Logs", "today.log"),
                "report.json",
                "link"
            ]));
        }
    }

    [Test]
    public void Walk_WithScriptedNativeBoundaries_CountsFailuresAndClosesDescriptors()
    {
        var visited = new List<string>();
        var closed = new List<int>();

        var failures = LinuxOwnershipRepair.Walk(
            "/data",
            (_, _, relativePath) =>
            {
                visited.Add(relativePath);
                return relativePath != "link";
            },
            _ => true,
            ((path, _) => path == "/data" ? 10 : -1,
                (descriptor, name, _) => descriptor == 10 && name == "Logs" ? 11 : -1,
                closed.Add,
                path => path == "/proc/self/fd/10"
                    ? ["Logs", "report.json", "link"]
                    : ["today.log"]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failures, Is.EqualTo(1));
            Assert.That(visited, Is.EqualTo([
                "Logs",
                Path.Combine("Logs", "today.log"),
                "report.json",
                "link"
            ]));
            Assert.That(closed, Is.EquivalentTo([10, 11]));
        }
    }

    [TestCase("/data", "/")]
    [TestCase("data", ".")]
    public void Reassignment_WithScriptedNativeBoundaries_ChecksThePinnedInode(string path, string startingDirectory)
    {
        var closed = new List<int>();
        var changed = new List<int>();
        var status = new LinuxOwnershipRepair.FileStatus
        {
            Mode = LinuxOwnershipRepair.RegularFile,
            LinkCount = 2,
            UserId = 1,
            GroupId = 2
        };
        int GetStatus(int _, out LinuxOwnershipRepair.FileStatus value)
        {
            value = status;
            return 0;
        }

        var hardlinkResult = LinuxOwnershipRepair.ReassignOpened(10, 1, 2,
            (GetStatus, (_, _, _) => throw new AssertionException("A hardlink must not be changed.")));
        status.LinkCount = 1;
        var correctOwnerResult = LinuxOwnershipRepair.ReassignOpened(10, 1, 2,
            (GetStatus, (_, _, _) => throw new AssertionException("An owned file must not be changed.")));
        status.UserId = 0;
        var changedResult = LinuxOwnershipRepair.ReassignOpened(10, 1, 2,
            (GetStatus, (descriptor, _, _) =>
            {
                changed.Add(descriptor);
                return 0;
            }
        ));
        var rootDescriptor = LinuxOwnershipRepair.OpenRoot(path, 0,
            ((descriptor, name, _) => (descriptor, name) switch
            {
                (-100, var directory) when directory == startingDirectory => 10,
                (10, "data") => 11,
                _ => throw new AssertionException("Each component must open relative to its owned parent.")
            }, closed.Add));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hardlinkResult, Is.True);
            Assert.That(correctOwnerResult, Is.True);
            Assert.That(changedResult, Is.True);
            Assert.That(changed, Is.EqualTo([10]));
            Assert.That(rootDescriptor, Is.EqualTo(11));
            Assert.That(closed, Is.EqualTo([10]));
        }
    }

    [TestCase("")]
    [TestCase("/")]
    [TestCase("/.")]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public void Walk_RootThatIsASymlink_IsLeftAlone(string suffix)
    {
        using var sandbox = new TempDirectory("OwnershipWalk-");
        var target = Path.Combine(sandbox.Path, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "file.txt"), "x");
        var link = Path.Combine(sandbox.Path, "link");
        File.CreateSymbolicLink(link, target);

        var failures = LinuxOwnershipRepair.Walk(
            link + suffix,
            (_, _, _) => throw new AssertionException("A symlinked root must not be walked."),
            _ => throw new AssertionException("A symlinked root must not be reassigned."));

        Assert.That(failures, Is.Zero);
    }

    [Test]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public void Reassign_MultiplyLinkedFile_LeavesTheSharedInodeAlone()
    {
        using var sandbox = new TempDirectory("OwnershipWalk-");
        var data = Path.Combine(sandbox.Path, "data");
        Directory.CreateDirectory(data);
        var outside = Path.Combine(sandbox.Path, "outside.txt");
        File.WriteAllText(outside, "x");
        Assert.That(CreateHardLink(outside, Path.Combine(data, "linked.txt")), Is.Zero);
        File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.SetUser);

        var failures = LinuxOwnershipRepair.Reassign(data, GetUserId());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failures, Is.Zero);
            Assert.That(File.GetUnixFileMode(outside), Is.EqualTo(UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.SetUser));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    [Platform("Linux")]
    [SupportedOSPlatform("linux")]
    public void Reassign_RepairsContainedFilesWithoutFollowingSymlinks(bool relativeRoot)
    {
        using var sandbox = new TempDirectory("OwnershipRepair-");
        var data = Path.Combine(sandbox.Path, "data");
        Directory.CreateDirectory(Path.Combine(data, "Logs"));
        var inside = Path.Combine(data, "Logs", "today.log");
        var outside = Path.Combine(sandbox.Path, "outside.txt");
        File.WriteAllText(inside, "inside");
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(data, "link"), outside);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(inside, mode | UnixFileMode.SetUser);
        File.SetUnixFileMode(outside, mode | UnixFileMode.SetUser);

        // The unchanged-owner sentinel still makes fchown clear setuid, without needing root.
        var failures = LinuxOwnershipRepair.Reassign(
            relativeRoot ? Path.GetRelativePath(Environment.CurrentDirectory, data) : data,
            uint.MaxValue);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failures, Is.Zero);
            Assert.That(File.GetUnixFileMode(inside), Is.EqualTo(mode), "The contained file must be repaired.");
            Assert.That(File.GetUnixFileMode(outside), Is.EqualTo(mode | UnixFileMode.SetUser),
                "The symlink target must not be changed.");
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    static extern uint GetUserId();

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "link", SetLastError = true,
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial int CreateHardLink(string existingPath, string newPath);

    sealed class NoRegistration : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}

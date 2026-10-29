using System.Runtime.InteropServices;
using GitWizard;

namespace GitWizardTests;

public class LibGit2MemorySettingsTests
{
    [Test]
    public void CoreStartup_CapsPackMappingsAndObjectCache_Issue450()
    {
        LibGit2MemorySettings.Configure();

        var mappedResult = GetMappedWindowLimit(2, out var mappedLimit);
        var cacheResult = GetCachedMemory(9, out _, out var cacheLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mappedResult, Is.Zero, "Reading the libgit2 mapped-window limit failed.");
            Assert.That(cacheResult, Is.Zero, "Reading the libgit2 object-cache limit failed.");
            Assert.That(mappedLimit, Is.EqualTo((nuint)(512 * 1024 * 1024)));
            Assert.That(cacheLimit, Is.EqualTo((nint)(128 * 1024 * 1024)));
        }
    }

    [DllImport("git2-3f4182d", EntryPoint = "git_libgit2_opts", CallingConvention = CallingConvention.Cdecl)]
    static extern int GetMappedWindowLimit(int option, out nuint limit);

    [DllImport("git2-3f4182d", EntryPoint = "git_libgit2_opts", CallingConvention = CallingConvention.Cdecl)]
    static extern int GetCachedMemory(int option, out nint current, out nint allowed);
}

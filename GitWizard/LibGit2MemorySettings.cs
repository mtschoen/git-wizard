using System.Runtime.InteropServices;

namespace GitWizard;

public static class LibGit2MemorySettings
{
    const int SetMappedWindowLimitOption = 3;
    const int SetObjectCacheLimitOption = 7;

    // Fits the observed 238 MiB and 162 MiB pack indexes together and avoids the 256 MiB refresh regression.
    const int MappedWindowLimitBytes = 512 * 1024 * 1024;

    // Half of libgit2's 256 MiB default balances bounded native cache growth with refresh throughput.
    const int ObjectCacheLimitBytes = 128 * 1024 * 1024;

    public static void Configure()
    {
        EnsureSucceeded(SetMappedWindowLimit(SetMappedWindowLimitOption, MappedWindowLimitBytes),
            SetMappedWindowLimitOption);
        EnsureSucceeded(SetObjectCacheLimit(SetObjectCacheLimitOption, ObjectCacheLimitBytes),
            SetObjectCacheLimitOption);
    }

    static void EnsureSucceeded(int result, int option)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"git_libgit2_opts option {option} failed with error {result}.");
        }
    }

    [DllImport("git2-3f4182d", EntryPoint = "git_libgit2_opts", CallingConvention = CallingConvention.Cdecl)]
    static extern int SetMappedWindowLimit(int option, nuint limit);

    [DllImport("git2-3f4182d", EntryPoint = "git_libgit2_opts", CallingConvention = CallingConvention.Cdecl)]
    static extern int SetObjectCacheLimit(int option, nint limit);
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitWizardTests.TestSupport;

/// <summary>
/// Sets one privilege on the process token until disposed, then restores its previous state. A privilege the
/// token does not hold is left alone, so the scope is a no-op for it.
/// </summary>
[SupportedOSPlatform("windows")]
sealed partial class ProcessPrivilege : IDisposable
{
    const uint TokenAdjustPrivileges = 0x0020;
    const uint TokenQuery = 0x0008;
    const uint PrivilegeEnabled = 0x0002;
    const int TokenPrivilegesClass = 3;
    const int ErrorNotAllAssigned = 1300;
    const int LuidAndAttributesSize = 12;

    readonly Luid _privilege;
    readonly bool _previouslyEnabled;
    bool _restored;

    ProcessPrivilege(Luid privilege, bool previouslyEnabled)
    {
        _privilege = privilege;
        _previouslyEnabled = previouslyEnabled;
    }

    /// <summary>Enables or disables <paramref name="name" /> (for example SeBackupPrivilege) until disposed.</summary>
    /// <returns>The restoring scope, or null when the token does not hold the privilege.</returns>
    public static ProcessPrivilege? Set(string name, bool enabled)
    {
        var privilege = Lookup(name);
        var previous = Read(privilege);
        if (previous is null)
        {
            return null;
        }

        Adjust(privilege, enabled);
        return new ProcessPrivilege(privilege, previous.Value);
    }

    /// <summary>The privilege's enabled state on the process token, or null when the token does not hold it.</summary>
    public static bool? IsEnabled(string name) => Read(Lookup(name));

    public void Dispose()
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        Adjust(_privilege, _previouslyEnabled);
    }

    static Luid Lookup(string name)
    {
        if (!LookupPrivilegeValue(null, name, out var luid))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Unknown privilege {name}.");
        }

        return luid;
    }

    static bool? Read(Luid privilege)
    {
        using var token = OpenToken(TokenQuery);
        GetTokenInformation(token, TokenPrivilegesClass, IntPtr.Zero, 0, out var length);
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, TokenPrivilegesClass, buffer, length, out length))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var count = Marshal.ReadInt32(buffer);
            for (var index = 0; index < count; index++)
            {
                var offset = sizeof(uint) + index * LuidAndAttributesSize;
                var luid = new Luid((uint)Marshal.ReadInt32(buffer, offset), Marshal.ReadInt32(buffer, offset + 4));
                if (luid == privilege)
                {
                    return ((uint)Marshal.ReadInt32(buffer, offset + 8) & PrivilegeEnabled) != 0;
                }
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    static void Adjust(Luid privilege, bool enabled)
    {
        using var token = OpenToken(TokenAdjustPrivileges | TokenQuery);
        var state = new TokenPrivileges(1, privilege, enabled ? PrivilegeEnabled : 0);
        if (!AdjustTokenPrivileges(token, false, in state, 0, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNotAllAssigned)
        {
            throw new Win32Exception(error, "The process token does not hold the privilege.");
        }
    }

    static SafeAccessTokenHandle OpenToken(uint access)
    {
        if (!OpenProcessToken(GetCurrentProcess(), access, out var token))
        {
            token.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return token;
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Luid(uint LowPart, int HighPart);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    readonly struct TokenPrivileges(uint count, Luid luid, uint attributes)
    {
        readonly uint _count = count;
        readonly Luid _luid = luid;
        readonly uint _attributes = attributes;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returnedLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, in TokenPrivileges newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);
}

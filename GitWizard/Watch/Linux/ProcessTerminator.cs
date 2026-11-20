using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GitWizard.Watch.Linux;

/// <summary>
/// The platform seam behind <see cref="ProcessTerminator"/>: acquires a stable identity for a child
/// process and signals through that identity. Production uses pidfds; tests inject a fake.
/// </summary>
internal interface IProcessSignaller
{
    /// <summary>Opens an identity for <paramref name="processId"/>; false (with an errno) when none can be had.</summary>
    bool TryOpen(int processId, out SafeHandle? identity, out int errorNumber);

    /// <summary>Sends SIGTERM through <paramref name="identity"/>; returns 0 or an errno.</summary>
    int Terminate(SafeHandle identity);
}

/// <summary>
/// Ends a launched child by SIGTERM without ever signalling a bare process id, which could name an
/// unrelated process once the child is reaped and the id is reused. The identity is a pidfd opened
/// right after start and kept for the child's lifetime; a pidfd opened while the child was
/// unreaped refers to that child only, and signalling it after the reap returns ESRCH. When no
/// identity can be had, nothing is signalled and the caller's pipe close and wait path ends the
/// child. Live already requires kernel 5.9 for fanotify, above pidfd_open (5.3) and
/// pidfd_send_signal (5.1).
/// </summary>
internal sealed class ProcessTerminator : IDisposable
{
    const int NoSuchProcess = 3;

    readonly Lock _gate = new();
    readonly IProcessSignaller _signaller;
    readonly Action<string> _log;
    SafeHandle? _identity;

    ProcessTerminator(IProcessSignaller signaller, SafeHandle? identity, Action<string> log)
    {
        _signaller = signaller;
        _identity = identity;
        _log = log;
    }

    /// <summary>
    /// Acquires the identity for a just-started child. <paramref name="hasExited"/> must read the
    /// runtime's reaped state; it is checked after the open, so an identity is kept only when it
    /// was opened while the child was still unreaped.
    /// </summary>
    public static ProcessTerminator Create(int processId, Func<bool> hasExited, IProcessSignaller signaller, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(signaller);
        ArgumentNullException.ThrowIfNull(log);
        SafeHandle? identity = null;
        try
        {
            if (!signaller.TryOpen(processId, out identity, out var errorNumber) || identity is null)
            {
                log($"No process identity for the pkexec process ({new Win32Exception(errorNumber).Message}); it will not be signalled.");
                return new ProcessTerminator(signaller, null, log);
            }

            if (hasExited())
            {
                log("The pkexec process had already exited when its identity was acquired; nothing to signal.");
                return new ProcessTerminator(signaller, null, log);
            }

            var owner = new ProcessTerminator(signaller, identity, log);
            identity = null;
            return owner;
        }
        finally
        {
            identity?.Dispose();
        }
    }

    /// <summary>True while a signalling identity is held.</summary>
    public bool HasIdentity
    {
        get
        {
            lock (_gate)
            {
                return _identity is not null;
            }
        }
    }

    /// <summary>
    /// Sends SIGTERM through the identity. Already-exited (ESRCH) and refused (EPERM, once the
    /// process is root) outcomes are not errors.
    /// </summary>
    public void Terminate()
    {
        lock (_gate)
        {
            if (_identity is null)
            {
                return;
            }

            var errorNumber = _signaller.Terminate(_identity);
            if (errorNumber != 0 && errorNumber != NoSuchProcess)
            {
                _log($"Ending the pkexec process was refused: {new Win32Exception(errorNumber).Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _identity?.Dispose();
            _identity = null;
        }
    }
}

/// <summary>Production signaller: pidfd_open and pidfd_send_signal, on Linux x86_64 and arm64 only.</summary>
internal sealed partial class PidfdProcessSignaller : IProcessSignaller
{
    // Identical on x86_64 (asm/unistd_64.h) and arm64 (asm-generic/unistd.h).
    const long PidfdOpenNumber = 434;
    const long PidfdSendSignalNumber = 424;
    const int TerminateSignal = 15;
    const int NotSupported = 38;

    public static PidfdProcessSignaller Instance { get; } = new();

    static bool IsSupported =>
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    public bool TryOpen(int processId, out SafeHandle? identity, out int errorNumber)
    {
        identity = null;
        errorNumber = 0;
        if (!IsSupported)
        {
            errorNumber = NotSupported;
            return false;
        }

        var descriptor = PidfdOpen(PidfdOpenNumber, processId, 0);
        if (descriptor < 0)
        {
            errorNumber = Marshal.GetLastPInvokeError();
            return false;
        }

        identity = new PidDescriptor((int)descriptor);
        return true;
    }

    public int Terminate(SafeHandle identity)
    {
        var added = false;
        try
        {
            identity.DangerousAddRef(ref added);
            var result = PidfdSendSignal(PidfdSendSignalNumber, identity.DangerousGetHandle().ToInt32(), TerminateSignal, IntPtr.Zero, 0);
            return result == 0 ? 0 : Marshal.GetLastPInvokeError();
        }
        finally
        {
            if (added)
            {
                identity.DangerousRelease();
            }
        }
    }

    sealed partial class PidDescriptor : SafeHandleMinusOneIsInvalid
    {
        public PidDescriptor(int descriptor)
            : base(true) => SetHandle(descriptor);

        protected override bool ReleaseHandle() => CloseDescriptor(handle.ToInt32()) == 0;

        [LibraryImport("libc", EntryPoint = "close")]
        private static partial int CloseDescriptor(int descriptor);
    }

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long PidfdOpen(long number, long processId, long flags);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long PidfdSendSignal(long number, long descriptor, long signal, IntPtr information, long flags);
}

using System.Runtime.InteropServices;

namespace GitWizard.CLI;

/// <summary>
/// The sudo-elevated watch child runs as root against the invoking user's data folder
/// (GITWIZARD_HOME), so every file it creates there (log, report, configuration, caches) would be
/// root-owned and lock the user's later runs out. Holding one of these for the life of the child
/// hands everything under that folder back to the invoking user (sudo supplies SUDO_UID):
/// on entry (the log exists by then, and a killed child may have left residue), on disposal, and
/// when SIGTERM or SIGHUP end the child without unwinding the watch. SIGKILL cannot be caught; the
/// next elevated start repairs what it leaves. One seam covers every write site, so no
/// file-creating code needs to know about elevation.
/// </summary>
internal sealed class ElevatedDataFolderOwnership : IDisposable
{
    internal const string InvokingUserVariable = "SUDO_UID";

    readonly string _dataFolder;
    readonly uint _invokingUserId;
    readonly Func<string, uint, int> _reassign;
    readonly Action<string> _logError;
    IDisposable? _terminationRegistration;

    ElevatedDataFolderOwnership(string dataFolder, uint invokingUserId, Func<string, uint, int> reassign, Action<string> logError)
    {
        _dataFolder = dataFolder;
        _invokingUserId = invokingUserId;
        _reassign = reassign;
        _logError = logError;
    }

    /// <summary>
    /// Begins ownership repair for a sudo child. Returns null when there is no invoking user to
    /// give files back to: the variable is absent or not a number, or names root itself.
    /// <paramref name="reassign"/> hands the folder's contents to the user id and returns the number
    /// of entries it could not reassign. <paramref name="registerTermination"/> runs the given
    /// action when the process is told to terminate and returns the registration to release.
    /// </summary>
    internal static ElevatedDataFolderOwnership? Begin(
        string dataFolder,
        string? invokingUserText,
        Func<string, uint, int> reassign,
        Action<string> logError,
        Func<Action, IDisposable> registerTermination)
    {
        if (!uint.TryParse(invokingUserText, out var invokingUserId) || invokingUserId == 0)
        {
            return null;
        }

        var ownership = new ElevatedDataFolderOwnership(dataFolder, invokingUserId, reassign, logError);
        ownership.Reassign();
        ownership._terminationRegistration = registerTermination(ownership.Reassign);
        return ownership;
    }

    /// <summary>Runs <paramref name="repair"/> on SIGTERM or SIGHUP, then lets the default termination proceed.</summary>
    internal static IDisposable RegisterTerminationSignals(Action repair) =>
        RegisterTerminationSignals(repair, PosixSignalRegistration.Create);

    internal static IDisposable RegisterTerminationSignals(
        Action repair,
        Func<PosixSignal, Action<PosixSignalContext>, IDisposable> createRegistration)
    {
        void OnSignal(PosixSignalContext context) => repair();

        return new CompositeRegistration(
            createRegistration(PosixSignal.SIGTERM, OnSignal),
            createRegistration(PosixSignal.SIGHUP, OnSignal));
    }

    public void Dispose()
    {
        _terminationRegistration?.Dispose();
        Reassign();
    }

    void Reassign()
    {
        var failures = _reassign(_dataFolder, _invokingUserId);
        if (failures > 0)
        {
            _logError($"Could not return {failures} file(s) in {_dataFolder} to user {_invokingUserId}.");
        }
    }

    sealed class CompositeRegistration(params IDisposable[] registrations) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        }
    }
}

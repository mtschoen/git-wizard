using MFTLib.Index;

namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    // The library keeps a live-watch or catch-up report after the drive recovers, so a later fault
    // of another kind sees the same report. A report explains one episode: it is raised once.
    readonly System.Collections.Concurrent.ConcurrentDictionary<char, JournalCheckpointLoss> _raisedFaultLosses = new();

    internal void ProcessArmedDrive(DriveStatus drive, GitWizardConfiguration configuration,
        Dictionary<string, string> errors, List<JournalWarning> warnings, CancellationToken cancellationToken)
    {
        var owner = _owner ?? throw new InvalidOperationException("No index session available.");
        if (drive.State is DriveState.Failed or DriveState.Offline)
        {
            errors[drive.DriveLetter.ToString()] = drive.FailureMessage ?? $"Drive is {drive.State}.";
            // A cache-declined drive keeps its CheckpointLoss so the hint can explain the
            // discarded checkpoint even though the drive is not being watched.
            AddJournalWarning(drive, owner, configuration, warnings);
            return;
        }

        if (!drive.Watch.Supported)
            return;

        var letter = char.ToUpperInvariant(drive.DriveLetter);
        AddUsableDrive(letter);
        // Cache the drive's real indexed root directory so OnChanged can rewrite a live
        // change's path back onto this drive's own "<letter>:\" form, regardless of
        // whether IndexedDrive.RootDirectory is the drive root (MFTLib #143).
        _driveRealRoots[letter] = owner.Index.Root(drive.DriveLetter, cancellationToken).Path;

        AddJournalWarning(drive, owner, configuration, warnings);
    }

    // A watch fault on a drive asks whether the journal outran the watch: MFTLib records a
    // live-watch or scan catch-up loss before raising WatchFaulted, so the
    // drive's hint can be refreshed in the same moment the failure is reported.
    void RaiseFaultJournalWarning(char driveLetter)
    {
        var owner = _owner;
        var drive = owner?.Index.Drives.FirstOrDefault(candidate =>
            char.ToUpperInvariant(candidate.DriveLetter) == driveLetter);
        if (owner is null || drive is null)
            return;
        ProcessFaultedDrive(drive, owner, GitWizardConfiguration.GetGlobalConfiguration());
    }

    // Internal for tests: a consumer test index never reports a CheckpointLoss (MFTLib's
    // journal isolation answers "cannot say"), so the decision is fed a drive status directly,
    // the same way ProcessArmedDrive is tested. A live-watch or scan catch-up loss belongs to
    // the fault being handled; a drive-opening report is the open's standing explanation and an
    // unrelated fault leaves it, and this event, untouched. A retained report instance already
    // raised for the drive is not raised again; distinct report instances (such as successive
    // journal recreations with identical fields) represent new loss episodes and are raised.
    internal void ProcessFaultedDrive(DriveStatus drive, MftIndexSession owner, GitWizardConfiguration configuration)
    {
        if (drive.Watch.CheckpointLoss is not { DetectedDuring: JournalCheckpointLossDetection.LiveWatch or JournalCheckpointLossDetection.ScanCatchUp })
            return;
        var letter = char.ToUpperInvariant(drive.DriveLetter);
        if (_raisedFaultLosses.TryGetValue(letter, out var raised) && ReferenceEquals(raised, drive.Watch.CheckpointLoss))
            return;
        if (BuildJournalWarning(drive, owner, configuration) is not { } warning)
            return;
        _raisedFaultLosses[letter] = warning.Loss;
        JournalWarningDetected?.Invoke(warning);
    }

    static void AddJournalWarning(DriveStatus drive, MftIndexSession owner,
        GitWizardConfiguration configuration, List<JournalWarning> warnings)
    {
        if (BuildJournalWarning(drive, owner, configuration) is { } warning)
            warnings.Add(warning);
    }

    static JournalWarning? BuildJournalWarning(DriveStatus drive, MftIndexSession owner,
        GitWizardConfiguration configuration)
    {
        var indexedDrive = owner.IndexedDrives.FirstOrDefault(candidate =>
            char.ToUpperInvariant(candidate.DriveLetter) == char.ToUpperInvariant(drive.DriveLetter));
        if (indexedDrive is null || configuration.IgnoredJournalWarnings.Contains(indexedDrive.VolumeSerial))
            return null;
        if (drive.Watch.CheckpointLoss is not { } loss)
            return null;

        return new JournalWarning
        {
            DriveLetter = char.ToUpperInvariant(drive.DriveLetter).ToString(),
            VolumeSerial = indexedDrive.VolumeSerial,
            Loss = loss
        };
    }
}

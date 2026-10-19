using MFTLib.Index;

namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    // The library keeps a live-watch or catch-up report after the drive recovers, so a later fault
    // of another kind sees the same report. A report explains one episode: it is raised once.
    readonly System.Collections.Concurrent.ConcurrentDictionary<char, JournalCheckpointLoss> _raisedFaultLosses = new();

    // The warning each drive's consumers currently hold, with the watch state version it was built
    // from. A watch-state change refreshes it in place: same loss, same episode, current wording.
    readonly Dictionary<char, PublishedWarning> _publishedWarnings = [];
    readonly Lock _publishedWarningsLock = new();

    sealed record PublishedWarning(JournalWarning Warning, long StateVersion);

    public IReadOnlyList<JournalWarning> CurrentJournalWarnings
    {
        get
        {
            lock (_publishedWarningsLock)
                return _publishedWarnings.Values.Select(published => published.Warning).ToArray();
        }
    }

    internal void ProcessArmedDrive(DriveStatus drive, GitWizardConfiguration configuration,
        Dictionary<string, string> errors, List<JournalWarning> warnings, CancellationToken cancellationToken)
    {
        var owner = _owner ?? throw new InvalidOperationException("No index session available.");
        if (drive.State is DriveState.Failed or DriveState.Offline)
        {
            errors[drive.Drive.ToString()] = drive.Failure?.Message ?? $"Drive is {drive.State}.";
            // A cache-declined drive keeps its CheckpointLoss so the hint can explain the
            // discarded checkpoint even though the drive is not being watched.
            AddJournalWarning(drive, owner, configuration, warnings);
            return;
        }

        if (!drive.Watch.IsSupported)
            return;

        var letter = drive.Drive.Letter;
        AddUsableDrive(letter);
        // Cache the drive's real indexed root directory so OnChanged can rewrite a live
        // change's path back onto this drive's own "<letter>:\" form, regardless of
        // whether IndexedDrive.RootDirectory is the drive root (MFTLib #143).
        _driveRealRoots[letter] = owner.Index.Root(drive.Drive, cancellationToken).Path;

        AddJournalWarning(drive, owner, configuration, warnings);
    }

    // A watch fault on a drive asks whether the journal outran the watch: MFTLib records a
    // live-watch or scan catch-up loss before raising WatchFaulted, so the
    // drive's hint can be refreshed in the same moment the failure is reported.
    void RaiseFaultJournalWarning(char driveLetter)
    {
        var owner = _owner;
        var drive = owner?.Index.Drives.FirstOrDefault(candidate =>
            candidate.Drive.Letter == driveLetter);
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
        var letter = drive.Drive.Letter;
        if (_raisedFaultLosses.TryGetValue(letter, out var raised) && ReferenceEquals(raised, drive.Watch.CheckpointLoss))
        {
            // The same episode: only the drive's recovery state can have moved on.
            RefreshJournalWarning(letter, drive.Watch, configuration);
            return;
        }
        if (BuildJournalWarning(drive, owner, configuration) is not { } warning)
            return;
        _raisedFaultLosses[letter] = warning.Loss;
        PublishJournalWarning(letter, warning, drive.Watch.StateVersion);
    }

    void PublishJournalWarning(char letter, JournalWarning warning, long stateVersion)
    {
        lock (_publishedWarningsLock)
        {
            _publishedWarnings[letter] = new PublishedWarning(warning, stateVersion);
            JournalWarningDetected?.Invoke(warning);
        }
    }

    // Replaces the warning consumers hold for a drive when its watch state moved on, so the hint
    // never keeps describing a recovery that has since finished or stopped. The loss instance is
    // the episode: a state change carrying another loss is a new episode for the fault path, an
    // episode the user dismissed stays dismissed, and a stale version never overwrites a newer one.
    internal void RefreshJournalWarning(char letter, DriveWatchStatus watch, GitWizardConfiguration configuration)
    {
        lock (_publishedWarningsLock)
        {
            if (!_publishedWarnings.TryGetValue(letter, out var published) ||
                !ReferenceEquals(published.Warning.Loss, watch.CheckpointLoss) ||
                watch.StateVersion < published.StateVersion ||
                configuration.IgnoredJournalWarnings.Contains(published.Warning.VolumeSerial))
                return;
            if (published.Warning.WatchState == watch.State &&
                published.Warning.IsRecoveryStopped == watch.IsRecoveryStopped)
                return;
            var refreshed = published.Warning with
            {
                WatchState = watch.State,
                IsRecoveryStopped = watch.IsRecoveryStopped
            };
            _publishedWarnings[letter] = new PublishedWarning(refreshed, watch.StateVersion);
            JournalWarningDetected?.Invoke(refreshed);
        }
    }

    // Warnings built at arm time describe the drives before watching started. After catch-up the
    // returned list is rebuilt from the drives' current states, and what is returned is what the
    // drives' consumers hold, so later state changes refresh it.
    List<JournalWarning> CollectArmedJournalWarnings(MftIndexSession owner, GitWizardConfiguration configuration)
    {
        var warnings = new List<JournalWarning>();
        lock (_publishedWarningsLock)
        {
            _publishedWarnings.Clear();
            foreach (var drive in owner.Index.Drives)
            {
                var isFailed = drive.State is DriveState.Failed or DriveState.Offline;
                if (!isFailed && !drive.Watch.IsSupported)
                    continue;
                if (BuildJournalWarning(drive, owner, configuration) is not { } warning)
                    continue;
                warnings.Add(warning);
                _publishedWarnings[drive.Drive.Letter] = new PublishedWarning(warning, drive.Watch.StateVersion);
            }
        }
        return warnings;
    }

    // Called with a state change the source accepted; a drive without a published warning is untouched.
    void RefreshJournalWarningForState(char driveLetter, DriveWatchStatus watch)
    {
        var letter = char.ToUpperInvariant(driveLetter);
        lock (_publishedWarningsLock)
        {
            if (!_publishedWarnings.ContainsKey(letter))
                return;
        }
        RefreshJournalWarning(letter, watch, GitWizardConfiguration.GetGlobalConfiguration());
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
            candidate.Drive == drive.Drive);
        if (indexedDrive is null || configuration.IgnoredJournalWarnings.Contains(indexedDrive.VolumeSerial))
            return null;
        if (drive.Watch.CheckpointLoss is not { } loss)
            return null;

        return new JournalWarning
        {
            DriveLetter = drive.Drive.ToString(),
            VolumeSerial = indexedDrive.VolumeSerial,
            Loss = loss,
            WatchState = drive.Watch.State,
            IsRecoveryStopped = drive.Watch.IsRecoveryStopped
        };
    }
}

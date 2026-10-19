using MFTLib.Index;

namespace GitWizard.Watch;

internal static class JournalHintBuilder
{
    // The retained loss says what happened and when it was found; the watch state and the
    // recovery-stopped flag say where recovery stands now. A null state means the caller does not
    // know it, and the hint then makes no claim about recovery.
    public static string BuildHint(JournalCheckpointLoss loss, WatchState? watchState, bool recoveryStopped)
    {
        return loss.Cause switch
        {
            JournalCheckpointLossCause.JournalRecreated => BuildRecreatedHint(loss, watchState, recoveryStopped),

            JournalCheckpointLossCause.CheckpointTrimmed => BuildTrimmedHint(loss, watchState, recoveryStopped),

            JournalCheckpointLossCause.JournalAdvanced => BuildAdvancedHint(loss, watchState, recoveryStopped),

            _ => $"Hint on {loss.Drive}: Checkpoint loss detected."
        };
    }

    static string BuildRecreatedHint(JournalCheckpointLoss loss, WatchState? watchState, bool recoveryStopped)
    {
        if (loss.DetectedDuring == JournalCheckpointLossDetection.LiveWatch)
        {
            return $"Hint on {loss.Drive}: The change journal was recreated, so the position " +
                   "the watch had reached no longer refers to anything and live updates for this " +
                   $"drive stopped.{BuildRecoveryClause(watchState, recoveryStopped)} No journal size change is needed.";
        }

        if (loss.DetectedDuring == JournalCheckpointLossDetection.ScanCatchUp)
        {
            return $"Hint on {loss.Drive}: The change journal was recreated while the drive " +
                   $"was being scanned, so the scan could not catch up.{BuildRecoveryClause(watchState, recoveryStopped)} " +
                   "No journal size change is needed.";
        }

        return $"Hint on {loss.Drive}: The change journal was recreated, so the saved " +
               "position no longer refers to anything and a full rescan was needed. " +
               "Nothing needs changing.";
    }

    static string BuildAdvancedHint(JournalCheckpointLoss loss, WatchState? watchState, bool recoveryStopped)
    {
        if (loss.DetectedDuring == JournalCheckpointLossDetection.LiveWatch)
        {
            return $"Hint on {loss.Drive}: The change journal moved while the watch was running, " +
                   $"so live updates for this drive stopped.{BuildRecoveryClause(watchState, recoveryStopped)} " +
                   "No journal size change is needed.";
        }

        if (loss.DetectedDuring == JournalCheckpointLossDetection.ScanCatchUp)
        {
            return $"Hint on {loss.Drive}: The change journal moved while the drive was being " +
                   $"scanned, so the scan could not catch up.{BuildRecoveryClause(watchState, recoveryStopped)} " +
                   "No journal size change is needed.";
        }

        return $"Hint on {loss.Drive}: The change journal moved while the index was closed, " +
               "so the saved journal position could not be resumed. A full open rescans the drive; " +
               "a snapshot open keeps the cached index until the next full open. Nothing needs changing.";
    }

    static string BuildTrimmedHint(JournalCheckpointLoss loss, WatchState? watchState, bool recoveryStopped)
    {
        var rescan = BuildRescanClause(loss, watchState, recoveryStopped);
        if (loss.SizeThatWouldHaveRetained is not { } suggestedSize)
        {
            return $"Hint on {loss.Drive}: {rescan}The size that would have avoided this " +
                   "rescan is too large to represent, so no new size is suggested.";
        }

        return $"Hint on {loss.Drive}: {rescan}To prevent this, increase the journal size: " +
               $"a journal of at least {ByteSizeFormatter.Format(suggestedSize)} would have avoided " +
               $"this rescan (it was {ByteSizeFormatter.Format(loss.JournalSettings.MaximumSize)} when this was detected).";
    }

    // What the loss means for the drive depends on when it was found: at open the cold scan has
    // already happened, while a live-watch or scan catch-up loss leaves recovery to MFTLib and the
    // claim about it follows the current state.
    static string BuildRescanClause(JournalCheckpointLoss loss, WatchState? watchState, bool recoveryStopped)
    {
        if (loss.DetectedDuring == JournalCheckpointLossDetection.LiveWatch)
        {
            var recovery = BuildRecoveryClause(watchState, recoveryStopped);
            return loss.BytesBehind is { } liveBytesBehind
                ? "Live updates for this drive stopped because the change journal moved " +
                  $"{ByteSizeFormatter.Format(liveBytesBehind)} past the position the watch had " +
                  $"reached.{recovery} "
                : "Live updates for this drive stopped because the change journal moved past " +
                  $"the position the watch had reached.{recovery} ";
        }

        if (loss.DetectedDuring == JournalCheckpointLossDetection.ScanCatchUp)
        {
            return "The change journal moved past the position recorded before the drive's scan " +
                   $"while the scan ran, so the scan could not catch up.{BuildRecoveryClause(watchState, recoveryStopped)} ";
        }

        return loss.BytesBehind is { } bytesBehind
            ? $"The last saved position was {ByteSizeFormatter.Format(bytesBehind)} older than " +
              "the change journal still holds, so a full rescan was needed. "
            : "The last saved position fell out of the change journal, so a full rescan was needed. ";
    }

    // Starts with a space so it follows the sentence it qualifies. Only a stopped recovery, a
    // recovering state and the two running states support a claim; any other state, or none, does not.
    static string BuildRecoveryClause(WatchState? watchState, bool recoveryStopped)
    {
        if (recoveryStopped)
            return " Automatic recovery stopped, so this drive is not being kept current. Rescan this drive.";

        return watchState switch
        {
            WatchState.Recovering => " The drive is being rescanned to catch up.",
            WatchState.CatchingUp => " The watch is catching up with the change journal.",
            WatchState.CaughtUp => " The drive was rescanned and the watch has caught up.",
            _ => " The current watch state is unknown, so check the drive's watch status before deciding whether to rescan."
        };
    }
}

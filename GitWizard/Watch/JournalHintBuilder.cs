using MFTLib.Index;

namespace GitWizard.Watch;

internal static class JournalHintBuilder
{
    public static string BuildHint(JournalCheckpointLoss loss)
    {
        return loss.Cause switch
        {
            JournalCheckpointLossCause.JournalRecreated => BuildRecreatedHint(loss),

            JournalCheckpointLossCause.CheckpointTrimmed => BuildTrimmedHint(loss),

            _ => $"Hint on {loss.DriveLetter}: Checkpoint loss detected."
        };
    }

    static string BuildRecreatedHint(JournalCheckpointLoss loss)
    {
        if (loss.DetectedDuring == JournalCheckpointLossDetection.LiveWatch)
        {
            return $"Hint on {loss.DriveLetter}: The change journal was recreated, so the position " +
                   "the watch had reached no longer refers to anything and live updates for this " +
                   "drive stopped. A rescan of the drive is needed to catch up. Nothing needs changing.";
        }

        return $"Hint on {loss.DriveLetter}: The change journal was recreated, so the saved " +
               "position no longer refers to anything and a full rescan was needed. " +
               "Nothing needs changing.";
    }

    static string BuildTrimmedHint(JournalCheckpointLoss loss)
    {
        var rescan = BuildRescanClause(loss);
        if (loss.SizeThatWouldHaveRetained is not { } suggestedSize)
        {
            return $"Hint on {loss.DriveLetter}: {rescan}The size that would have avoided this " +
                   "rescan is too large to represent, so no new size is suggested.";
        }

        return $"Hint on {loss.DriveLetter}: {rescan}To prevent this, increase the journal size: " +
               $"a journal of at least {ByteSizeFormatter.Format(suggestedSize)} would have avoided " +
               $"this rescan (it is {ByteSizeFormatter.Format(loss.MaximumSize)} now).";
    }

    // What the loss means for the drive depends on when it was found: at open the cold scan has
    // already happened, while a live-watch loss leaves the drive behind until someone rescans it.
    static string BuildRescanClause(JournalCheckpointLoss loss)
    {
        if (loss.DetectedDuring == JournalCheckpointLossDetection.LiveWatch)
        {
            return loss.BytesBehind is { } liveBytesBehind
                ? "Live updates for this drive stopped because the change journal moved " +
                  $"{ByteSizeFormatter.Format(liveBytesBehind)} past the position the watch had " +
                  "reached, so a rescan of the drive is needed to catch up. "
                : "Live updates for this drive stopped because the change journal moved past " +
                  "the position the watch had reached, so a rescan of the drive is needed to catch up. ";
        }

        return loss.BytesBehind is { } bytesBehind
            ? $"The last saved position was {ByteSizeFormatter.Format(bytesBehind)} older than " +
              "the change journal still holds, so a full rescan was needed. "
            : "The last saved position fell out of the change journal, so a full rescan was needed. ";
    }
}

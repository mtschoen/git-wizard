using MFTLib.Index;

namespace GitWizard.Watch;

public sealed partial class IndexVolumeChangeSource
{
    void ReportScanProgress(VolumeScanProgress sample)
    {
        try
        {
            ScanProgress?.Invoke(sample);
        }
        catch (Exception exception)
        {
            // Progress is best-effort; a subscriber fault must never fail a scan.
            GitWizardLog.LogException(exception, "A Live scan-progress subscriber failed.");
        }
    }

    // Reports synchronously on the broker client's receive thread, so samples reach subscribers
    // in frame order; marshalling to a UI thread belongs to the subscriber, per MFTLib's
    // IProgress<T> convention. The broker throttles ScanProgress frames to one per 250 ms, so
    // no further coalescing happens here.
    sealed class ArmScanProgress(IReadOnlyList<string> requestedDrives, Action<VolumeScanProgress> sink)
        : IProgress<IndexScanProgress>
    {
        public void Report(IndexScanProgress value)
        {
            var letter = value.Drive.ToString();
            var ordinal = 0;
            for (var i = 0; i < requestedDrives.Count; i++)
            {
                if (string.Equals(requestedDrives[i], letter, StringComparison.OrdinalIgnoreCase))
                {
                    ordinal = i + 1;
                    break;
                }
            }

            // A sample naming a drive outside the requested set should not exist; clamp rather
            // than report a nonsensical "0 of N".
            sink(new VolumeScanProgress(letter, Math.Max(ordinal, 1), requestedDrives.Count,
                value.WrittenRowCount, value.ExpectedRowCount));
        }
    }
}

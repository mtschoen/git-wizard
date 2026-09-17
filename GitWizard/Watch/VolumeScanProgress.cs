namespace GitWizard.Watch;

/// <summary>
/// One arm-time scan sample from a source that cold-scans while arming. <see cref="Drive" /> is
/// the normalized uppercase drive letter (for example "D"), <see cref="DriveOrdinal" /> its
/// 1-based position in the arm's requested drive set, and <see cref="DriveTotal" /> that set's
/// size. <see cref="TotalRows" /> is null while the producer does not yet know the drive's total.
/// </summary>
public sealed record VolumeScanProgress(string Drive, int DriveOrdinal, int DriveTotal,
    uint RowsWritten, uint? TotalRows);

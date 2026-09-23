using MFTLib.Index;

namespace GitWizard;

/// <summary>
/// One requested drive's outcome: the library's immutable status when the drive reached the index,
/// or the reason its volume identity could not be read, in which case no producer ran.
/// </summary>
public sealed record MftDriveStatus(string Drive, DriveStatus? IndexStatus, string? IdentityFailure);

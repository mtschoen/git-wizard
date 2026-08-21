using JetBrains.Annotations;

namespace GitWizard.Watch;

/// <summary>
/// Result of <see cref="IVolumeChangeSource.ArmAndCatchUpAsync"/>: any per-drive scan failures.
/// A drive present in <see cref="Errors"/> was not successfully armed; watching continues
/// on the remaining, successfully armed drives.
/// </summary>
public sealed record VolumeArmResult(
    [property: UsedImplicitly] IReadOnlyDictionary<string, string> Errors);

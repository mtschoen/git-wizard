using MFTLib.Index;

namespace GitWizard;

public sealed record MftIndexCacheSnapshot
{
    public bool IsApplicable { get; init; }
    public string CacheDirectory { get; init; } = string.Empty;
    public string Profile { get; init; } = string.Empty;
    public IReadOnlyList<string> KeepFileNames { get; init; } = [];

    /// <summary>A cached block whose stored tag differs from this one is discarded on open, so the inventory shows both.</summary>
    public CacheTag RequestedCacheTag { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public IReadOnlyList<CachedBlockStatus> Inventory { get; init; } = [];
    public IReadOnlyList<MftActiveCacheSession> Sessions { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool Succeeded => Errors.Count == 0;
}

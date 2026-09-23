namespace GitWizard;

/// <summary>How an index session treats a drive without a usable cached block.</summary>
public enum MftIndexOpenIntent
{
    /// <summary>Adopt a cached block when possible; otherwise scan through the broker and persist the result.</summary>
    Persistent = 0,

    /// <summary>Adopt a cached block only; never launch the broker, scan, or start watching while opening.</summary>
    Snapshot = 1
}

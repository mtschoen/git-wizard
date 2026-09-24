namespace GitWizard.Watch;

/// <summary>
/// Optional confirmation of when a source's background read pump actually began running,
/// distinct from <see cref="IVolumeChangeSource.WatchAsync"/> merely being called. A source that
/// starts its pump on a separate thread (<c>FanotifyVolumeChangeSource</c>'s <c>Task.Run</c> pump
/// on Linux) only queues that work when <c>WatchAsync</c>'s enumerator is first advanced; the
/// pump may not have begun executing by the time that call returns. A source whose pump is
/// started and awaited earlier, before <c>WatchAsync</c> can even be called (the index-backed
/// source starts and awaits its watch during arming), reports <see cref="PumpStarted"/> already
/// complete. A source without this interface offers no better signal than its own first read
/// having begun.
/// </summary>
public interface IPumpLifecycle
{
    /// <summary>Completes once the pump is confirmed active. Never faults or cancels on its own.</summary>
    Task PumpStarted { get; }
}

namespace NLightning.Domain.Onchain.Interfaces;

using Bitcoin.Events;
using Events;
using Models;

/// <summary>
/// Watches outpoints for spends in processed blocks and reports disconnected blocks (BOLT 5 plan O0-T2, O0-T3).
/// </summary>
/// <remarks>
/// A watch is persisted (<c>WatchedOutpoints</c>) and reloaded by the chain monitor at startup, so a caller registers
/// it once. The event is raised for every processed block that spends the outpoint, also for a replayed block and for
/// a block of a new branch after a reorg, so every listener must be idempotent. Handlers run on the monitor's
/// processing path after the block was saved: they must only enqueue work (take a channel lock and save on their own
/// task), never block.
/// </remarks>
public interface IOutpointWatcher
{
    /// <summary>A watched outpoint was spent in a processed (and saved) block.</summary>
    event EventHandler<OutpointSpentEventArgs>? OnWatchedOutpointSpent;

    /// <summary>A processed block left the active chain; raised after the rollback was saved.</summary>
    event EventHandler<BlockDisconnectedEventArgs>? OnBlockDisconnected;

    /// <summary>Saves the watch in its own unit of work (skipped when it exists) and starts watching it.</summary>
    Task WatchOutpointAsync(WatchedOutpointModel watchedOutpoint);

    /// <summary>
    /// Starts watching an outpoint whose row the caller saved (<c>IUnitOfWork.WatchedOutpointDbRepository.Add</c> in
    /// the same save as the state that needs it). Nothing is written.
    /// </summary>
    void TrackWatchedOutpoint(WatchedOutpointModel watchedOutpoint);
}
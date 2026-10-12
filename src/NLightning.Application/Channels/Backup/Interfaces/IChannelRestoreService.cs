namespace NLightning.Application.Channels.Backup.Interfaces;

using Domain.Bitcoin.Events;
using Models;

/// <summary>
/// <c>restorechanbackup</c>, like LND's: turns a static channel backup of this node into recovery-only channels
/// (<see cref="RecoveryChannels"/>) on a node that lost its channel database, then connects to their peers so they
/// force close (BOLT 2 <c>option_data_loss_protect</c>). Our commitment is never broadcast; our output of the peer's
/// commitment is swept by the on-chain resolution once that commitment confirms.
/// </summary>
public interface IChannelRestoreService
{
    /// <summary>
    /// Restores every channel of <paramref name="backup"/> that is not in the database yet.
    /// </summary>
    /// <exception cref="ChannelBackupException">The backup does not decrypt with the node key, is tampered with or
    /// malformed, or is for another node or chain.</exception>
    Task<ChannelRestoreResult> RestoreAsync(ReadOnlyMemory<byte> backup, CancellationToken cancellationToken);

    /// <summary>
    /// Called once at start (NL-430): in the background, every recovery channel still waiting for its peer's close
    /// has its funding spend looked up again once it is loaded, and the search of a spend older than the recent
    /// window starts again (the search lives in memory only). Returns at once; the default does nothing.
    /// </summary>
    void ResumeSpendSearches()
    {
    }

    /// <summary>
    /// A funding spend the chain monitor reported, before the on-chain watcher sees it (NL-478 review): when the channel
    /// is a recovery channel and the spend is no commitment (a splice that confirmed after the restore), the channel is
    /// moved to the splice's funding output (or waits at this one while that output can't be recognized yet) instead of
    /// being closed, and true is returned; false leaves the spend to the on-chain watcher. The default returns false.
    /// </summary>
    Task<bool> TryHandleRecoveryFundingSpendAsync(OutpointSpentEventArgs spend, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
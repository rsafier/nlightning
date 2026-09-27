namespace NLightning.Application.Channels.Backup.Interfaces;

using Models;

/// <summary>
/// Finds out, at restore time, whether a backed-up channel's funding output is already spent on chain and by which
/// transaction. The chain monitor only sees spends from its current height on, so a peer that force closed before
/// the restore (on the <c>error</c> our wiped node answered its <c>channel_reestablish</c> with) would otherwise never
/// be seen, and our <c>to_remote</c> output, paid to a channel basepoint rather than a wallet address, never swept.
/// </summary>
public interface IFundingSpendLocator
{
    /// <summary>
    /// Checks the funding output of <paramref name="entry"/> and, when it is spent, searches the recent blocks
    /// (<see cref="ChannelBackupOptions.RestoreSpendSearchDepth"/>) for the spend; never throws for a chain failure.
    /// </summary>
    Task<FundingSpendLocation> LocateAsync(ChannelBackupEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Searches the blocks below <paramref name="belowHeight"/> (exclusive) down to the funding block for the spend of
    /// <paramref name="entry"/>'s funding output (NL-430: a spend older than <see cref="LocateAsync"/>'s window);
    /// never throws for a chain failure. <see cref="FundingSpendStatus.SpentNotFound"/> then means the whole range
    /// was searched.
    /// </summary>
    Task<FundingSpendLocation> RescanAsync(ChannelBackupEntry entry, uint belowHeight,
                                           CancellationToken cancellationToken);
}
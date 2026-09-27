namespace NLightning.Application.Channels.Backup.Interfaces;

using Domain.Bitcoin.Events;
using Domain.Crypto.ValueObjects;
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

    /// <summary>
    /// When <paramref name="spend"/> (a spend of <paramref name="entry"/>'s funding output found by
    /// <see cref="LocateAsync"/> or <see cref="RescanAsync"/>) is a splice of the channel rather than a commitment, the
    /// entry moved to the splice's funding output (outpoint, capacity, both keys, our key index, funding height and
    /// short channel id); null for a commitment, a mutual close or a transaction whose funding output can't be
    /// recognized (splicing plan lane SP2-E, NL-478; see <see cref="SpliceSpendFollower"/>). Never throws for a chain
    /// failure.
    /// </summary>
    /// <param name="entry">The channel at the funding the spend spends.</param>
    /// <param name="spend">The spend.</param>
    /// <param name="deriveLocalFundingKey">Our funding key of this channel at a funding key index (null when it can't
    /// be derived).</param>
    /// <param name="cancellationToken">Stops the search.</param>
    Task<ChannelBackupEntry?> FollowSpliceAsync(ChannelBackupEntry entry, OutpointSpentEventArgs spend,
                                                Func<uint, CompactPubKey?> deriveLocalFundingKey,
                                                CancellationToken cancellationToken) =>
        Task.FromResult<ChannelBackupEntry?>(null);
}
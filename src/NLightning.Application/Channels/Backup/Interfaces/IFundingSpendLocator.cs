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
    /// <summary>Checks the funding output of <paramref name="entry"/>; never throws for a chain failure.</summary>
    Task<FundingSpendLocation> LocateAsync(ChannelBackupEntry entry, CancellationToken cancellationToken);
}
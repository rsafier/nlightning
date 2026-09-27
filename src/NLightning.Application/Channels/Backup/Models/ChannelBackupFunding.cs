namespace NLightning.Application.Channels.Backup.Models;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A pending splice funding of a backed-up channel (splicing plan SP2-0, lane SP2-E): if the backup is older than the
/// lock, the restore still recognizes a spend of <see cref="ChannelBackupEntry.FundingTxId"/> by this splice and follows
/// the channel to its new outpoint, whose commitment the peer then broadcasts. Public data only: our key is re-derived
/// from the channel key index and <see cref="LocalFundingKeyIndex"/> (D5).
/// </summary>
/// <param name="FundingTxId">The splice transaction id.</param>
/// <param name="FundingOutputIndex">Its funding output index.</param>
/// <param name="CapacitySat">Its funding output amount, in sat.</param>
/// <param name="LocalFundingKeyIndex">Our funding key index for it (D5).</param>
/// <param name="LocalFundingPubKey">Our funding key for it (checks the index).</param>
/// <param name="RemoteFundingPubKey">The peer's funding key for it.</param>
public sealed record ChannelBackupFunding(
    TxId FundingTxId,
    ushort FundingOutputIndex,
    ulong CapacitySat,
    uint LocalFundingKeyIndex,
    CompactPubKey LocalFundingPubKey,
    CompactPubKey RemoteFundingPubKey);
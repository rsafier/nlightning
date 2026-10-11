namespace NLightning.Application.Onchain.Interfaces;

using Channels.Safety.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;

/// <summary>
/// Force close while a splice is pending (splicing plan §3.6, SP2-C-T2): our commitment on the funding the splice
/// spends was broadcast, and the splice confirmed instead (both spend that output, exactly one confirms). Our latest
/// commitment on the splice funding is then signed for broadcast (SP-I4 allows the same number on another active
/// funding), persisted as a <c>LocalCommitment</c> broadcast row and published; the row of the commitment that can no
/// longer confirm is abandoned. Implemented by the failure service, the only broadcast path of our commitments (D10).
/// </summary>
public interface ISpliceCommitmentBroadcaster
{
    /// <summary>
    /// Broadcasts our latest commitment on the pending splice funding <paramref name="spliceFundingTxId"/> of a failed
    /// channel (idempotent: an existing row is published again). Takes the channel's lock itself; the caller must hold
    /// none.
    /// </summary>
    /// <returns><see cref="ChannelFailureStatus.NotApplicable"/> when the channel is not loaded, not failed or closing
    /// on chain, or the funding is not one of its pending splices; <see cref="ChannelFailureStatus.RefusedDataLoss"/>
    /// after data loss.</returns>
    Task<ChannelFailureOutcome> BroadcastOnSpliceAsync(ChannelId channelId, TxId spliceFundingTxId,
                                                       CancellationToken cancellationToken = default);
}
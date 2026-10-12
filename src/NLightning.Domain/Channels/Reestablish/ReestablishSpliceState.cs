namespace NLightning.Domain.Channels.Reestablish;

using Bitcoin.ValueObjects;

/// <summary>
/// A pending splice as the <c>my_current_funding_locked</c> rules see it (SP-RE-02, SP-RE-04).
/// </summary>
/// <param name="TxId">The splice transaction id.</param>
/// <param name="SpliceLockedSent">We sent (and persisted) <c>splice_locked</c> for it, possibly while disconnected
/// (the depth watcher marks it sent before publishing).</param>
/// <param name="SpliceLockedReceived">We received the peer's <c>splice_locked</c> for it.</param>
public sealed record ReestablishPendingSplice(TxId TxId, bool SpliceLockedSent, bool SpliceLockedReceived);

/// <summary>
/// The funding-lock facts of a channel for <c>my_current_funding_locked</c> (BOLT 2 channel_reestablish TLV 5;
/// splicing plan SP-RE-02, SP-RE-04, SP-RE-05, SP2-0). Built by the Application reestablish code from the channel's
/// <c>FundingSet</c> and announcement state (lane SP2-A); null in <see cref="ReestablishLocalState.Splice"/> when
/// <c>option_splice</c> is not negotiated (then TLV 5 is never sent and a received one is ignored).
/// </summary>
/// <param name="ChannelReadySent">We sent <c>channel_ready</c> (the fallback txid of SP-RE-02 is then the funding).
/// </param>
/// <param name="CurrentFundingTxId">The channel's current funding (the original one, or the last locked splice).
/// </param>
/// <param name="LastSpliceLockedSent">The last splice we sent <c>splice_locked</c> for (current or pending), or null.
/// </param>
/// <param name="AnnounceChannel">The channel is public (<c>announce_channel</c>).</param>
/// <param name="PendingSplices">The pending splices, oldest first.</param>
/// <param name="AnnouncementSignaturesReceivedFor">The fundings whose <c>announcement_signatures</c> from the peer we
/// hold (retransmit bit 0 of our TLV 5 is set when ours names a txid outside this set on a public channel).</param>
/// <param name="AnnouncementSignaturesReadyFor">The fundings for which we are ready to send
/// <c>announcement_signatures</c> now (SP-RE-04 bit 0, SP-G-01: locked both ways and announcement depth).</param>
/// <param name="CurrentFundingIsSplice">The current funding is a locked splice, not the channel's original funding
/// (SP-RE-05: a <c>my_current_funding_locked</c> naming it is "for a splice transaction").</param>
public sealed record ReestablishSpliceState(
    bool ChannelReadySent,
    TxId CurrentFundingTxId,
    TxId? LastSpliceLockedSent,
    bool AnnounceChannel,
    IReadOnlyList<ReestablishPendingSplice> PendingSplices,
    IReadOnlyCollection<TxId> AnnouncementSignaturesReceivedFor,
    IReadOnlyCollection<TxId> AnnouncementSignaturesReadyFor,
    bool CurrentFundingIsSplice = false)
{
    /// <summary>Whether <paramref name="txId"/> is a splice transaction of the channel (pending, or the locked current
    /// funding when that is a splice).</summary>
    public bool IsSplice(TxId txId) =>
        PendingSplices.Any(p => p.TxId == txId) || (CurrentFundingIsSplice && CurrentFundingTxId == txId);
}
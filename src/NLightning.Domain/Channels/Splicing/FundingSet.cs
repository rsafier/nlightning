namespace NLightning.Domain.Channels.Splicing;

using Bitcoin.ValueObjects;
using Enums;

/// <summary>
/// The fundings of a channel (splicing plan §3.3, D6, D7): the <see cref="Current"/> one and the ordered
/// <see cref="Pending"/> splices (at most one splice and its RBF attempts, SP-S-01). Immutable: every change returns a
/// new set.
/// </summary>
/// <remarks>
/// The read-only members are implemented here; the transitions (<see cref="AddPending"/>, <see cref="Lock"/>,
/// <see cref="Discard"/>) are lane SP1-B's (SP1-B-T1/T3), with their rules: a new splice only when nothing is pending,
/// RBF siblings of the pending splice, lock folds the deltas and discards siblings and ancestors.
/// <c>ChannelModel.FundingOutput</c> stays a view of <see cref="Current"/> so the single-funding code keeps working.
/// </remarks>
/// <param name="Current">The current funding (<see cref="ChannelFundingStatus.Current"/>).</param>
/// <param name="Pending">The pending splice attempts, oldest first (<see cref="ChannelFundingStatus.Pending"/>).</param>
public sealed record FundingSet(ChannelFunding Current, IReadOnlyList<ChannelFunding> Pending)
{
    /// <summary>A channel that was never spliced.</summary>
    public static FundingSet Single(ChannelFunding current) => new(current, []);

    /// <summary>Whether a splice is negotiated but not locked (SP-S-01: no new <c>splice_init</c> then).</summary>
    public bool HasPending => Pending.Count > 0;

    /// <summary>Every funding commitments are signed for: <see cref="Current"/> first, then <see cref="Pending"/>
    /// (the order of the batched <c>commitment_signed</c>s, SP-OP-03).</summary>
    public IEnumerable<ChannelFunding> Active => Pending.Prepend(Current);

    /// <summary>The number of active fundings (the <c>start_batch</c> size when above 1).</summary>
    public int ActiveCount => Pending.Count + 1;

    /// <summary>The active funding with <paramref name="fundingTxId"/>, or null.</summary>
    public ChannelFunding? Find(TxId fundingTxId) => Active.FirstOrDefault(f => f.FundingTxId == fundingTxId);

    /// <summary>Adds a negotiated splice (or RBF attempt) as pending (lane SP1-B).</summary>
    public FundingSet AddPending(ChannelFunding funding) =>
        throw new NotImplementedException("Lane SP1-B (SP1-B-T1)");

    /// <summary>
    /// Locks the pending funding <paramref name="fundingTxId"/> (<c>splice_locked</c> sent and received for it): it
    /// becomes <see cref="Current"/> with its deltas folded, the former current becomes
    /// <see cref="ChannelFundingStatus.Replaced"/>, its siblings <see cref="ChannelFundingStatus.Discarded"/> (lane
    /// SP1-B-T3).
    /// </summary>
    /// <returns>The new set and the fundings that left it (replaced and discarded), for the lock's save.</returns>
    public (FundingSet Next, IReadOnlyList<ChannelFunding> Retired) Lock(TxId fundingTxId) =>
        throw new NotImplementedException("Lane SP1-B (SP1-B-T3)");

    /// <summary>
    /// Discards every pending funding (a commitment of the current funding confirmed, splicing plan §3.6) (lane
    /// SP1-B, used by SP2-C).
    /// </summary>
    public (FundingSet Next, IReadOnlyList<ChannelFunding> Discarded) Discard() =>
        throw new NotImplementedException("Lane SP1-B");
}
namespace NLightning.Domain.Accounting.Books.Reports;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// The per-channel and per-peer view of [<see cref="Since"/>, <see cref="Until"/>) (plan §6.1 "Reports"), built from
/// the feed's events and their details (the books keep one aggregate account per bucket, not per channel).
/// </summary>
/// <param name="Since">The start (inclusive), or null for the start of the feed.</param>
/// <param name="Until">The end (exclusive), or null for now.</param>
/// <param name="AsOf">The time the open periods of the yields end at (<see cref="Until"/> or now).</param>
/// <param name="ProjectedLedgerSeq">The books' cursor.</param>
/// <param name="Channels">One line per channel with an event in the period (or the one channel asked for), ordered by
/// channel id.</param>
/// <param name="Peers">The channel lines summed per peer, ordered by node id (a channel whose peer is unknown is summed
/// under a null peer, last).</param>
public sealed record AccountingChannelsReport(
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    DateTimeOffset AsOf,
    long ProjectedLedgerSeq,
    IReadOnlyList<AccountingChannelLine> Channels,
    IReadOnlyList<AccountingPeerLine> Peers);

/// <summary>
/// One channel's money over a period. Amounts are msat and positive unless said otherwise.
/// </summary>
/// <remarks>
/// A forward's fee is shown on both its channels (<see cref="RoutingInMsat"/> on the incoming one,
/// <see cref="RoutingOutMsat"/> on the outgoing one), so the routing columns of several channels do not add up to the
/// node's routing income; the yield and <see cref="NetMsat"/> use the outgoing side only, the channel whose policy set
/// the fee (BOLT 7).
/// </remarks>
public sealed record AccountingChannelLine
{
    public required ChannelId ChannelId { get; init; }

    /// <summary>The latest short channel id seen in the feed (<c>block x tx x output</c>), when known.</summary>
    public string? ShortChannelId { get; init; }

    /// <summary>The channel's peer, when known.</summary>
    public CompactPubKey? Counterparty { get; init; }

    /// <summary>The channel's latest capacity (funding, then splices), when its funding is in the feed.</summary>
    public long? CapacityMsat { get; init; }

    /// <summary>Whether we opened it, when known.</summary>
    public bool? IsInitiator { get; init; }

    /// <summary>When its funding confirmed, when in the feed.</summary>
    public DateTimeOffset? OpenedAt { get; init; }

    /// <summary>When it closed (mutual close or force close classified), when it did.</summary>
    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>Fees of forwards that came in over this channel.</summary>
    public long RoutingInMsat { get; init; }

    /// <summary>Fees of forwards that went out over this channel.</summary>
    public long RoutingOutMsat { get; init; }

    public int ForwardsIn { get; init; }
    public int ForwardsOut { get; init; }

    /// <summary>The amounts forwarded in over this channel.</summary>
    public long ForwardedInMsat { get; init; }

    /// <summary>The amounts forwarded out over this channel.</summary>
    public long ForwardedOutMsat { get; init; }

    /// <summary>Our payments sent out over this channel (not counting self-payments), fees excluded.</summary>
    public long PaymentsSentMsat { get; init; }

    public int PaymentsSent { get; init; }

    /// <summary>Our invoices settled over this channel.</summary>
    public long PaymentsReceivedMsat { get; init; }

    public int PaymentsReceived { get; init; }

    /// <summary>Route fees of our payments (not self-payments) sent over this channel.</summary>
    public long RoutingFeesPaidMsat { get; init; }

    /// <summary>The amounts of our self-payments (rebalances) sent out over this channel.</summary>
    public long RebalancedOutMsat { get; init; }

    /// <summary>Route fees of our self-payments (rebalances) sent out over this channel.</summary>
    public long RebalanceCostMsat { get; init; }

    /// <summary>The push at the open: positive when the peer pushed to us, negative when we pushed.</summary>
    public long PushMsat { get; init; }

    public long FundingFeeMsat { get; init; }
    public long SpliceFeeMsat { get; init; }
    public long CloseFeeMsat { get; init; }
    public long CommitmentFeeMsat { get; init; }

    /// <summary>Fees of our sweeps and claims of this channel's outputs on chain.</summary>
    public long SweepFeeMsat { get; init; }

    /// <summary>Fees of anchor CPFP children of this channel's commitment.</summary>
    public long CpfpFeeMsat { get; init; }

    /// <summary>Value lost on chain: trimmed or dust value of a force close, forwards lost on chain.</summary>
    public long OnchainLossMsat { get; init; }

    public long OnchainFeesMsat =>
        FundingFeeMsat + SpliceFeeMsat + CloseFeeMsat + CommitmentFeeMsat + SweepFeeMsat + CpfpFeeMsat;

    /// <summary>What the channel earned as a fee earner: routing out, less rebalance cost, on-chain fees and losses.</summary>
    public long NetMsat => RoutingOutMsat - RebalanceCostMsat - OnchainFeesMsat - OnchainLossMsat;

    /// <summary><see cref="RoutingOutMsat"/> over <see cref="CapacityMsat"/> (null when the capacity is unknown).</summary>
    public double? YieldOnCapacity { get; init; }

    /// <summary>
    /// <see cref="YieldOnCapacity"/> annualized over the time the channel was open within the period (null when the
    /// open time is unknown or shorter than an hour).
    /// </summary>
    public double? AnnualizedYield { get; init; }
}

/// <summary>
/// The channel lines of one peer summed (see <see cref="AccountingChannelLine"/> for the columns).
/// </summary>
public sealed record AccountingPeerLine
{
    /// <summary>The peer, or null for channels whose peer is not in the feed.</summary>
    public CompactPubKey? Counterparty { get; init; }

    public int ChannelCount { get; init; }

    /// <summary>The sum of the known capacities.</summary>
    public long CapacityMsat { get; init; }

    public long RoutingInMsat { get; init; }
    public long RoutingOutMsat { get; init; }
    public int ForwardsIn { get; init; }
    public int ForwardsOut { get; init; }
    public long PaymentsSentMsat { get; init; }
    public long PaymentsReceivedMsat { get; init; }
    public long RoutingFeesPaidMsat { get; init; }
    public long RebalanceCostMsat { get; init; }
    public long OnchainFeesMsat { get; init; }
    public long OnchainLossMsat { get; init; }
    public long NetMsat { get; init; }
}
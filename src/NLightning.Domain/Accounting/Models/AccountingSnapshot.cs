namespace NLightning.Domain.Accounting.Models;

using Channels.Enums;
using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// The node's live balances by bucket at one moment (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §7 IPC 42, §10): one
/// bucket per channel and the on-chain wallet. Amounts are msat.
/// </summary>
/// <remarks>
/// <para>A pending funding is counted twice until it confirms: the channel's local balance is in its bucket while
/// the wallet outputs the funding spends are still in <see cref="WalletBalanceBucket.ConfirmedMsat"/> (they show in
/// <see cref="WalletBalanceBucket.LockedMsat"/> meanwhile).</para>
/// <para>Nothing here is persisted; a snapshot is a reading, not an accounting event.</para>
/// </remarks>
/// <param name="TakenAt">When it was taken.</param>
/// <param name="BlockHeight">The chain monitor's last processed block (0 when unknown).</param>
/// <param name="Channels">One bucket per channel, loaded channels first, then the channels resolved on chain that are
/// not loaded.</param>
/// <param name="Wallet">The on-chain wallet.</param>
public sealed record AccountingSnapshot(
    DateTimeOffset TakenAt,
    uint BlockHeight,
    IReadOnlyList<ChannelBalanceBucket> Channels,
    WalletBalanceBucket Wallet)
{
    /// <summary>Our off-chain balance over every channel whose funding is not spent (gross, see
    /// <see cref="ChannelBalanceBucket.LocalBalanceMsat"/>).</summary>
    public long ChannelLocalMsat => Channels.Sum(c => c.LocalBalanceMsat);

    /// <summary>Our outputs of force closes still waiting for their sweep (see
    /// <see cref="ChannelBalanceBucket.PendingOnchainMsat"/>).</summary>
    public long PendingOnchainMsat => Channels.Sum(c => c.PendingOnchainMsat);

    /// <summary>HTLC outputs of force closes not resolved yet (see
    /// <see cref="ChannelBalanceBucket.PendingHtlcOnchainMsat"/>).</summary>
    public long PendingHtlcOnchainMsat => Channels.Sum(c => c.PendingHtlcOnchainMsat);

    /// <summary>Outputs of force closes we may still take that the books do not count yet (see
    /// <see cref="ChannelBalanceBucket.PendingUncountedMsat"/>).</summary>
    public long PendingUncountedMsat => Channels.Sum(c => c.PendingUncountedMsat);

    /// <summary>How many outputs of force closes are still waiting for a resolution of ours.</summary>
    public int PendingSweepCount => Channels.Sum(c => c.PendingSweepCount);

    /// <summary>Everything above plus the wallet (confirmed and unconfirmed).</summary>
    public long TotalMsat => ChannelLocalMsat + PendingOnchainMsat + PendingHtlcOnchainMsat + Wallet.ConfirmedMsat
                           + Wallet.UnconfirmedMsat;
}

/// <summary>
/// One channel's balances in an <see cref="AccountingSnapshot"/>.
/// </summary>
/// <remarks>
/// Off-chain amounts (<see cref="LocalBalanceMsat"/>, <see cref="RemoteBalanceMsat"/> and the in-flight HTLCs) are 0
/// once the funding is spent (<see cref="ChannelState.OnchainResolving"/>): from then on the money is in the commitment's
/// outputs (<see cref="PendingOnchainMsat"/>, <see cref="PendingHtlcOnchainMsat"/>) or already in the wallet.
/// </remarks>
/// <param name="ChannelId">The channel.</param>
/// <param name="ShortChannelId">Its short channel id, once its funding confirmed and when it is loaded.</param>
/// <param name="State">Its state (<see cref="ChannelState.OnchainResolving"/> for a channel that is not loaded).</param>
/// <param name="Counterparty">The peer, when the channel is loaded.</param>
/// <param name="CapacityMsat">The current funding's amount (0 when unknown).</param>
/// <param name="LocalBalanceMsat">Our gross balance: it still includes our offered HTLCs that are not final (the
/// commitment engine's convention, NL-062).</param>
/// <param name="RemoteBalanceMsat">The peer's gross balance (same convention).</param>
/// <param name="LocalInFlightMsat">Our offered HTLCs not final yet (part of <see cref="LocalBalanceMsat"/>).</param>
/// <param name="RemoteInFlightMsat">The peer's offered HTLCs not final yet (part of
/// <see cref="RemoteBalanceMsat"/>).</param>
/// <param name="PendingOnchainMsat">Our outputs of the channel's force close that no transaction spent yet and that the
/// books count as pending (to_local, to_remote, a funder's anchor, second-level outputs): pending, waiting or
/// broadcast.</param>
/// <param name="PendingHtlcOnchainMsat">The channel's HTLC outputs on chain not resolved yet that the books count as
/// pending (the HTLCs we offered): either side may still take them.</param>
/// <param name="PendingSweepCount">How many outputs make up the three pending amounts.</param>
/// <param name="IsLoaded">Whether the channel is loaded in memory (false: only its on-chain outputs are known).</param>
/// <param name="PendingUncountedMsat">Unresolved outputs we may still take that the books book only once claimed
/// (the peer's HTLCs, a revoked commitment's outputs, a fundee's anchor; NL-618): a possible gain, not part of
/// <see cref="AccountingSnapshot.TotalMsat"/>.</param>
/// <param name="LocalInFlightFulfilledMsat">The part of <paramref name="LocalInFlightMsat"/> the peer fulfilled (we know
/// the preimage, so the money is paid; NL-602 A3-T6, the risk-weighted view).</param>
/// <param name="RemoteInFlightPreimageMsat">The part of <paramref name="RemoteInFlightMsat"/> whose preimage we hold (we
/// fulfilled it, or accepted it as the final node; A3-T6).</param>
public sealed record ChannelBalanceBucket(
    ChannelId ChannelId,
    ShortChannelId? ShortChannelId,
    ChannelState State,
    CompactPubKey? Counterparty,
    long CapacityMsat,
    long LocalBalanceMsat,
    long RemoteBalanceMsat,
    long LocalInFlightMsat,
    long RemoteInFlightMsat,
    long PendingOnchainMsat,
    long PendingHtlcOnchainMsat,
    int PendingSweepCount,
    bool IsLoaded,
    long PendingUncountedMsat = 0,
    long LocalInFlightFulfilledMsat = 0,
    long RemoteInFlightPreimageMsat = 0);

/// <summary>
/// The on-chain wallet in an <see cref="AccountingSnapshot"/> (the <c>walletbalance</c> numbers).
/// </summary>
/// <param name="ConfirmedMsat">Outputs with at least 3 confirmations.</param>
/// <param name="UnconfirmedMsat">The other outputs.</param>
/// <param name="LockedMsat">Outputs locked to a channel funding or reserved for a fee (part of the two above).</param>
public sealed record WalletBalanceBucket(long ConfirmedMsat, long UnconfirmedMsat, long LockedMsat);
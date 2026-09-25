namespace NLightning.Domain.Channels.Commitments;

using Crypto.ValueObjects;
using Enums;

/// <summary>
/// One HTLC tracked by the commitment state machine.
/// </summary>
/// <param name="Direction"><see cref="HtlcDirection.Outgoing"/> when we offered it (ids from our counter),
/// <see cref="HtlcDirection.Incoming"/> when the peer did.</param>
/// <param name="Id">The <c>update_add_htlc</c> id (unique per direction).</param>
/// <param name="AmountMsat">The amount in millisatoshi.</param>
/// <param name="PaymentHash">The payment hash.</param>
/// <param name="CltvExpiry">The absolute block height at which the HTLC times out.</param>
/// <param name="State">The authoritative per-HTLC state (<see cref="HtlcStateTable"/>).</param>
/// <param name="Removal">Set once a fulfill/fail was sent or received (states 15-19, 35-39).</param>
/// <param name="OnionRoutingPacket">The 1366-byte onion of the <c>update_add_htlc</c> (opaque to the engine; kept so
/// the add can be retransmitted and the onion peeled after lock-in).</param>
/// <param name="PathKey">The <c>blinded_path</c> TLV <c>path_key</c> of the add, if any.</param>
public sealed record HtlcRecord(
    HtlcDirection Direction,
    ulong Id,
    ulong AmountMsat,
    Hash PaymentHash,
    uint CltvExpiry,
    HtlcState State,
    HtlcRemoval? Removal = null,
    ReadOnlyMemory<byte> OnionRoutingPacket = default,
    CompactPubKey? PathKey = null)
{
    public HtlcKey Key => new(Direction, Id);

    /// <summary>The HTLC is in the latest commitment of <paramref name="side"/>.</summary>
    public bool IsInCommit(CommitmentSide side) => HtlcStateTable.IsInCommit(State, side);

    /// <summary>True when the holder of the <paramref name="side"/> commitment offered this HTLC (an "offered" output
    /// in BOLT 3 terms, spent by HTLC-timeout); false for a "received" output (spent by HTLC-success).</summary>
    public bool IsOfferedBy(CommitmentSide side) =>
        (side == CommitmentSide.Local) == (Direction == HtlcDirection.Outgoing);
}
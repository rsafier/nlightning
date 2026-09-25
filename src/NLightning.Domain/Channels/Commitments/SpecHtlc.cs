namespace NLightning.Domain.Channels.Commitments;

using Crypto.ValueObjects;
using Enums;

/// <summary>
/// An HTLC as it appears in one commitment (<see cref="CommitmentSpec"/>).
/// </summary>
/// <param name="Direction">Outgoing = we offered it (our perspective, independent of the commitment holder).</param>
/// <param name="Id">The <c>update_add_htlc</c> id.</param>
/// <param name="AmountMsat">The amount in millisatoshi.</param>
/// <param name="PaymentHash">The payment hash.</param>
/// <param name="CltvExpiry">The absolute expiry height.</param>
public readonly record struct SpecHtlc(
    HtlcDirection Direction,
    ulong Id,
    ulong AmountMsat,
    Hash PaymentHash,
    uint CltvExpiry)
{
    /// <summary>True when the holder of a <paramref name="holder"/> commitment offered this HTLC (BOLT 3 "offered"
    /// output, HTLC-timeout); false for a "received" output (HTLC-success).</summary>
    public bool IsOfferedBy(CommitmentSide holder) =>
        (holder == CommitmentSide.Local) == (Direction == HtlcDirection.Outgoing);
}
// ReSharper disable PropertyCanBeMadeInitOnly.Global

using NLightning.Domain.Channels.ValueObjects;

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

/// <summary>
/// One HTLC of the commitment state machine (<c>HtlcRecord</c>), written row by row by <c>ChannelStateDbRepository</c>.
/// </summary>
/// <remarks>
/// Rows in a final state (19, 39) are kept as an archive until pruned, so that events can be re-derived after a crash.
/// Rows in a legacy state (0-3) come from before migration <c>AddCommitmentState</c> and cannot be restored (NL-025).
/// </remarks>
public class HtlcEntity
{
    /// <summary>
    /// The channel of the HTLC.
    /// </summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required ChannelId ChannelId { get; set; }

    /// <summary>
    /// The <c>update_add_htlc</c> id (unique per direction).
    /// </summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required ulong HtlcId { get; set; }

    /// <summary>
    /// <c>HtlcDirection</c>: outgoing when we offered it.
    /// </summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required byte Direction { get; set; }

    /// <summary>
    /// The amount in millisatoshi.
    /// </summary>
    public required ulong AmountMsat { get; set; }

    /// <summary>
    /// The 32-byte payment hash.
    /// </summary>
    public required byte[] PaymentHash { get; set; }

    /// <summary>
    /// The absolute block height at which the HTLC times out.
    /// </summary>
    public required uint CltvExpiry { get; set; }

    /// <summary>
    /// The <c>HtlcState</c> (10-19 we offered, 30-39 they offered; 0-3 legacy).
    /// </summary>
    public required byte State { get; set; }

    /// <summary>
    /// The 1366-byte onion of the <c>update_add_htlc</c> (empty only in tests of the engine).
    /// </summary>
    public required byte[] OnionRoutingPacket { get; set; }

    /// <summary>
    /// The <c>blinded_path</c> TLV <c>path_key</c> of the add, if any (33 bytes).
    /// </summary>
    public byte[]? PathKey { get; set; }

    /// <summary>
    /// <c>HtlcRemovalKind</c> once a fulfill/fail was sent or received (states 15-19, 35-39); null before.
    /// </summary>
    public byte? RemovalKind { get; set; }

    /// <summary>
    /// The preimage of a fulfill removal.
    /// </summary>
    public byte[]? PaymentPreimage { get; set; }

    /// <summary>
    /// The opaque failure onion of a fail removal.
    /// </summary>
    public byte[]? FailReason { get; set; }

    /// <summary>
    /// The BOLT 4 failure code of a fail-malformed removal.
    /// </summary>
    public ushort? FailureCode { get; set; }

    /// <summary>
    /// The onion hash of a fail-malformed removal (32 bytes).
    /// </summary>
    public byte[]? Sha256OfOnion { get; set; }

    /// <summary>
    /// A preimage the peer revealed for an HTLC we offered; it survives the reversal of an unsigned fulfill (BOLT 2).
    /// </summary>
    public byte[]? KnownPreimage { get; set; }

    /// <summary>
    /// The Sphinx shared secret of the onion we peeled for this HTLC, needed to wrap its failure (ONION M4). Written
    /// on its own, never by a state transition.
    /// </summary>
    public byte[]? OnionSharedSecret { get; set; }

    /// <summary>
    /// <c>HtlcOriginKind</c> of an HTLC we offered (1 our payment, 2 a forward); null when unknown (incoming HTLCs,
    /// rows from before migration <c>AddInvoicesPaymentsAndCircuits</c>). Written on its own
    /// (<c>ChannelStateDbRepository.SetHtlcOriginAsync</c>), never by a state transition.
    /// </summary>
    public byte? OriginKind { get; set; }

    /// <summary>
    /// The payment hash of our payment, for <see cref="OriginKind"/> 1.
    /// </summary>
    public byte[]? OriginPaymentHash { get; set; }

    /// <summary>
    /// The channel of the forwarded incoming HTLC, for <see cref="OriginKind"/> 2.
    /// </summary>
    public ChannelId? OriginIncomingChannelId { get; set; }

    /// <summary>
    /// The id of the forwarded incoming HTLC, for <see cref="OriginKind"/> 2.
    /// </summary>
    public ulong? OriginIncomingHtlcId { get; set; }

    // Default constructor for EF Core
    internal HtlcEntity()
    {
    }
}
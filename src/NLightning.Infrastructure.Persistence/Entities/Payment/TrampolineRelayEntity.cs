// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Crypto.ValueObjects;

/// <summary>
/// A trampoline payment we relay (<c>TrampolineRelayModel</c>, NL-875): the incoming MPP set of one payment hash and
/// the outgoing payment that continues it. Keyed by the payment hash; its incoming parts are
/// <see cref="TrampolineRelayPartEntity"/> rows.
/// </summary>
/// <remarks>
/// Indexed by status (the startup replay of unfinished relays) and creation time (listings, newest first). No foreign
/// key to the payment or channel rows: a relay is resolved through its own rows and the HTLC origins
/// (<c>Htlcs.OriginKind</c> 3 with the payment hash).
/// </remarks>
public class TrampolineRelayEntity
{
    /// <summary>The 32-byte payment hash.</summary>
    public required Hash PaymentHash { get; set; }

    /// <summary><c>TrampolineRelayStatus</c> (0 collecting, 1 sending, 2 fulfilled, 3 failed).</summary>
    public required byte Status { get; set; }

    /// <summary>The next trampoline node or the recipient; null for a payment to blinded paths.</summary>
    public CompactPubKey? NextNodeId { get; set; }

    /// <summary>A blinded trampoline hop's <c>encrypted_recipient_data</c>.</summary>
    public byte[]? NextEncryptedRecipientData { get; set; }

    /// <summary>Its <c>path_key</c>.</summary>
    public byte[]? NextPathKey { get; set; }

    /// <summary>The recipient's features.</summary>
    public byte[]? RecipientFeatures { get; set; }

    /// <summary>The recipient's blinded paths (raw TLV bytes).</summary>
    public byte[]? RecipientBlindedPaths { get; set; }

    /// <summary>The peeled trampoline onion to forward.</summary>
    public byte[]? NextTrampolinePacket { get; set; }

    /// <summary>What the next node must receive, in millisatoshi.</summary>
    public required long AmountOutMsat { get; set; }

    /// <summary>The next node's <c>outgoing_cltv_value</c>.</summary>
    public required uint CltvExpiryOut { get; set; }

    /// <summary>The outer onion's <c>total_msat</c>, in millisatoshi.</summary>
    public required long IncomingTotalMsat { get; set; }

    /// <summary>What the relay earned once fulfilled, in millisatoshi.</summary>
    public long? FeeEarnedMsat { get; set; }

    /// <summary>The <c>payment_secret</c> of our outgoing payment.</summary>
    public byte[]? OutgoingPaymentSecret { get; set; }

    /// <summary>The preimage, once fulfilled.</summary>
    public byte[]? Preimage { get; set; }

    /// <summary>The BOLT 4 failure code returned upstream, once failed.</summary>
    public ushort? FailureCode { get; set; }

    /// <summary>Why the relay failed (local text).</summary>
    public string? FailureReason { get; set; }

    /// <summary>When the first incoming part arrived (stored as UTC ticks).</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the relay was fulfilled or failed (stored as UTC ticks).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    // Default constructor for EF Core
    internal TrampolineRelayEntity()
    {
    }
}
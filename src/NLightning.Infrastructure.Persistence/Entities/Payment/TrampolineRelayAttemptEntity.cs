// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Crypto.ValueObjects;

/// <summary>
/// A failed trampoline relay that a payer's retry with the same payment hash replaced (<c>TrampolineRelayAttemptModel</c>,
/// NL-899, migration <c>AddTrampolineRelayAttempts</c>): the summary <c>listforwards</c> keeps of it once its
/// <see cref="TrampolineRelayEntity"/> row and parts made way for the new attempt. Keyed by the payment hash and the
/// attempt number (from 1); written once, in the save that removes the relay, and never changed.
/// </summary>
/// <remarks>
/// No foreign key: the relay row it came from is gone. Indexed by creation time (listings, newest first). Nothing
/// but the listings reads it: the relay engine, the switch and the resolvers know only the current relay.
/// </remarks>
public class TrampolineRelayAttemptEntity
{
    /// <summary>The 32-byte payment hash.</summary>
    public required Hash PaymentHash { get; set; }

    /// <summary>The replaced attempt's number for the hash, from 1.</summary>
    public required int Attempt { get; set; }

    /// <summary>The next trampoline node; null for a payment to blinded paths.</summary>
    public CompactPubKey? NextNodeId { get; set; }

    /// <summary>What the next node had to receive, in millisatoshi.</summary>
    public required long AmountOutMsat { get; set; }

    /// <summary>The next node's <c>outgoing_cltv_value</c>.</summary>
    public required uint CltvExpiryOut { get; set; }

    /// <summary>The outer onion's <c>total_msat</c>, in millisatoshi.</summary>
    public required long IncomingTotalMsat { get; set; }

    /// <summary>The sum of the incoming parts, in millisatoshi.</summary>
    public required long IncomingAmountMsat { get; set; }

    /// <summary>How many incoming parts the attempt had.</summary>
    public required int Parts { get; set; }

    /// <summary>The channel ids the parts came in on, 32 bytes each, in part order without repeats.</summary>
    public required byte[] IncomingChannelIds { get; set; }

    /// <summary>The BOLT 4 failure code returned upstream.</summary>
    public ushort? FailureCode { get; set; }

    /// <summary>Why the relay failed (local text).</summary>
    public string? FailureReason { get; set; }

    /// <summary>When the first incoming part arrived (stored as UTC ticks).</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the relay failed (stored as UTC ticks).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    // Default constructor for EF Core
    internal TrampolineRelayAttemptEntity()
    {
    }
}
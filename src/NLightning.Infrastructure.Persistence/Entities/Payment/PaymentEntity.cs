// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// One of our outgoing payments (<c>PaymentModel</c>, BOLT2 plan N8-T3): the latest attempt for its payment hash.
/// </summary>
public class PaymentEntity
{
    /// <summary>
    /// The 32-byte payment hash.
    /// </summary>
    /// <remarks>This is the primary key</remarks>
    public required Hash PaymentHash { get; set; }

    /// <summary>
    /// The BOLT 11 invoice paid, if any.
    /// </summary>
    public string? Bolt11 { get; set; }

    /// <summary>
    /// The payee's node id.
    /// </summary>
    public required CompactPubKey PayeeNodeId { get; set; }

    /// <summary>
    /// The amount the payee receives, in millisatoshi.
    /// </summary>
    public required long AmountMsat { get; set; }

    /// <summary>
    /// The routing fees, in millisatoshi.
    /// </summary>
    public required long FeeMsat { get; set; }

    /// <summary>
    /// When the payment was created (stored as UTC ticks).
    /// </summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// <c>PaymentStatus</c> (0 in flight, 1 succeeded, 2 failed).
    /// </summary>
    public required byte Status { get; set; }

    /// <summary>
    /// The channel our HTLC was offered on, once recorded.
    /// </summary>
    public ChannelId? OutgoingChannelId { get; set; }

    /// <summary>
    /// The id of our HTLC on <see cref="OutgoingChannelId"/>, once recorded.
    /// </summary>
    public ulong? OutgoingHtlcId { get; set; }

    /// <summary>
    /// The 32-byte preimage, once succeeded.
    /// </summary>
    public byte[]? Preimage { get; set; }

    /// <summary>
    /// The BOLT 4 failure code decoded at the origin, if any.
    /// </summary>
    public ushort? FailureCode { get; set; }

    /// <summary>
    /// The route index of the node that produced the failure, if attributable.
    /// </summary>
    public int? FailureSourceIndex { get; set; }

    /// <summary>
    /// A local description of the failure.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// When the payment completed (stored as UTC ticks).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The route of the onion, with each hop's shared secret (cascade-deleted with the payment).
    /// </summary>
    public virtual ICollection<PaymentHopEntity>? Hops { get; set; }

    // Default constructor for EF Core
    internal PaymentEntity()
    {
    }
}
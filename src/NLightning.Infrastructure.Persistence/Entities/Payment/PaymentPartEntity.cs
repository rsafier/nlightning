// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// One offered part (HTLC) of an outgoing payment (<c>PaymentPartModel</c>, NL-321): what the origin needs to resolve
/// the part and read its failure after a restart, when it is not the part the <c>Payments</c> row records.
/// </summary>
public class PaymentPartEntity
{
    /// <summary>
    /// The payment hash of the payment the part belongs to.
    /// </summary>
    /// <remarks>This is part of the composite primary key (and the foreign key to <c>Payments</c>)</remarks>
    public required Hash PaymentHash { get; set; }

    /// <summary>
    /// The position among the payment's offered parts, in offer order (0 first).
    /// </summary>
    /// <remarks>This is part of the composite primary key</remarks>
    public required byte PartIndex { get; set; }

    /// <summary>
    /// The channel the part's HTLC was offered on.
    /// </summary>
    public required ChannelId ChannelId { get; set; }

    /// <summary>
    /// The id of the part's HTLC on <see cref="ChannelId"/>.
    /// </summary>
    public required ulong HtlcId { get; set; }

    /// <summary>
    /// <c>PaymentPartState</c> (0 in flight, 1 succeeded, 2 failed).
    /// </summary>
    public required byte State { get; set; }

    /// <summary>
    /// The route of the part's onion, with each hop's shared secret (cascade-deleted with the part).
    /// </summary>
    public virtual ICollection<PaymentPartHopEntity>? Hops { get; set; }

    // Default constructor for EF Core
    internal PaymentPartEntity()
    {
    }
}
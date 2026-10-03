// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// One incoming HTLC of a trampoline relay (<c>TrampolineRelayPartModel</c>, NL-875), keyed by the incoming (channel,
/// HTLC id); it belongs to the <see cref="TrampolineRelayEntity"/> of its payment hash (cascade).
/// </summary>
public class TrampolineRelayPartEntity
{
    /// <summary>The channel of the incoming HTLC.</summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required ChannelId ChannelId { get; set; }

    /// <summary>The id of the incoming HTLC.</summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required ulong HtlcId { get; set; }

    /// <summary>The relay's payment hash.</summary>
    public required Hash PaymentHash { get; set; }

    /// <summary>The HTLC's amount, in millisatoshi.</summary>
    public required long AmountMsat { get; set; }

    /// <summary>The HTLC's <c>cltv_expiry</c>.</summary>
    public required uint CltvExpiry { get; set; }

    /// <summary>The 32-byte shared secret of the outer onion.</summary>
    public required byte[] OuterSharedSecret { get; set; }

    /// <summary>The 32-byte shared secret of the trampoline onion.</summary>
    public required byte[] TrampolineSharedSecret { get; set; }

    /// <summary>The outer onion's <c>payment_secret</c>, when it had one.</summary>
    public byte[]? OuterPaymentSecret { get; set; }

    // Default constructor for EF Core
    internal TrampolineRelayPartEntity()
    {
    }
}
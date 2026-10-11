using NBitcoin;

namespace NLightning.Application.Payments.Routing;

using Bolt11.Models;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Models;
using Domain.Money;

/// <summary>
/// What a payment must reach: the payee, the invoice secrets and the route hints (from a decoded BOLT 11 invoice).
/// </summary>
/// <param name="PayeeNodeId">The final node (<c>n</c>, or the key recovered from the invoice signature).</param>
/// <param name="PaymentHash">BOLT 11 <c>p</c>: the HTLC's <c>payment_hash</c>.</param>
/// <param name="PaymentSecret">BOLT 11 <c>s</c>: the final payload's <c>payment_data.payment_secret</c>.</param>
/// <param name="Amount">The invoice amount, or null for an invoice without one.</param>
/// <param name="MinFinalCltvExpiryDelta">BOLT 11 <c>c</c> (18 when absent).</param>
/// <param name="RouteHints">BOLT 11 <c>r</c> fields in invoice order, each an ordered list of hops from a node we may
/// reach to the payee.</param>
/// <param name="PaymentMetadata">BOLT 11 <c>m</c>, sent as the final payload's <c>payment_metadata</c>.</param>
/// <param name="SupportsMpp">The invoice's features set <c>basic_mpp</c> (bit 16 or 17): the payee accepts the payment
/// in several HTLCs (BOLT 4 "Basic Multi-Part Payments"; the payer MUST NOT split otherwise).</param>
public sealed record PaymentTarget(
    CompactPubKey PayeeNodeId,
    Hash PaymentHash,
    Secret PaymentSecret,
    LightningMoney? Amount,
    ushort MinFinalCltvExpiryDelta,
    IReadOnlyList<IReadOnlyList<RoutingInfo>> RouteHints,
    ReadOnlyMemory<byte>? PaymentMetadata = null,
    bool SupportsMpp = false)
{
    /// <summary>
    /// Builds the target from a decoded (and therefore validated) BOLT 11 invoice.
    /// </summary>
    /// <exception cref="ArgumentException">If the invoice has no payee key, payment hash or payment secret.</exception>
    public static PaymentTarget FromInvoice(Invoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var payee = invoice.PayeePubKey
                 ?? throw new ArgumentException("The invoice has no payee key.", nameof(invoice));
        var paymentHash = invoice.PaymentHash
                       ?? throw new ArgumentException("The invoice has no payment hash.", nameof(invoice));
        var paymentSecret = invoice.PaymentSecret
                         ?? throw new ArgumentException("The invoice has no payment secret.", nameof(invoice));

        var routeHints = invoice.RouteHints
                                .Select(IReadOnlyList<RoutingInfo> (hint) => hint.ToList())
                                .ToList();

        return new PaymentTarget(new CompactPubKey(payee.ToBytes()), ToWireBytes(paymentHash),
                                 ToWireBytes(paymentSecret), invoice.Amount.IsZero ? null : invoice.Amount,
                                 invoice.MinFinalCltvExpiry, routeHints,
                                 invoice.Metadata is { Length: > 0 } metadata ? metadata : null,
                                 invoice.Features?.IsFeatureSet(Feature.BasicMpp) ?? false);
    }

    // Bolt11 keeps 32-byte hashes as uint256 whose ToString() is the wire (and LND) hex
    internal static byte[] ToWireBytes(uint256 value) => Convert.FromHexString(value.ToString());

    internal static uint256 FromWireBytes(ReadOnlySpan<byte> value) => new(Convert.ToHexString(value));
}
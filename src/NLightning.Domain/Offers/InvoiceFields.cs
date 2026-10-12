namespace NLightning.Domain.Offers;

using Constants;
using Crypto.ValueObjects;
using Encoding;
using Models;
using Protocol.Onion.Models;
using Protocol.OnionMessages;

/// <summary>
/// The typed invoice fields (types 160-176) of an invoice. Null means absent.
/// </summary>
public sealed record InvoiceFields
{
    /// <summary>
    /// <c>invoice_paths</c>.
    /// </summary>
    public IReadOnlyList<WireBlindedPath>? Paths { get; init; }

    /// <summary>
    /// <c>invoice_blindedpay</c>, one per path in order.
    /// </summary>
    public IReadOnlyList<BlindedPayInfo>? BlindedPay { get; init; }

    /// <summary>
    /// <c>invoice_created_at</c>, seconds since the epoch.
    /// </summary>
    public ulong? CreatedAt { get; init; }

    /// <summary>
    /// <c>invoice_relative_expiry</c> in seconds; absent means
    /// <see cref="Bolt12Constants.DefaultInvoiceRelativeExpirySeconds"/>.
    /// </summary>
    public uint? RelativeExpiry { get; init; }

    /// <summary>
    /// <c>invoice_payment_hash</c>.
    /// </summary>
    public Hash? PaymentHash { get; init; }

    /// <summary>
    /// <c>invoice_amount</c> in msat.
    /// </summary>
    public ulong? Amount { get; init; }

    /// <summary>
    /// <c>invoice_fallbacks</c>, every entry as sent (see <see cref="FallbackAddress.IsUsable"/>).
    /// </summary>
    public IReadOnlyList<FallbackAddress>? Fallbacks { get; init; }

    /// <summary>
    /// <c>invoice_features</c>.
    /// </summary>
    public ReadOnlyMemory<byte>? Features { get; init; }

    /// <summary>
    /// <c>invoice_node_id</c>.
    /// </summary>
    public CompactPubKey? NodeId { get; init; }

    /// <summary>
    /// The seconds after <see cref="CreatedAt"/> at which the invoice expires.
    /// </summary>
    public uint EffectiveRelativeExpiry => RelativeExpiry ?? Bolt12Constants.DefaultInvoiceRelativeExpirySeconds;

    internal static InvoiceFields Read(Bolt12TlvStream stream, string zeroHopsRequirementId)
    {
        var fields = new InvoiceFields();
        foreach (var record in stream.Records)
        {
            fields = record.Type switch
            {
                Bolt12TlvTypes.InvoicePaths => fields with
                {
                    Paths = Bolt12FieldCodec.ReadPaths(record, zeroHopsRequirementId)
                },
                Bolt12TlvTypes.InvoiceBlindedPay => fields with { BlindedPay = Bolt12FieldCodec.ReadPayInfos(record) },
                Bolt12TlvTypes.InvoiceCreatedAt => fields with { CreatedAt = Bolt12FieldCodec.ReadTu64(record) },
                Bolt12TlvTypes.InvoiceRelativeExpiry => fields with
                {
                    RelativeExpiry = Bolt12FieldCodec.ReadTu32(record)
                },
                Bolt12TlvTypes.InvoicePaymentHash => fields with
                {
                    PaymentHash = new Hash(Bolt12FieldCodec.ReadFixed(record, 32))
                },
                Bolt12TlvTypes.InvoiceAmount => fields with { Amount = Bolt12FieldCodec.ReadTu64(record) },
                Bolt12TlvTypes.InvoiceFallbacks => fields with { Fallbacks = Bolt12FieldCodec.ReadFallbacks(record) },
                Bolt12TlvTypes.InvoiceFeatures => fields with { Features = record.Value.ToArray() },
                Bolt12TlvTypes.InvoiceNodeId => fields with { NodeId = Bolt12FieldCodec.ReadPoint(record) },
                _ => fields
            };
        }

        return fields;
    }
}
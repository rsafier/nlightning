namespace NLightning.Domain.Offers;

using Constants;
using Crypto.ValueObjects;
using Encoding;
using Models;
using Protocol.OnionMessages;
using Protocol.ValueObjects;

/// <summary>
/// The typed invoice_request fields (types 0 and 80-91) of an invoice_request or invoice. Null means absent.
/// </summary>
public sealed record InvoiceRequestFields
{
    /// <summary>
    /// <c>invreq_metadata</c>.
    /// </summary>
    public ReadOnlyMemory<byte>? Metadata { get; init; }

    /// <summary>
    /// <c>invreq_chain</c>.
    /// </summary>
    public ChainHash? Chain { get; init; }

    /// <summary>
    /// <c>invreq_amount</c> in msat.
    /// </summary>
    public ulong? Amount { get; init; }

    /// <summary>
    /// <c>invreq_features</c>.
    /// </summary>
    public ReadOnlyMemory<byte>? Features { get; init; }

    /// <summary>
    /// <c>invreq_quantity</c>.
    /// </summary>
    public ulong? Quantity { get; init; }

    /// <summary>
    /// <c>invreq_payer_id</c>.
    /// </summary>
    public CompactPubKey? PayerId { get; init; }

    /// <summary>
    /// <c>invreq_payer_note</c>.
    /// </summary>
    public string? PayerNote { get; init; }

    /// <summary>
    /// <c>invreq_paths</c>.
    /// </summary>
    public IReadOnlyList<WireBlindedPath>? Paths { get; init; }

    /// <summary>
    /// <c>invreq_bip_353_name</c>.
    /// </summary>
    public Bip353Name? Bip353Name { get; init; }

    internal static InvoiceRequestFields Read(Bolt12TlvStream stream, string zeroHopsRequirementId)
    {
        var fields = new InvoiceRequestFields();
        foreach (var record in stream.Records)
        {
            fields = record.Type switch
            {
                Bolt12TlvTypes.InvreqMetadata => fields with { Metadata = record.Value.ToArray() },
                Bolt12TlvTypes.InvreqChain => fields with { Chain = Bolt12FieldCodec.ReadFixed(record, 32) },
                Bolt12TlvTypes.InvreqAmount => fields with { Amount = Bolt12FieldCodec.ReadTu64(record) },
                Bolt12TlvTypes.InvreqFeatures => fields with { Features = record.Value.ToArray() },
                Bolt12TlvTypes.InvreqQuantity => fields with { Quantity = Bolt12FieldCodec.ReadTu64(record) },
                Bolt12TlvTypes.InvreqPayerId => fields with { PayerId = Bolt12FieldCodec.ReadPoint(record) },
                Bolt12TlvTypes.InvreqPayerNote => fields with { PayerNote = Bolt12FieldCodec.ReadUtf8(record) },
                Bolt12TlvTypes.InvreqPaths => fields with
                {
                    Paths = Bolt12FieldCodec.ReadPaths(record, zeroHopsRequirementId)
                },
                Bolt12TlvTypes.InvreqBip353Name => fields with { Bip353Name = Bolt12FieldCodec.ReadBip353Name(record) },
                _ => fields
            };
        }

        return fields;
    }
}
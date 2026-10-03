using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Offers;

using Constants;
using Encoding;
using Validators;

/// <summary>
/// A BOLT 12 invoice_request (onion message payload 64, or an <c>lnr1...</c> string): its raw TLV stream and typed
/// fields.
/// </summary>
/// <remarks>
/// Parsing enforces the format: BOLT 1 stream rules, the ranges 0-159 and 1,000,000,000-2,999,999,999 plus the
/// signature elements 240-1000 (B12-ENC-04), no unknown even type (B12-ENC-03), every known field's value format and a
/// 64-byte <c>signature</c>. The reader rules are <see cref="InvoiceRequestValidator"/>'s; the signature itself is
/// checked with <see cref="Interfaces.IBolt12Signer"/> over <see cref="Signing.Bolt12MerkleTree"/>'s root.
/// </remarks>
public sealed class InvoiceRequest
{
    /// <summary>
    /// The invoice_request's TLV stream, exactly as received or built (signature included).
    /// </summary>
    public Bolt12TlvStream Stream { get; }

    /// <summary>
    /// The copied offer fields.
    /// </summary>
    public OfferFields OfferFields { get; }

    /// <summary>
    /// The invoice_request's own fields.
    /// </summary>
    public InvoiceRequestFields Fields { get; }

    /// <summary>
    /// The 64-byte <c>signature</c>, or null.
    /// </summary>
    public ReadOnlyMemory<byte>? Signature { get; }

    private InvoiceRequest(Bolt12TlvStream stream, OfferFields offerFields, InvoiceRequestFields fields,
                           ReadOnlyMemory<byte>? signature)
    {
        Stream = stream;
        OfferFields = offerFields;
        Fields = fields;
        Signature = signature;
    }

    /// <summary>
    /// The offer fields exactly as sent (types 1-79 and 1,000,000,000-1,999,999,999), to compare with an offer's
    /// bytes (B12-IRQ-03).
    /// </summary>
    public Bolt12TlvStream GetOfferStream() => Stream.Filter(Bolt12TlvRanges.IsOfferField);

    /// <summary>
    /// The <c>lnr1...</c> string.
    /// </summary>
    public string ToBolt12String(bool uppercase = false) =>
        Bolt12Bech32.Encode(Bolt12Constants.InvoiceRequestHrp, Stream.Encode(), uppercase);

    /// <summary>
    /// Reads an invoice_request from its TLV stream.
    /// </summary>
    public static bool TryParse(Bolt12TlvStream stream, [NotNullWhen(true)] out InvoiceRequest? invoiceRequest,
                                [NotNullWhen(false)] out Bolt12Violation? violation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        invoiceRequest = null;
        try
        {
            Bolt12TlvRanges.CheckTypes(stream,
                                       t => Bolt12TlvRanges.IsInvoiceRequestField(t)
                                         || Bolt12TlvRanges.IsSignatureField(t),
                                       Bolt12TlvRanges.InvoiceRequestTypes, "invoice_request");
            invoiceRequest = new InvoiceRequest(stream,
                                                OfferFields.Read(stream, Bolt12RequirementIds.InvoiceRequestReader),
                                                InvoiceRequestFields.Read(stream,
                                                                          Bolt12RequirementIds.InvoiceRequestReader),
                                                ReadSignature(stream));
            violation = null;
            return true;
        }
        catch (Bolt12FormatException e)
        {
            violation = e.Violation;
            return false;
        }
    }

    /// <summary>
    /// Reads an invoice_request from its TLV bytes (an onion message payload).
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, [NotNullWhen(true)] out InvoiceRequest? invoiceRequest,
                                [NotNullWhen(false)] out Bolt12Violation? violation)
    {
        invoiceRequest = null;
        if (!Bolt12TlvStream.TryParse(bytes, out var stream, out var reason))
        {
            violation = new Bolt12Violation(Bolt12RequirementIds.TlvStream, reason);
            return false;
        }

        return TryParse(stream, out invoiceRequest, out violation);
    }

    /// <summary>
    /// Reads an <c>lnr1...</c> string.
    /// </summary>
    public static bool TryParse(string? bolt12, [NotNullWhen(true)] out InvoiceRequest? invoiceRequest,
                                [NotNullWhen(false)] out Bolt12Violation? violation)
    {
        invoiceRequest = null;
        if (!Bolt12String.TryDecode(bolt12, [Bolt12Constants.InvoiceRequestHrp], out var data, out violation))
            return false;

        return TryParse(data, out invoiceRequest, out violation);
    }

    /// <summary>
    /// Reads an invoice_request's TLV stream.
    /// </summary>
    /// <exception cref="FormatException">The stream is not a well-formed invoice_request.</exception>
    public static InvoiceRequest Parse(Bolt12TlvStream stream) =>
        TryParse(stream, out var invoiceRequest, out var violation)
            ? invoiceRequest
            : throw new FormatException(violation.ToString());

    /// <summary>
    /// Reads an <c>lnr1...</c> string.
    /// </summary>
    /// <exception cref="FormatException">The string is not a well-formed invoice_request.</exception>
    public static InvoiceRequest Parse(string bolt12) =>
        TryParse(bolt12, out var invoiceRequest, out var violation)
            ? invoiceRequest
            : throw new FormatException(violation.ToString());

    internal static ReadOnlyMemory<byte>? ReadSignature(Bolt12TlvStream stream)
    {
        foreach (var record in stream.Records)
            if (record.Type == Bolt12TlvTypes.Signature)
                return Bolt12FieldCodec.ReadFixed(record, Bolt12Constants.SignatureLength);

        return null;
    }
}
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Offers;

using Constants;
using Encoding;
using Validators;

/// <summary>
/// A BOLT 12 invoice (onion message payload 66): its raw TLV stream and typed fields.
/// </summary>
/// <remarks>
/// <para>Parsing enforces the format: BOLT 1 stream rules, the ranges 0-239 and 1,000,000,000-3,999,999,999 plus the
/// signature elements 240-1000 (B12-ENC-04), no unknown even type (B12-ENC-03), every known field's value format and a
/// 64-byte <c>signature</c>. The reader rules are <see cref="InvoiceValidator"/>'s.</para>
/// <para>The spec defines no string form for invoices (they travel only in onion messages); CLN's <c>lni1...</c> is
/// accepted on input (<see cref="TryParse(string?, out Bolt12Invoice?, out Bolt12Violation?)"/>) and never
/// written.</para>
/// </remarks>
public sealed class Bolt12Invoice
{
    /// <summary>
    /// The invoice's TLV stream, exactly as received or built (signature included).
    /// </summary>
    public Bolt12TlvStream Stream { get; }

    /// <summary>
    /// The copied offer fields.
    /// </summary>
    public OfferFields OfferFields { get; }

    /// <summary>
    /// The copied invoice_request fields.
    /// </summary>
    public InvoiceRequestFields InvoiceRequestFields { get; }

    /// <summary>
    /// The invoice's own fields.
    /// </summary>
    public InvoiceFields Fields { get; }

    /// <summary>
    /// The 64-byte <c>signature</c>, or null.
    /// </summary>
    public ReadOnlyMemory<byte>? Signature { get; }

    private Bolt12Invoice(Bolt12TlvStream stream, OfferFields offerFields, InvoiceRequestFields invoiceRequestFields,
                          InvoiceFields fields, ReadOnlyMemory<byte>? signature)
    {
        Stream = stream;
        OfferFields = offerFields;
        InvoiceRequestFields = invoiceRequestFields;
        Fields = fields;
        Signature = signature;
    }

    /// <summary>
    /// The fields that must exactly match the invoice_request (types 0-159 and 1,000,000,000-2,999,999,999,
    /// B12-INV-04).
    /// </summary>
    public Bolt12TlvStream GetInvoiceRequestStream() => Stream.Filter(Bolt12TlvRanges.IsInvoiceRequestField);

    /// <summary>
    /// The invoice's TLV bytes (the onion message payload).
    /// </summary>
    public byte[] Encode() => Stream.Encode();

    /// <summary>
    /// Reads an invoice from its TLV stream.
    /// </summary>
    public static bool TryParse(Bolt12TlvStream stream, [NotNullWhen(true)] out Bolt12Invoice? invoice,
                                [NotNullWhen(false)] out Bolt12Violation? violation)
    {
        ArgumentNullException.ThrowIfNull(stream);
        invoice = null;
        try
        {
            Bolt12TlvRanges.CheckTypes(stream,
                                       t => Bolt12TlvRanges.IsInvoiceField(t) || Bolt12TlvRanges.IsSignatureField(t),
                                       Bolt12TlvRanges.InvoiceTypes, "invoice");
            invoice = new Bolt12Invoice(stream,
                                        OfferFields.Read(stream, Bolt12RequirementIds.InvoiceReader),
                                        InvoiceRequestFields.Read(stream, Bolt12RequirementIds.InvoiceReader),
                                        InvoiceFields.Read(stream, Bolt12RequirementIds.InvoiceReader),
                                        InvoiceRequest.ReadSignature(stream));
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
    /// Reads an invoice from its TLV bytes (an onion message payload).
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, [NotNullWhen(true)] out Bolt12Invoice? invoice,
                                [NotNullWhen(false)] out Bolt12Violation? violation)
    {
        invoice = null;
        if (!Bolt12TlvStream.TryParse(bytes, out var stream, out var reason))
        {
            violation = new Bolt12Violation(Bolt12RequirementIds.TlvStream, reason);
            return false;
        }

        return TryParse(stream, out invoice, out violation);
    }

    /// <summary>
    /// Reads CLN's <c>lni1...</c> string (input only).
    /// </summary>
    public static bool TryParse(string? bolt12, [NotNullWhen(true)] out Bolt12Invoice? invoice,
                                [NotNullWhen(false)] out Bolt12Violation? violation)
    {
        invoice = null;
        if (!Bolt12String.TryDecode(bolt12, [Bolt12Constants.InvoiceHrp], out var data, out violation))
            return false;

        return TryParse(data, out invoice, out violation);
    }

    /// <summary>
    /// Reads an invoice's TLV stream.
    /// </summary>
    /// <exception cref="FormatException">The stream is not a well-formed invoice.</exception>
    public static Bolt12Invoice Parse(Bolt12TlvStream stream) =>
        TryParse(stream, out var invoice, out var violation)
            ? invoice
            : throw new FormatException(violation.ToString());

    /// <summary>
    /// Reads an invoice's TLV bytes.
    /// </summary>
    /// <exception cref="FormatException">The bytes are not a well-formed invoice.</exception>
    public static Bolt12Invoice Parse(ReadOnlyMemory<byte> bytes) =>
        TryParse(bytes, out var invoice, out var violation)
            ? invoice
            : throw new FormatException(violation.ToString());
}
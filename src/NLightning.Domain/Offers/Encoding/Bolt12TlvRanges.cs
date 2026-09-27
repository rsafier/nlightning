namespace NLightning.Domain.Offers.Encoding;

using Constants;
using Validators;

/// <summary>
/// The BOLT 12 TLV ranges of each message (<c>12-offer-encoding.md</c> writer/reader rules) and the known types of
/// each stream.
/// </summary>
public static class Bolt12TlvRanges
{
    private const ulong ExperimentalStart = 1_000_000_000;

    /// <summary>
    /// An offer field: 1 to 79 or 1,000,000,000 to 1,999,999,999 (also the offer part of an invoice_request or
    /// invoice).
    /// </summary>
    public static bool IsOfferField(ulong type) => type is >= 1 and <= 79 or >= ExperimentalStart and <= 1_999_999_999;

    /// <summary>
    /// A non-signature invoice_request field: 0 to 159 or 1,000,000,000 to 2,999,999,999 (also the part of an
    /// invoice that must exactly match the request, B12-INV-04).
    /// </summary>
    public static bool IsInvoiceRequestField(ulong type) =>
        type is <= 159 or >= ExperimentalStart and <= 2_999_999_999;

    /// <summary>
    /// A non-signature invoice field: 0 to 239 or 1,000,000,000 to 3,999,999,999.
    /// </summary>
    public static bool IsInvoiceField(ulong type) => type is <= 239 or >= ExperimentalStart and <= 3_999_999_999;

    /// <summary>
    /// A signature element: 240 to 1000 inclusive (BOLT 12 "Signature Calculation"); left out of the Merkle tree.
    /// </summary>
    public static bool IsSignatureField(ulong type) =>
        type is >= Bolt12Constants.SignatureRangeStart and <= Bolt12Constants.SignatureRangeEnd;

    internal static readonly HashSet<ulong> OfferTypes =
    [
        Bolt12TlvTypes.OfferChains, Bolt12TlvTypes.OfferMetadata, Bolt12TlvTypes.OfferCurrency,
        Bolt12TlvTypes.OfferAmount, Bolt12TlvTypes.OfferDescription, Bolt12TlvTypes.OfferFeatures,
        Bolt12TlvTypes.OfferAbsoluteExpiry, Bolt12TlvTypes.OfferPaths, Bolt12TlvTypes.OfferIssuer,
        Bolt12TlvTypes.OfferQuantityMax, Bolt12TlvTypes.OfferIssuerId
    ];

    internal static readonly HashSet<ulong> InvoiceRequestTypes =
    [
        .. OfferTypes,
        Bolt12TlvTypes.InvreqMetadata, Bolt12TlvTypes.InvreqChain, Bolt12TlvTypes.InvreqAmount,
        Bolt12TlvTypes.InvreqFeatures, Bolt12TlvTypes.InvreqQuantity, Bolt12TlvTypes.InvreqPayerId,
        Bolt12TlvTypes.InvreqPayerNote, Bolt12TlvTypes.InvreqPaths, Bolt12TlvTypes.InvreqBip353Name,
        Bolt12TlvTypes.Signature
    ];

    internal static readonly HashSet<ulong> InvoiceTypes =
    [
        .. InvoiceRequestTypes,
        Bolt12TlvTypes.InvoicePaths, Bolt12TlvTypes.InvoiceBlindedPay, Bolt12TlvTypes.InvoiceCreatedAt,
        Bolt12TlvTypes.InvoiceRelativeExpiry, Bolt12TlvTypes.InvoicePaymentHash, Bolt12TlvTypes.InvoiceAmount,
        Bolt12TlvTypes.InvoiceFallbacks, Bolt12TlvTypes.InvoiceFeatures, Bolt12TlvTypes.InvoiceNodeId
    ];

    internal static readonly HashSet<ulong> InvoiceErrorTypes =
        [Bolt12TlvTypes.ErroneousField, Bolt12TlvTypes.SuggestedValue, Bolt12TlvTypes.Error];

    /// <summary>
    /// Range first (B12-ENC-04), then unknown even types (B12-ENC-03); unknown odd types in range are kept.
    /// </summary>
    internal static void CheckTypes(Bolt12TlvStream stream, Func<ulong, bool> inRange, HashSet<ulong> knownTypes,
                                    string messageName)
    {
        foreach (var record in stream.Records)
        {
            if (!inRange(record.Type))
                throw new Bolt12FormatException(new Bolt12Violation(
                                                    Bolt12RequirementIds.TlvRange,
                                                    $"TLV {record.Type} is outside the {messageName} ranges.",
                                                    record.Type));

            if (record.Type % 2 == 0 && !knownTypes.Contains(record.Type))
                throw new Bolt12FormatException(new Bolt12Violation(
                                                    Bolt12RequirementIds.TlvStream,
                                                    $"TLV {record.Type} is an unknown even type in an {messageName}.",
                                                    record.Type));
        }
    }
}
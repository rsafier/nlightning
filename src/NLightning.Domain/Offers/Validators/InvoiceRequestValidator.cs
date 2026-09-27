namespace NLightning.Domain.Offers.Validators;

using Constants;
using Encoding;
using Protocol.Constants;
using Protocol.ValueObjects;

/// <summary>
/// The BOLT 12 invoice_request reader's rules that need neither our offer store nor the signature check
/// (B12-IRQ-02, B12-IRQ-04, B12-SIG-03), on a request that already parsed.
/// </summary>
/// <remarks>
/// Not here: "the offer fields exactly match a valid, unexpired offer" and the arrival-path rules (B12-IRQ-03, the
/// handler's, against our offer store), and the signature (<see cref="Interfaces.IBolt12Signer.Verify"/> over
/// <see cref="Signing.Bolt12MerkleTree.ComputeRoot"/>, by <c>invreq_payer_id</c>). The amount check converts an
/// <c>offer_currency</c> amount only when a converter is given; without one, a currency offer's expected amount is
/// not checked here.
/// </remarks>
public static class InvoiceRequestValidator
{
    /// <summary>
    /// The first rule <paramref name="invoiceRequest"/> breaks, or null.
    /// </summary>
    /// <param name="invoiceRequest">The request.</param>
    /// <param name="supportedChains">The chains we accept, or null to skip the chain check (no <c>invreq_chain</c>
    /// means bitcoin mainnet).</param>
    /// <param name="convertToMsat">Converts an <c>offer_currency</c> amount (currency, minor units) to msat, or
    /// returns null when it cannot.</param>
    public static Bolt12Violation? Validate(InvoiceRequest invoiceRequest,
                                            IReadOnlyCollection<ChainHash>? supportedChains = null,
                                            Func<string, ulong, ulong?>? convertToMsat = null)
    {
        ArgumentNullException.ThrowIfNull(invoiceRequest);
        var offer = invoiceRequest.OfferFields;
        var request = invoiceRequest.Fields;

        if (request.PayerId is null)
            return Reader("invreq_payer_id is missing.", Bolt12TlvTypes.InvreqPayerId);

        if (request.Metadata is null)
            return Reader("invreq_metadata is missing.", Bolt12TlvTypes.InvreqMetadata);

        if (request.Features is { } features && Bolt12FieldCodec.FindUnknownEvenBit(features.Span) is { } bit)
            return Reader($"invreq_features sets the unknown even bit {bit}.", Bolt12TlvTypes.InvreqFeatures);

        // BOLT 12: a response to an offer is signed with invreq_payer_id; an invoice_request without an offer (a
        // refund, lnr) "MUST NOT include signature" per the writer rules, so it is not required there.
        if (CheckSignatureElements(invoiceRequest.Stream, required: offer.IsOfferResponse) is { } signature)
            return signature;

        if (offer.IsOfferResponse)
        {
            if (offer.QuantityMax is { } quantityMax)
            {
                if (request.Quantity is not { } quantity)
                    return Amounts("offer_quantity_max is set but invreq_quantity is missing.",
                                   Bolt12TlvTypes.InvreqQuantity);
                if (quantityMax != 0 && (quantity == 0 || quantity > quantityMax))
                    return Amounts($"invreq_quantity {quantity} is outside 1..{quantityMax}.",
                                   Bolt12TlvTypes.InvreqQuantity);
            }
            else if (request.Quantity is not null)
            {
                return Amounts("invreq_quantity is set but the offer has no offer_quantity_max.",
                               Bolt12TlvTypes.InvreqQuantity);
            }

            if (offer.Amount is { } offerAmount)
            {
                if (request.Amount is { } requestAmount
                 && GetExpectedAmountMsat(offerAmount, offer.Currency, request.Quantity, convertToMsat) is { } expected
                 && requestAmount < expected)
                    return Amounts($"invreq_amount {requestAmount} is below the expected {expected} msat.",
                                   Bolt12TlvTypes.InvreqAmount);
            }
            else if (request.Amount is null)
            {
                return Amounts("Neither offer_amount nor invreq_amount is set.", Bolt12TlvTypes.InvreqAmount);
            }
        }
        else
        {
            if (offer.Chains is not null)
                return Amounts("offer_chains is set in an invoice_request without an offer.",
                               Bolt12TlvTypes.OfferChains);
            if (offer.Features is not null)
                return Amounts("offer_features is set in an invoice_request without an offer.",
                               Bolt12TlvTypes.OfferFeatures);
            if (offer.QuantityMax is not null)
                return Amounts("offer_quantity_max is set in an invoice_request without an offer.",
                               Bolt12TlvTypes.OfferQuantityMax);
            if (request.Amount is null)
                return Amounts("invreq_amount is missing in an invoice_request without an offer.",
                               Bolt12TlvTypes.InvreqAmount);
        }

        if (supportedChains is not null && !supportedChains.Contains(request.Chain ?? ChainConstants.Main))
            return Amounts("The invoice_request's chain is not supported.", Bolt12TlvTypes.InvreqChain);

        if (request.Bip353Name is { HasValidCharacters: false })
            return Amounts("invreq_bip_353_name has a character outside 0-9, a-z, A-Z, '-', '_' and '.'.",
                           Bolt12TlvTypes.InvreqBip353Name);

        return null;
    }

    /// <summary>
    /// The expected amount of a request for an offer (BOLT 12: <c>offer_amount</c>, converted from
    /// <c>offer_currency</c>, times <c>invreq_quantity</c>), or null when it cannot be computed (a currency without a
    /// converter, or an overflow).
    /// </summary>
    public static ulong? GetExpectedAmountMsat(ulong offerAmount, string? currency, ulong? quantity,
                                               Func<string, ulong, ulong?>? convertToMsat = null)
    {
        var perItem = currency is null ? offerAmount : convertToMsat?.Invoke(currency, offerAmount);
        if (perItem is not { } amount)
            return null;

        var total = (UInt128)amount * (quantity ?? 1);
        return total > ulong.MaxValue ? null : (ulong)total;
    }

    /// <summary>
    /// B12-SIG-03: a <c>signature</c> when <paramref name="required"/>, and no other signature element.
    /// </summary>
    internal static Bolt12Violation? CheckSignatureElements(Bolt12TlvStream stream, bool required)
    {
        if (required && !stream.Contains(Bolt12TlvTypes.Signature))
            return new Bolt12Violation(Bolt12RequirementIds.Signature, "signature is missing.",
                                       Bolt12TlvTypes.Signature);

        foreach (var record in stream.Records)
            if (Bolt12TlvRanges.IsSignatureField(record.Type) && record.Type != Bolt12TlvTypes.Signature)
                return new Bolt12Violation(Bolt12RequirementIds.Signature,
                                           $"Signature element {record.Type} besides signature.", record.Type);

        return null;
    }

    private static Bolt12Violation Reader(string reason, ulong field) =>
        new(Bolt12RequirementIds.InvoiceRequestReader, reason, field);

    private static Bolt12Violation Amounts(string reason, ulong field) =>
        new(Bolt12RequirementIds.InvoiceRequestAmounts, reason, field);
}
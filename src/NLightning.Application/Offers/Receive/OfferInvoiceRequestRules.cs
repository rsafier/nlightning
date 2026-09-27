namespace NLightning.Application.Offers.Receive;

using Domain.Offers.Constants;
using Domain.Offers.Models;
using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Why an invoice_request for one of our offers is refused after its signature verified: the <c>invoice_error</c> we
/// answer with (BOLT 12 "Invoice Errors": <c>error</c>, and <c>erroneous_field</c> when one field is at fault).
/// </summary>
/// <param name="Error">The explanatory string.</param>
/// <param name="ErroneousField">The invoice_request field at fault, or null.</param>
public sealed record InvoiceRequestRefusal(string Error, ulong? ErroneousField = null);

/// <summary>
/// The BOLT 12 "Invoice Requests" reader rules that compare a request with the offer it copies (B12-IRQ-04: chain,
/// quantity, amount, <c>invreq_bip_353_name</c>) and the invoice amount that follows (BOLT 12 "Invoices" writer:
/// <c>invreq_amount</c>, else the expected amount). Pure.
/// </summary>
public static class OfferInvoiceRequestRules
{
    /// <summary>
    /// Checks <paramref name="request"/> against <paramref name="offer"/> for a node on <paramref name="ourChain"/>.
    /// </summary>
    /// <param name="request">The read request.</param>
    /// <param name="offer">The offer whose fields the request copied.</param>
    /// <param name="ourChain">Our network's chain hash.</param>
    /// <param name="invoiceAmountMsat">The invoice amount, when the request passes.</param>
    /// <returns>Null when it passes, else the refusal.</returns>
    public static InvoiceRequestRefusal? Check(ReadInvoiceRequest request, OfferModel offer, ChainHash ourChain,
                                               out ulong invoiceAmountMsat)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(offer);
        invoiceAmountMsat = 0;

        // BOLT 12: without invreq_chain the request is for bitcoin; either way the chain must be one we support
        var chain = request.Chain ?? ChainConstants.Main;
        if (chain != ourChain)
            return new InvoiceRequestRefusal("Unsupported chain", Bolt12TlvTypes.InvreqChain);

        if (offer.QuantityMax is { } quantityMax)
        {
            if (request.Quantity is not { } quantity)
                return new InvoiceRequestRefusal("invreq_quantity is required", Bolt12TlvTypes.InvreqQuantity);

            // BOLT 12 writer: MUST set invreq_quantity to greater than zero; with a maximum, at most it
            if (quantity == 0 || (quantityMax != 0 && quantity > quantityMax))
                return new InvoiceRequestRefusal("invreq_quantity is out of range", Bolt12TlvTypes.InvreqQuantity);
        }
        else if (request.Quantity is not null)
        {
            return new InvoiceRequestRefusal("invreq_quantity is not allowed", Bolt12TlvTypes.InvreqQuantity);
        }

        if (offer.Amount is { } offerAmount)
        {
            // We never create offers in another currency, so the expected amount is offer_amount x quantity in msat
            var expected = (UInt128)offerAmount.MilliSatoshi * (request.Quantity ?? 1);
            if (expected > ulong.MaxValue)
                return new InvoiceRequestRefusal("Amount too large", Bolt12TlvTypes.InvreqQuantity);

            if (request.Amount is { } requested)
            {
                if (requested < expected)
                    return new InvoiceRequestRefusal("invreq_amount is below the offer amount",
                                                     Bolt12TlvTypes.InvreqAmount);

                invoiceAmountMsat = requested;
            }
            else
            {
                invoiceAmountMsat = (ulong)expected;
            }
        }
        else
        {
            if (request.Amount is not { } requested)
                return new InvoiceRequestRefusal("invreq_amount is required", Bolt12TlvTypes.InvreqAmount);

            invoiceAmountMsat = requested;
        }

        if (invoiceAmountMsat == 0)
            return new InvoiceRequestRefusal("The amount must be positive", Bolt12TlvTypes.InvreqAmount);

        if (!request.Bip353NameIsValid)
            return new InvoiceRequestRefusal("invreq_bip_353_name has invalid characters",
                                             Bolt12TlvTypes.InvreqBip353Name);

        return null;
    }
}
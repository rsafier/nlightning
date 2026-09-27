using System.Security.Cryptography;
using System.Text;

namespace NLightning.Application.Offers.Send;

using Domain.Crypto.ValueObjects;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Protocol.Constants;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Builds the invoice_request for an offer (BOLT 12 "Invoice Requests" writer responding to an offer, B12-IRQ-01;
/// BOLT 12 plan B4-T2).
/// </summary>
/// <remarks>
/// <para>Every offer record is copied as it was encoded, unknown ones included; then 32 random bytes of
/// <c>invreq_metadata</c> (unpredictable, plan D3), <c>invreq_chain</c> only for a chain other than bitcoin,
/// <c>invreq_amount</c> when the caller gives one (required without <c>offer_amount</c> and for another
/// <c>offer_currency</c>, which we never convert), <c>invreq_quantity</c> exactly when the offer has
/// <c>offer_quantity_max</c>, the transient <c>invreq_payer_id</c> derived from the metadata
/// (<see cref="IBolt12Signer.DerivePayerId"/>) and the note; signed with that key over the Merkle root.</para>
/// <para>Stateless.</para>
/// </remarks>
public static class InvoiceRequestFactory
{
    /// <summary>
    /// The request for <paramref name="offer"/>.
    /// </summary>
    /// <param name="offer">The checked offer.</param>
    /// <param name="request">What to ask for.</param>
    /// <param name="chain">Our chain.</param>
    /// <param name="signer">Derives the payer key and signs.</param>
    /// <param name="metadata">The <c>invreq_metadata</c> (tests); null draws 32 bytes from the CSPRNG.</param>
    /// <exception cref="ArgumentException">The amount or quantity breaks a BOLT 12 rule. Nothing is signed.</exception>
    public static BuiltInvoiceRequest Create(OfferToPay offer, PayOfferRequest request, ChainHash chain,
                                             IBolt12Signer signer, byte[]? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(signer);

        var quantity = CheckQuantity(offer, request.Quantity);
        var expected = GetExpectedAmount(offer, request, quantity);
        if (request.PayerNote is { } note && note.Length == 0)
            throw new ArgumentException("B12-IRQ-01: an empty payer note; leave it out instead.", nameof(request));

        metadata ??= RandomNumberGenerator.GetBytes(Bolt12Constants.OurInvoiceRequestMetadataLength);
        var records = new List<Bolt12TlvRecord>(offer.Stream.Records)
        {
            new(Bolt12TlvTypes.InvreqMetadata, metadata)
        };
        if (offer.Chains is not null && chain != ChainConstants.Main)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvreqChain, (byte[])chain));
        if (request.Amount is { } amount)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvreqAmount,
                                            TruncatedInt.EncodeTu64(amount.MilliSatoshi)));
        if (quantity is { } q)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvreqQuantity, TruncatedInt.EncodeTu64(q)));

        var payerId = signer.DerivePayerId(metadata);
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvreqPayerId, (byte[])payerId));
        if (request.PayerNote is { } payerNote)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvreqPayerNote, Encoding.UTF8.GetBytes(payerNote)));

        var unsigned = new Bolt12TlvStream(records.OrderBy(r => r.Type).ToList());
        var signature = signer.SignAsPayer(metadata, Bolt12Constants.InvoiceRequestSignatureTag,
                                           Bolt12Wire.MerkleRoot(unsigned));
        var stream = new Bolt12TlvStream(unsigned.Records
                                                 .Append(new Bolt12TlvRecord(Bolt12TlvTypes.Signature, signature))
                                                 .OrderBy(r => r.Type)
                                                 .ToList());
        return new BuiltInvoiceRequest(stream, Bolt12Wire.Encode(stream), metadata, payerId, expected,
                                       request.Amount is not null, quantity);
    }

    /// <summary>
    /// B12-IRQ-01 quantity rules: required (above 0, at most a non-zero <c>offer_quantity_max</c>) when the offer has
    /// <c>offer_quantity_max</c>, forbidden otherwise.
    /// </summary>
    private static ulong? CheckQuantity(OfferToPay offer, ulong? quantity)
    {
        if (offer.QuantityMax is not { } max)
        {
            if (quantity is not null)
                throw new ArgumentException("B12-IRQ-01: the offer has no offer_quantity_max, so no quantity may be "
                                          + "given.", nameof(quantity));
            return null;
        }

        if (quantity is not { } q || q == 0)
            throw new ArgumentException("B12-IRQ-01: the offer has offer_quantity_max, so a quantity above 0 is "
                                      + "required.", nameof(quantity));
        if (max != 0 && q > max)
            throw new ArgumentException($"B12-IRQ-01: quantity {q} is above offer_quantity_max {max}.",
                                        nameof(quantity));
        return q;
    }

    /// <summary>
    /// What the invoice must ask for: <c>invreq_amount</c> when given (at least the offer's amount times the
    /// quantity for an offer in msat), else the offer's amount times the quantity.
    /// </summary>
    private static ulong GetExpectedAmount(OfferToPay offer, PayOfferRequest request, ulong? quantity)
    {
        if (request.Amount is { IsZero: true })
            throw new ArgumentException("B12-IRQ-01: the amount must be positive.", nameof(request));

        if (offer.Amount is not { } offerAmount)
            return request.Amount?.MilliSatoshi
                ?? throw new ArgumentException("B12-IRQ-01: the offer has no amount; give one.", nameof(request));

        if (offer.Currency is not null)
            return request.Amount?.MilliSatoshi
                ?? throw new ArgumentException($"The offer is priced in {offer.Currency}, which we do not convert; "
                                             + "give the amount in msat.", nameof(request));

        ulong minimum;
        try
        {
            minimum = checked(offerAmount * (quantity ?? 1));
        }
        catch (OverflowException e)
        {
            throw new ArgumentException("B12-IRQ-01: offer_amount times the quantity overflows.", nameof(request), e);
        }

        if (request.Amount is { } amount && amount.MilliSatoshi < minimum)
            throw new ArgumentException($"B12-IRQ-01: {amount.MilliSatoshi} msat is below the offer's {minimum} msat.",
                                        nameof(request));
        return request.Amount?.MilliSatoshi ?? minimum;
    }
}

/// <summary>
/// A signed invoice_request we built.
/// </summary>
/// <param name="Stream">Its records.</param>
/// <param name="Bytes">Its wire bytes (the onion message's field 64).</param>
/// <param name="Metadata">Its <c>invreq_metadata</c> (the payer key is derived from it).</param>
/// <param name="PayerId">Its <c>invreq_payer_id</c>.</param>
/// <param name="ExpectedAmountMsat">What the invoice must ask for.</param>
/// <param name="HasAmount">Whether it sets <c>invreq_amount</c>.</param>
/// <param name="Quantity">Its <c>invreq_quantity</c>, or null.</param>
public sealed record BuiltInvoiceRequest(Bolt12TlvStream Stream, byte[] Bytes, byte[] Metadata, CompactPubKey PayerId,
                                         ulong ExpectedAmountMsat, bool HasAmount, ulong? Quantity);
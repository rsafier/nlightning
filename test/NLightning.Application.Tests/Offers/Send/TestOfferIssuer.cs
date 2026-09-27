using System.Text;

namespace NLightning.Application.Tests.Offers.Send;

using Application.Offers.Send;
using Domain.Crypto.ValueObjects;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Writes offers and answers invoice_requests like an offer's issuer (BOLT 12 "Offers" and "Invoices" writers), for
/// the payer tests. Stand-in for lane B12-D's receive side until the lanes are integrated.
/// </summary>
internal sealed class TestOfferIssuer
{
    public TestOfferIssuer(byte[]? nodeKey = null)
    {
        Signer = new TestBolt12Signer(nodeKey ?? Enumerable.Repeat((byte)0x41, 32).ToArray());
    }

    public TestBolt12Signer Signer { get; }

    public CompactPubKey NodeId => Signer.NodeId;

    /// <summary>
    /// An offer string. <paramref name="extra"/> records are added as they are (unknown or invalid ones included).
    /// </summary>
    public string CreateOffer(ulong? amountMsat = 10_000, string? description = "nltg test offer",
                              ChainHash? chain = null, bool withIssuerId = true,
                              IReadOnlyList<WireBlindedPath>? paths = null, ulong? quantityMax = null,
                              ulong? absoluteExpiry = null, string? currency = null,
                              IEnumerable<Bolt12TlvRecord>? extra = null)
    {
        var records = new List<Bolt12TlvRecord>();
        if (chain is { } chainHash)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferChains, (byte[])chainHash));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferMetadata, new byte[] { 0xAA, 0xBB }));
        if (currency is not null)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferCurrency, Encoding.UTF8.GetBytes(currency)));
        if (amountMsat is { } amount)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferAmount, TruncatedInt.EncodeTu64(amount)));
        if (description is not null)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferDescription, Encoding.UTF8.GetBytes(description)));
        if (absoluteExpiry is { } expiry)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferAbsoluteExpiry, TruncatedInt.EncodeTu64(expiry)));
        if (paths is { Count: > 0 })
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferPaths, BlindedPathCodec.EncodeList(paths)));
        if (quantityMax is { } max)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferQuantityMax, TruncatedInt.EncodeTu64(max)));
        if (withIssuerId)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.OfferIssuerId, (byte[])NodeId));
        if (extra is not null)
            records.AddRange(extra);

        var stream = new Bolt12TlvStream(records.OrderBy(r => r.Type).ToList());
        return Bolt12Wire.EncodeString(Bolt12Constants.OfferHrp, Bolt12Wire.Encode(stream));
    }

    /// <summary>
    /// The invoice answering <paramref name="invoiceRequest"/>: every non-signature record copied, then the invoice
    /// fields, signed with <paramref name="signingKey"/> (default: the node key). <paramref name="mutate"/> changes the
    /// records before the signature.
    /// </summary>
    public byte[] CreateInvoice(ReadOnlyMemory<byte> invoiceRequest, Hash paymentHash,
                                IReadOnlyList<BlindedPaymentPath> paths, ulong amountMsat, DateTimeOffset createdAt,
                                uint? relativeExpiry = null, byte[]? features = null, CompactPubKey? nodeId = null,
                                Func<List<Bolt12TlvRecord>, List<Bolt12TlvRecord>>? mutate = null,
                                byte[]? signingKey = null)
    {
        var request = Bolt12Wire.ParseStream(invoiceRequest);
        var records = request.Records.Where(r => !Bolt12Wire.IsSignatureType(r.Type)).ToList();
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoicePaths,
                                        BlindedPathCodec.EncodeList(paths.Select(p => WireBlindedPath.FromBlindedPath(p.Path))
                                                                         .ToList())));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceBlindedPay,
                                        InvoiceVerifier.WritePayInfos(paths.Select(p => p.PayInfo))));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceCreatedAt,
                                        TruncatedInt.EncodeTu64((ulong)createdAt.ToUnixTimeSeconds())));
        if (relativeExpiry is { } expiry)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceRelativeExpiry, TruncatedInt.EncodeTu32(expiry)));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoicePaymentHash, (byte[])paymentHash));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceAmount, TruncatedInt.EncodeTu64(amountMsat)));
        if (features is not null)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceFeatures, features));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceNodeId, (byte[])(nodeId ?? NodeId)));
        if (mutate is not null)
            records = mutate(records);

        var unsigned = new Bolt12TlvStream(records.OrderBy(r => r.Type).ToList());
        var signature = signingKey is null
                            ? Signer.SignAsNode(Bolt12Constants.InvoiceSignatureTag, Bolt12Wire.MerkleRoot(unsigned))
                            : TestBolt12Signer.Sign(signingKey, Bolt12Constants.InvoiceSignatureTag,
                                                    Bolt12Wire.MerkleRoot(unsigned));
        return Bolt12Wire.Encode(new Bolt12TlvStream(unsigned.Records
                                                              .Append(new Bolt12TlvRecord(Bolt12TlvTypes.Signature,
                                                                                          signature))
                                                              .OrderBy(r => r.Type)
                                                              .ToList()));
    }

    /// <summary>
    /// An <c>invoice_error</c> with <c>error</c> and, optionally, <c>erroneous_field</c>.
    /// </summary>
    public static byte[] CreateInvoiceError(string error, ulong? erroneousField = null)
    {
        var records = new List<Bolt12TlvRecord>();
        if (erroneousField is { } field)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.ErroneousField, TruncatedInt.EncodeTu64(field)));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.Error, Encoding.UTF8.GetBytes(error)));
        return Bolt12Wire.Encode(new Bolt12TlvStream(records));
    }
}
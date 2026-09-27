using System.Text;

namespace NLightning.Application.Offers.Receive;

using Domain.Crypto.ValueObjects;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.Tlv;

/// <summary>
/// Writes the invoices and invoice_errors we answer invoice_requests with (BOLT 12 "Invoices" and "Invoice Errors"
/// writers; plan B3-T3).
/// </summary>
/// <remarks>
/// <para>Invoice: every non-signature field of the request copied raw (unknown ones included), then
/// <c>invoice_paths</c> and one <c>invoice_blindedpay</c> per path in the same order (features empty: our paths set no
/// <c>allowed_features</c>), <c>invoice_created_at</c>, <c>invoice_relative_expiry</c> only when it is not 7200,
/// <c>invoice_payment_hash</c>, <c>invoice_amount</c>, <c>invoice_features</c> with MPP optional (bit 17) when we
/// accept multi-part payments, <c>invoice_node_id</c> (the offer's issuer id), and exactly one <c>signature</c> by the
/// node key over the Merkle root, tag <c>lightninginvoicesignature</c>. Records in ascending type order.</para>
/// </remarks>
public static class OfferInvoiceFactory
{
    /// <summary>BOLT 12 invoice feature bit 17: multi-part payments allowed.</summary>
    public const int MppOptionalBit = 17;

    /// <summary>
    /// The signed invoice's TLV stream.
    /// </summary>
    /// <param name="request">The invoice_request answered.</param>
    /// <param name="paths">The blinded payment paths (at least one).</param>
    /// <param name="createdAt"><c>invoice_created_at</c>.</param>
    /// <param name="relativeExpirySeconds">How long the invoice may be paid.</param>
    /// <param name="paymentHash"><c>invoice_payment_hash</c>.</param>
    /// <param name="amountMsat"><c>invoice_amount</c>.</param>
    /// <param name="allowMpp">Set MPP optional.</param>
    /// <param name="nodeId"><c>invoice_node_id</c>.</param>
    /// <param name="signer">Signs as the node.</param>
    /// <exception cref="ArgumentException">No path.</exception>
    public static byte[] CreateInvoice(ReadInvoiceRequest request, IReadOnlyList<BlindedPaymentPath> paths,
                                       DateTimeOffset createdAt, uint relativeExpirySeconds, Hash paymentHash,
                                       ulong amountMsat, bool allowMpp, CompactPubKey nodeId, IBolt12Signer signer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(signer);
        if (paths.Count == 0)
            throw new ArgumentException("An invoice needs at least one blinded path.", nameof(paths));

        var records = request.Stream.Records.Where(r => !Bolt12Wire.IsSignatureType(r.Type)).ToList();
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoicePaths,
                                        BlindedPathCodec.EncodeList(
                                            paths.Select(p => WireBlindedPath.FromBlindedPath(p.Path)).ToList())));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceBlindedPay,
                                        paths.SelectMany(p => Bolt12Wire.EncodePayInfo(p.PayInfo)).ToArray()));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceCreatedAt,
                                        TruncatedInt.EncodeTu64((ulong)createdAt.ToUnixTimeSeconds())));
        if (relativeExpirySeconds != Bolt12Constants.DefaultInvoiceRelativeExpirySeconds)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceRelativeExpiry,
                                            TruncatedInt.EncodeTu32(relativeExpirySeconds)));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoicePaymentHash, (byte[])paymentHash));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceAmount, TruncatedInt.EncodeTu64(amountMsat)));
        if (allowMpp)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceFeatures, FeatureBitmap(MppOptionalBit)));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.InvoiceNodeId, (byte[])nodeId));

        records.Sort((a, b) => a.Type.CompareTo(b.Type));
        var merkleRoot = Bolt12Wire.ComputeMerkleRoot(records);
        var signature = signer.SignAsNode(Bolt12Constants.InvoiceSignatureTag, merkleRoot);
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.Signature, signature));
        records.Sort((a, b) => a.Type.CompareTo(b.Type));
        return Bolt12Wire.Encode(records);
    }

    /// <summary>
    /// An <c>invoice_error</c> TLV stream: <c>erroneous_field</c> when given, then <c>error</c>.
    /// </summary>
    public static byte[] CreateInvoiceError(InvoiceRequestRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        var records = new List<Bolt12TlvRecord>();
        if (refusal.ErroneousField is { } field)
            records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.ErroneousField, TruncatedInt.EncodeTu64(field)));
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.Error, Encoding.UTF8.GetBytes(refusal.Error)));
        return Bolt12Wire.Encode(records);
    }

    /// <summary>
    /// A big-endian feature bitmap with only <paramref name="bit"/> set.
    /// </summary>
    public static byte[] FeatureBitmap(int bit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bit);
        var bytes = new byte[bit / 8 + 1];
        bytes[0] = (byte)(1 << (bit % 8));
        return bytes;
    }
}
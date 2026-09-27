using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Offers.Send;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Protocol.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// The payer's checks of an invoice received for one of our invoice_requests (BOLT 12 "Invoices" reader,
/// B12-INV-03/04/05; BOLT 12 plan B4-T2).
/// </summary>
/// <remarks>
/// <para>Order: the TLV stream (strict; types in 0-239, 1,000,000,000-3,999,999,999 or exactly one
/// <c>signature</c>; no unknown even type), the required fields, the chain, <c>invoice_features</c> (bit 16/17
/// <c>basic_mpp</c> is the only even bit we know), the expiry, <c>invoice_paths</c>/<c>invoice_blindedpay</c> (present,
/// one pay info per path, paths with an unknown even pay info feature or an introduction node we cannot name dropped,
/// at least one left), the exact match of fields 0-159 and 1,000,000,000-2,999,999,999 with our request,
/// <c>invoice_node_id</c> (= <c>offer_issuer_id</c>, else the final blinded node id of the offer path we sent to),
/// the amount (= <c>invreq_amount</c>, else = offer amount times quantity), and the signature. Arrival through our
/// <c>reply_path</c> is the caller's (the onion-message service only hands over replies through it). Fallbacks are
/// ignored: we pay only over the paths.</para>
/// <para>Messages start with the requirement id they break. Stateless.</para>
/// </remarks>
public static class InvoiceVerifier
{
    private const ulong MppCompulsoryBit = 16;
    private const ulong MppOptionalBit = 17;

    private static readonly ulong[] s_knownEvenTypes =
    [
        Bolt12TlvTypes.InvreqMetadata, Bolt12TlvTypes.OfferChains, Bolt12TlvTypes.OfferMetadata,
        Bolt12TlvTypes.OfferCurrency, Bolt12TlvTypes.OfferAmount, Bolt12TlvTypes.OfferDescription,
        Bolt12TlvTypes.OfferFeatures, Bolt12TlvTypes.OfferAbsoluteExpiry, Bolt12TlvTypes.OfferPaths,
        Bolt12TlvTypes.OfferIssuer, Bolt12TlvTypes.OfferQuantityMax, Bolt12TlvTypes.OfferIssuerId,
        Bolt12TlvTypes.InvreqChain, Bolt12TlvTypes.InvreqAmount, Bolt12TlvTypes.InvreqFeatures,
        Bolt12TlvTypes.InvreqQuantity, Bolt12TlvTypes.InvreqPayerId, Bolt12TlvTypes.InvreqPaths,
        Bolt12TlvTypes.InvoicePaths, Bolt12TlvTypes.InvoiceBlindedPay, Bolt12TlvTypes.InvoiceCreatedAt,
        Bolt12TlvTypes.InvoiceRelativeExpiry, Bolt12TlvTypes.InvoicePaymentHash, Bolt12TlvTypes.InvoiceAmount,
        Bolt12TlvTypes.InvoiceFallbacks, Bolt12TlvTypes.InvoiceFeatures, Bolt12TlvTypes.InvoiceNodeId,
        Bolt12TlvTypes.Signature
    ];

    /// <summary>
    /// Checks <paramref name="invoiceBytes"/> against our request.
    /// </summary>
    /// <param name="invoiceBytes">The invoice (the onion message's field 66).</param>
    /// <param name="request">The invoice_request it answers.</param>
    /// <param name="offer">The offer the request is for.</param>
    /// <param name="sentTo">The offer path we sent the request through, or null when we sent it to
    /// <c>offer_issuer_id</c>.</param>
    /// <param name="chain">Our chain.</param>
    /// <param name="now">The time the expiry is checked at.</param>
    /// <param name="signer">Verifies the signature.</param>
    /// <param name="resolveNode">Names the introduction node of an <c>invoice_paths</c> path given as a short channel
    /// id and direction (our channels, the graph), or returns null.</param>
    /// <param name="verified">The invoice and its usable paths.</param>
    /// <param name="reason">Why the invoice is rejected.</param>
    public static bool TryVerify(ReadOnlyMemory<byte> invoiceBytes, BuiltInvoiceRequest request, OfferToPay offer,
                                 WireBlindedPath? sentTo, ChainHash chain, DateTimeOffset now, IBolt12Signer signer,
                                 Func<SciddirOrPubkey, CompactPubKey?> resolveNode,
                                 [NotNullWhen(true)] out VerifiedInvoice? verified,
                                 [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(resolveNode);

        verified = null;
        Bolt12TlvStream stream;
        try
        {
            stream = Bolt12Wire.ParseStream(invoiceBytes);
        }
        catch (FormatException e)
        {
            reason = $"B12-ENC-03: the invoice is not a TLV stream ({e.Message})";
            return false;
        }

        foreach (var record in stream.Records)
        {
            if (Bolt12Wire.IsSignatureType(record.Type))
            {
                if (record.Type != Bolt12TlvTypes.Signature)
                {
                    reason = $"B12-SIG-03: signature element {record.Type} besides signature";
                    return false;
                }

                continue;
            }

            if (!IsInvoiceType(record.Type))
            {
                reason = $"B12-ENC-04: TLV type {record.Type} is outside the invoice ranges";
                return false;
            }

            if (record.Type % 2 == 0 && !s_knownEvenTypes.Contains(record.Type))
            {
                reason = $"B12-ENC-03: unknown even TLV type {record.Type}";
                return false;
            }
        }

        if (!TryReadRequired(stream, out var amountMsat, out var createdAt, out var paymentHash, out var nodeId,
                             out reason))
            return false;

        if (stream.TryGetValue(Bolt12TlvTypes.InvreqChain, out var invoiceChain)
                ? !invoiceChain.Span.SequenceEqual((byte[])chain)
                : chain != ChainConstants.Main)
        {
            reason = "B12-INV-03: the invoice is not for our chain";
            return false;
        }

        var mpp = false;
        if (stream.TryGetValue(Bolt12TlvTypes.InvoiceFeatures, out var features))
        {
            if (TryFindUnknownEvenBit(features.Span, MppCompulsoryBit, out var unknownBit))
            {
                reason = $"B12-INV-03: invoice_features sets the unknown even bit {unknownBit}";
                return false;
            }

            mpp = IsBitSet(features.Span, MppCompulsoryBit) || IsBitSet(features.Span, MppOptionalBit);
        }

        var relativeExpiry = Bolt12Constants.DefaultInvoiceRelativeExpirySeconds;
        if (stream.TryGetValue(Bolt12TlvTypes.InvoiceRelativeExpiry, out var expiryValue)
         && !TruncatedInt.TryDecodeTu32(expiryValue.Span, out relativeExpiry))
        {
            reason = "B12-ENC-03: invoice_relative_expiry is not a minimal tu32";
            return false;
        }

        if (now.ToUnixTimeSeconds() > (decimal)createdAt + relativeExpiry)
        {
            reason = "B12-INV-03: the invoice has expired";
            return false;
        }

        if (!TryReadPaths(stream, resolveNode, out var paths, out var totalPaths, out reason))
            return false;

        var ours = request.Stream.Records.Where(r => IsMirroredType(r.Type)).ToList();
        var theirs = stream.Records.Where(r => IsMirroredType(r.Type)).ToList();
        if (ours.Count != theirs.Count
         || ours.Zip(theirs).Any(p => p.First.Type != p.Second.Type || !p.First.Value.Span.SequenceEqual(p.Second.Value.Span)))
        {
            reason = "B12-INV-04: the invoice's request fields do not exactly match our invoice_request";
            return false;
        }

        var expectedNode = offer.IssuerId ?? sentTo?.Hops[^1].BlindedNodeId;
        if (expectedNode is null || nodeId != expectedNode.Value)
        {
            reason = "B12-INV-04: invoice_node_id is not the offer's issuer (or the path's final blinded node id)";
            return false;
        }

        if (amountMsat != request.ExpectedAmountMsat)
        {
            reason = request.HasAmount
                         ? $"B12-INV-04: invoice_amount {amountMsat} msat is not our invreq_amount "
                         + $"{request.ExpectedAmountMsat} msat"
                         : $"B12-INV-04: invoice_amount {amountMsat} msat is not the offer's "
                         + $"{request.ExpectedAmountMsat} msat";
            return false;
        }

        if (!stream.TryGetValue(Bolt12TlvTypes.Signature, out var signature)
         || !signer.Verify(Bolt12Constants.InvoiceSignatureTag, Bolt12Wire.MerkleRoot(stream), nodeId, signature))
        {
            reason = "B12-INV-04: the invoice's signature is missing or does not verify with invoice_node_id";
            return false;
        }

        verified = new VerifiedInvoice(
            new FetchedBolt12Invoice(invoiceBytes.ToArray(), request.Bytes, nodeId,
                                     LightningMoney.MilliSatoshis(amountMsat), paymentHash,
                                     createdAt > (ulong)DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                                         ? DateTimeOffset.MaxValue
                                         : DateTimeOffset.FromUnixTimeSeconds((long)createdAt),
                                     relativeExpiry, totalPaths),
            paths, mpp);
        reason = null;
        return true;
    }

    /// <summary>
    /// BOLT 12 invoice TLV ranges (not counting signatures): 0 to 239 and 1,000,000,000 to 3,999,999,999.
    /// </summary>
    public static bool IsInvoiceType(ulong type) => type is <= 239 or >= 1_000_000_000 and <= 3_999_999_999;

    /// <summary>
    /// The fields an invoice mirrors from the invoice_request: 0 to 159 and 1,000,000,000 to 2,999,999,999.
    /// </summary>
    public static bool IsMirroredType(ulong type) => type is <= 159 or >= 1_000_000_000 and <= 2_999_999_999;

    /// <summary>
    /// Decodes <c>invoice_blindedpay</c>: <c>blinded_payinfo</c> records back to back.
    /// </summary>
    public static bool TryReadPayInfos(ReadOnlySpan<byte> data, [NotNullWhen(true)] out List<BlindedPayInfo>? payInfos)
    {
        payInfos = [];
        var offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 28)
            {
                payInfos = null;
                return false;
            }

            var rest = data[offset..];
            var flen = BinaryPrimitives.ReadUInt16BigEndian(rest[26..]);
            if (rest.Length - 28 < flen)
            {
                payInfos = null;
                return false;
            }

            payInfos.Add(new BlindedPayInfo(BinaryPrimitives.ReadUInt32BigEndian(rest),
                                            BinaryPrimitives.ReadUInt32BigEndian(rest[4..]),
                                            BinaryPrimitives.ReadUInt16BigEndian(rest[8..]),
                                            BinaryPrimitives.ReadUInt64BigEndian(rest[10..]),
                                            BinaryPrimitives.ReadUInt64BigEndian(rest[18..]),
                                            rest.Slice(28, flen).ToArray()));
            offset += 28 + flen;
        }

        return true;
    }

    /// <summary>
    /// Encodes <c>blinded_payinfo</c> records back to back (tests and the harness's invoice writer).
    /// </summary>
    public static byte[] WritePayInfos(IEnumerable<BlindedPayInfo> payInfos)
    {
        using var output = new MemoryStream();
        Span<byte> fixedPart = stackalloc byte[28];
        foreach (var info in payInfos)
        {
            BinaryPrimitives.WriteUInt32BigEndian(fixedPart, info.FeeBaseMsat);
            BinaryPrimitives.WriteUInt32BigEndian(fixedPart[4..], info.FeeProportionalMillionths);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart[8..], info.CltvExpiryDelta);
            BinaryPrimitives.WriteUInt64BigEndian(fixedPart[10..], info.HtlcMinimumMsat);
            BinaryPrimitives.WriteUInt64BigEndian(fixedPart[18..], info.HtlcMaximumMsat);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart[26..], (ushort)info.Features.Length);
            output.Write(fixedPart);
            output.Write(info.Features.Span);
        }

        return output.ToArray();
    }

    private static bool TryReadRequired(Bolt12TlvStream stream, out ulong amountMsat, out ulong createdAt,
                                        out Hash paymentHash, out CompactPubKey nodeId,
                                        [NotNullWhen(false)] out string? reason)
    {
        amountMsat = 0;
        createdAt = 0;
        paymentHash = default;
        nodeId = default;
        if (!stream.TryGetValue(Bolt12TlvTypes.InvoiceAmount, out var amount)
         || !TruncatedInt.TryDecodeTu64(amount.Span, out amountMsat))
        {
            reason = "B12-INV-03: invoice_amount is missing or malformed";
            return false;
        }

        if (!stream.TryGetValue(Bolt12TlvTypes.InvoiceCreatedAt, out var created)
         || !TruncatedInt.TryDecodeTu64(created.Span, out createdAt))
        {
            reason = "B12-INV-03: invoice_created_at is missing or malformed";
            return false;
        }

        if (!stream.TryGetValue(Bolt12TlvTypes.InvoicePaymentHash, out var hash) || hash.Length != 32)
        {
            reason = "B12-INV-03: invoice_payment_hash is missing or malformed";
            return false;
        }

        if (!stream.TryGetValue(Bolt12TlvTypes.InvoiceNodeId, out var node) || node.Length != 33
                                                                            || node.Span[0] is not (2 or 3))
        {
            reason = "B12-INV-03: invoice_node_id is missing or not a point";
            return false;
        }

        paymentHash = new Hash(hash.ToArray());
        nodeId = new CompactPubKey(node.ToArray());
        reason = null;
        return true;
    }

    private static bool TryReadPaths(Bolt12TlvStream stream, Func<SciddirOrPubkey, CompactPubKey?> resolveNode,
                                     [NotNullWhen(true)] out List<BlindedPaymentPath>? paths, out int totalPaths,
                                     [NotNullWhen(false)] out string? reason)
    {
        paths = null;
        totalPaths = 0;
        if (!stream.TryGetValue(Bolt12TlvTypes.InvoicePaths, out var pathsValue)
         || !BlindedPathCodec.TryReadList(pathsValue.Span, out var wirePaths, out _) || wirePaths.Count == 0)
        {
            reason = "B12-INV-03: invoice_paths is missing, empty or malformed";
            return false;
        }

        if (!stream.TryGetValue(Bolt12TlvTypes.InvoiceBlindedPay, out var payValue)
         || !TryReadPayInfos(payValue.Span, out var payInfos))
        {
            reason = "B12-INV-03: invoice_blindedpay is missing or malformed";
            return false;
        }

        if (payInfos.Count != wirePaths.Count)
        {
            reason = $"B12-INV-03: {payInfos.Count} blinded_payinfo for {wirePaths.Count} paths";
            return false;
        }

        totalPaths = wirePaths.Count;
        paths = [];
        for (var i = 0; i < wirePaths.Count; i++)
        {
            // B12-INV-03: a path whose pay info sets an unknown even feature is not used (none is known)
            if (OfferToPay.HasEvenBit(payInfos[i].Features.Span))
                continue;
            if (resolveNode(wirePaths[i].FirstNode) is not { } introduction)
                continue;

            paths.Add(new BlindedPaymentPath(BlindedPathCodec.ToBlindedPath(wirePaths[i], introduction), payInfos[i]));
        }

        if (paths.Count == 0)
        {
            reason = "B12-INV-03: no usable invoice path (unknown features or unknown introduction nodes)";
            return false;
        }

        reason = null;
        return true;
    }

    private static bool IsBitSet(ReadOnlySpan<byte> features, ulong bit)
    {
        var byteIndex = features.Length - 1 - (int)(bit / 8);
        return byteIndex >= 0 && (features[byteIndex] & (1 << (int)(bit % 8))) != 0;
    }

    private static bool TryFindUnknownEvenBit(ReadOnlySpan<byte> features, ulong knownEvenBit, out ulong bit)
    {
        for (var i = 0; i < features.Length; i++)
        {
            for (var b = 0; b < 8; b += 2)
            {
                if ((features[i] & (1 << b)) == 0)
                    continue;
                bit = (ulong)((features.Length - 1 - i) * 8 + b);
                if (bit != knownEvenBit)
                    return true;
            }
        }

        bit = 0;
        return false;
    }
}

/// <summary>
/// An invoice that passed <see cref="InvoiceVerifier.TryVerify"/>.
/// </summary>
/// <param name="Invoice">The invoice's fields.</param>
/// <param name="Paths">Its usable paths, in the invoice's order of preference, introduction nodes named.</param>
/// <param name="AllowsMpp">Whether <c>invoice_features</c> sets <c>basic_mpp</c> (16 or 17).</param>
public sealed record VerifiedInvoice(FetchedBolt12Invoice Invoice, IReadOnlyList<BlindedPaymentPath> Paths,
                                     bool AllowsMpp);
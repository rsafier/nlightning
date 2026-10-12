using System.Buffers;
using System.Text;

namespace NLightning.Application.Offers.Receive;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;

/// <summary>
/// An invoice_request that passed the BOLT 12 reader checks that need no offer: its fields decoded, its offer fields
/// and non-signature records kept raw.
/// </summary>
/// <param name="Stream">Every record, as received.</param>
/// <param name="OfferBytes">The offer fields (types 1-79 and 1,000,000,000-1,999,999,999) as a TLV stream: the bytes an
/// offer of ours must equal exactly (B12-IRQ-03). Empty when the request copies no offer field.</param>
/// <param name="PayerId"><c>invreq_payer_id</c>.</param>
/// <param name="Metadata"><c>invreq_metadata</c>.</param>
/// <param name="Signature">The 64-byte <c>signature</c>.</param>
/// <param name="Chain"><c>invreq_chain</c>, or null.</param>
/// <param name="Amount"><c>invreq_amount</c> in msat, or null.</param>
/// <param name="Quantity"><c>invreq_quantity</c>, or null.</param>
/// <param name="PayerNote"><c>invreq_payer_note</c>, or null.</param>
/// <param name="HasBip353Name">Whether <c>invreq_bip_353_name</c> is present.</param>
/// <param name="Bip353NameIsValid">Whether its name and domain use only the allowed bytes (checked after the
/// signature: an <c>invoice_error</c> names it).</param>
/// <param name="HasOfferIssuerId">Whether the copied offer has <c>offer_issuer_id</c>.</param>
/// <param name="HasOfferPaths">Whether the copied offer has <c>offer_paths</c>.</param>
public sealed record ReadInvoiceRequest(
    Bolt12TlvStream Stream,
    ReadOnlyMemory<byte> OfferBytes,
    CompactPubKey PayerId,
    ReadOnlyMemory<byte> Metadata,
    ReadOnlyMemory<byte> Signature,
    ChainHash? Chain,
    ulong? Amount,
    ulong? Quantity,
    string? PayerNote,
    bool HasBip353Name,
    bool Bip353NameIsValid,
    bool HasOfferIssuerId,
    bool HasOfferPaths)
{
    /// <summary>
    /// Whether the request answers an offer (BOLT 12: <c>offer_issuer_id</c> or <c>offer_paths</c> present).
    /// </summary>
    public bool IsForOffer => HasOfferIssuerId || HasOfferPaths;
}

/// <summary>
/// The BOLT 12 "Invoice Requests" reader checks that come before the offer lookup and the signature (B12-IRQ-02):
/// every record in range, <c>invreq_payer_id</c> and <c>invreq_metadata</c> present, no unknown even
/// <c>invreq_features</c> bit, every <c>invreq_paths</c> path with at least one hop, exactly one signature, and the
/// fixed-size or truncated fields well formed.
/// </summary>
/// <remarks>
/// A request refused here gets no answer (plan D10: an <c>invoice_error</c> only after the signature verified). Pure.
/// </remarks>
public static class InvoiceRequestReader
{
    private const ulong ExperimentalOfferStart = 1_000_000_000;
    private const ulong ExperimentalOfferEnd = 1_999_999_999;
    private const ulong ExperimentalInvreqEnd = 2_999_999_999;
    private const ulong LastInvreqType = 159;

    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    private static readonly SearchValues<byte> s_bip353Bytes =
        SearchValues.Create("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ-_."u8);

    /// <summary>
    /// Whether <paramref name="type"/> is an offer field (BOLT 12: 1-79 and 1,000,000,000-1,999,999,999).
    /// </summary>
    public static bool IsOfferType(ulong type) =>
        type is >= 1 and <= 79 or >= ExperimentalOfferStart and <= ExperimentalOfferEnd;

    /// <summary>
    /// Whether <paramref name="type"/> may appear in an invoice_request outside the signature range (BOLT 12: 0-159 and
    /// 1,000,000,000-2,999,999,999).
    /// </summary>
    public static bool IsInvoiceRequestType(ulong type) =>
        type <= LastInvreqType || type is >= ExperimentalOfferStart and <= ExperimentalInvreqEnd;

    /// <summary>
    /// Reads the invoice_request TLV stream <paramref name="bytes"/>.
    /// </summary>
    /// <param name="bytes">The <c>onionmsg_tlv</c> type 64 value.</param>
    /// <param name="request">The request, when every check passed.</param>
    /// <param name="reason">Why it was refused, otherwise.</param>
    public static bool TryRead(ReadOnlyMemory<byte> bytes, out ReadInvoiceRequest? request, out string? reason)
    {
        request = null;
        if (!Bolt12TlvStream.TryParse(bytes, out var stream) || stream is null)
            return Fail("not a valid TLV stream", out reason);

        ReadOnlyMemory<byte>? signature = null;
        foreach (var record in stream.Records)
        {
            if (Bolt12TlvRanges.IsSignatureField(record.Type))
            {
                // BOLT 12: exactly one signature element, `signature`
                if (record.Type != Bolt12TlvTypes.Signature || signature is not null)
                    return Fail($"unexpected signature element {record.Type}", out reason);
                if (record.Value.Length != Bolt12Constants.SignatureLength)
                    return Fail("signature is not 64 bytes", out reason);

                signature = record.Value;
                continue;
            }

            if (!IsInvoiceRequestType(record.Type))
                return Fail($"type {record.Type} is outside the invoice_request ranges", out reason);
        }

        if (signature is null)
            return Fail("no signature", out reason);

        if (!stream.TryGetValue(Bolt12TlvTypes.InvreqMetadata, out var metadata))
            return Fail("no invreq_metadata", out reason);

        if (!stream.TryGetValue(Bolt12TlvTypes.InvreqPayerId, out var payerIdBytes)
         || !IsCompressedPoint(payerIdBytes.Span))
            return Fail("invreq_payer_id missing or not a point", out reason);

        if (stream.TryGetValue(Bolt12TlvTypes.InvreqFeatures, out var features)
         && Bolt12FieldCodec.FindUnknownEvenBit(features.Span) is not null)
            return Fail("invreq_features has an unknown even bit", out reason);

        if (stream.TryGetValue(Bolt12TlvTypes.InvreqPaths, out var paths)
         && (!BlindedPathCodec.TryReadList(paths.Span, out var pathList, out _) || pathList.Count == 0))
            return Fail("invreq_paths is malformed", out reason);

        ChainHash? chain = null;
        if (stream.TryGetValue(Bolt12TlvTypes.InvreqChain, out var chainBytes))
        {
            if (chainBytes.Length != CryptoConstants.Sha256HashLen)
                return Fail("invreq_chain is not 32 bytes", out reason);

            chain = new ChainHash(chainBytes.ToArray());
        }

        if (!TryReadTu64(stream, Bolt12TlvTypes.InvreqAmount, out var amount)
         || !TryReadTu64(stream, Bolt12TlvTypes.InvreqQuantity, out var quantity))
            return Fail("invreq_amount or invreq_quantity is not a minimal tu64", out reason);

        string? payerNote = null;
        if (stream.TryGetValue(Bolt12TlvTypes.InvreqPayerNote, out var noteBytes))
        {
            try
            {
                payerNote = s_strictUtf8.GetString(noteBytes.Span);
            }
            catch (DecoderFallbackException)
            {
                return Fail("invreq_payer_note is not UTF-8", out reason);
            }
        }

        var hasBip353 = stream.TryGetValue(Bolt12TlvTypes.InvreqBip353Name, out var bip353);
        var offerRecords = stream.Records.Where(r => IsOfferType(r.Type)).ToList();
        request = new ReadInvoiceRequest(
            stream,
            offerRecords.Count == 0 ? ReadOnlyMemory<byte>.Empty : new Bolt12TlvStream(offerRecords).Encode(),
            new CompactPubKey(payerIdBytes.ToArray()),
            metadata,
            signature.Value,
            chain,
            amount,
            quantity,
            payerNote,
            hasBip353,
            !hasBip353 || IsValidBip353Name(bip353.Span),
            stream.TryGetValue(Bolt12TlvTypes.OfferIssuerId, out _),
            stream.TryGetValue(Bolt12TlvTypes.OfferPaths, out _));
        reason = null;
        return true;
    }

    /// <summary>
    /// BOLT 12 <c>invreq_bip_353_name</c>: <c>u8 name_len || name || u8 domain_len || domain</c>, every byte one of
    /// <c>0-9 a-z A-Z - _ .</c>.
    /// </summary>
    public static bool IsValidBip353Name(ReadOnlySpan<byte> value)
    {
        if (value.Length < 1)
            return false;

        var nameLength = value[0];
        if (value.Length < 1 + nameLength + 1)
            return false;

        var name = value.Slice(1, nameLength);
        var domainLength = value[1 + nameLength];
        var domain = value[(2 + nameLength)..];
        return domain.Length == domainLength && name.IndexOfAnyExcept(s_bip353Bytes) < 0
                                             && domain.IndexOfAnyExcept(s_bip353Bytes) < 0;
    }

    private static bool TryReadTu64(Bolt12TlvStream stream, ulong type, out ulong? value)
    {
        value = null;
        if (!stream.TryGetValue(type, out var bytes))
            return true;

        if (!TruncatedInt.TryDecodeTu64(bytes.Span, out var decoded))
            return false;

        value = decoded;
        return true;
    }

    private static bool IsCompressedPoint(ReadOnlySpan<byte> bytes) =>
        bytes.Length == CryptoConstants.CompactPubkeyLen && bytes[0] is 0x02 or 0x03;

    private static bool Fail(string why, out string? reason)
    {
        reason = why;
        return false;
    }
}
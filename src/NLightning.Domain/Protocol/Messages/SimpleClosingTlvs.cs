namespace NLightning.Domain.Protocol.Messages;

using Models;
using Payloads;
using Tlv;

/// <summary>
/// The <c>closing_tlvs</c> stream of a set of signatures: the ECDSA ones (types 1-3, each a raw 64-byte TLV value),
/// then the simple taproot ones (types 5-7, raw 98-byte or 32-byte values) and <c>next_closee_nonce</c> (22), in
/// ascending type order.
/// </summary>
internal static class SimpleClosingTlvs
{
    public static TlvStream? ToStream(ClosingSignatures signatures,
                                      ClosingPartialSignaturesWithNonce? partialSignaturesWithNonce = null,
                                      ClosingPartialSignatures? partialSignatures = null,
                                      NextCloseeNonceTlv? nextCloseeNonceTlv = null)
    {
        var stream = new TlvStream();
        foreach (var kind in signatures.Kinds)
            stream.Add(new BaseTlv(ClosingSignatures.TypeOf(kind), (byte[])signatures.Get(kind)!));

        if (partialSignaturesWithNonce is not null)
        {
            foreach (var kind in partialSignaturesWithNonce.Kinds)
            {
                var signature = partialSignaturesWithNonce.Get(kind)!.Value;
                stream.Add(new BaseTlv(ClosingPartialSignaturesWithNonce.TypeOf(kind), signature.ToBytes()));
            }
        }

        if (partialSignatures is not null)
        {
            foreach (var kind in partialSignatures.Kinds)
            {
                var signature = partialSignatures.Get(kind)!.Value;
                stream.Add(new BaseTlv(ClosingPartialSignatures.TypeOf(kind), [.. (byte[])signature]));
            }
        }

        if (nextCloseeNonceTlv is not null)
            stream.Add(nextCloseeNonceTlv);

        return stream.Any() ? stream : null;
    }
}
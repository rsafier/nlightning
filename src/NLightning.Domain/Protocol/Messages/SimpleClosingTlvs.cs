namespace NLightning.Domain.Protocol.Messages;

using Models;
using Payloads;
using Tlv;

/// <summary>The <c>closing_tlvs</c> stream of a set of signatures (each a raw 64-byte TLV value).</summary>
internal static class SimpleClosingTlvs
{
    public static TlvStream? ToStream(ClosingSignatures signatures)
    {
        var kinds = signatures.Kinds;
        if (kinds.Count == 0)
            return null;

        var stream = new TlvStream();
        foreach (var kind in kinds)
            stream.Add(new BaseTlv(ClosingSignatures.TypeOf(kind), (byte[])signatures.Get(kind)!));
        return stream;
    }
}
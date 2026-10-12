namespace NLightning.Domain.Crypto.Models;

using Constants;
using ValueObjects;

/// <summary>
/// The result of BIP 327 KeyAgg (and KeySort, when asked) over a set of public keys, with its tweaks applied: what a
/// MuSig2 session over these keys signs for.
/// </summary>
/// <param name="PubKeys">The keys in the order KeyAgg took them (sorted when the aggregation sorted them); a session
/// must use this order.</param>
/// <param name="Tweaks">The tweaks applied after KeyAgg, in order (for a taproot key path: the one x-only BIP 341
/// tweak).</param>
/// <param name="InternalKey">The untweaked aggregate key <c>Q</c> of KeyAgg as a compressed point. For a taproot
/// output it is the internal key; its x coordinate is BIP 327's <c>GetXonlyPk(KeyAgg(...))</c>.</param>
/// <param name="OutputKey">The aggregate key after the tweaks as a compressed point: the key the final BIP-340
/// signature verifies under (x coordinate only) and, for a taproot key path, the output key whose x coordinate is the
/// witness program. Its first byte gives the y parity (03 = odd) that a script path's control block needs.</param>
public sealed record MusigKeyAggregate(IReadOnlyList<CompactPubKey> PubKeys,
                                       IReadOnlyList<MusigTweak> Tweaks,
                                       CompactPubKey InternalKey,
                                       CompactPubKey OutputKey)
{
    /// <summary>
    /// The 32-byte x-only output key: the P2TR witness program and the BIP-340 verification key.
    /// </summary>
    public byte[] XOnlyOutputKey => ((ReadOnlySpan<byte>)OutputKey)[1..].ToArray();

    /// <summary>
    /// Whether the output key has an odd y coordinate (the BIP 341 control block's parity bit).
    /// </summary>
    public bool OutputKeyHasOddY => ((ReadOnlySpan<byte>)OutputKey)[0] == 0x03;

    /// <summary>
    /// The P2TR scriptPubKey of the output key: <c>OP_1 OP_PUSHBYTES_32 x(OutputKey)</c>.
    /// </summary>
    public byte[] GetTaprootScriptPubKey()
    {
        var script = new byte[2 + MusigConstants.XOnlyPubKeyLen];
        script[0] = 0x51;
        script[1] = MusigConstants.XOnlyPubKeyLen;
        ((ReadOnlySpan<byte>)OutputKey)[1..].CopyTo(script.AsSpan(2));
        return script;
    }

    /// <summary>
    /// A signing session over these keys and tweaks.
    /// </summary>
    public MusigSigningSession CreateSession(MusigAggregateNonce aggregateNonce, ReadOnlyMemory<byte> message) =>
        new(PubKeys, Tweaks, aggregateNonce, message);
}
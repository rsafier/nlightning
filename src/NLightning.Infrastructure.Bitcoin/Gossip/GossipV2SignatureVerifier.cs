using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NLightning.Infrastructure.Bitcoin.Gossip;

using Crypto.Contexts;
using Domain.Crypto.Constants;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Interfaces;
using Domain.Protocol.Payloads;

/// <summary>
/// <see cref="IGossipV2SignatureVerifier"/> over libsecp256k1 (BIP 340) and our BIP 327 MuSig2 key aggregation.
/// </summary>
/// <remarks>
/// <para>
/// Channel proofs (BOLTs PR #1059 draft <c>4eef3dfa</c>): with both bitcoin keys, a P2WSH output must be the BOLT 3
/// 2-of-2 of the keys (no merkle root) and a P2TR output the MuSig2 aggregate of the keys, tweaked by the merkle root
/// when one is given; the signature is then checked against <c>KeyAgg(KeySort(node_id_1, node_id_2, bitcoin_key_1,
/// bitcoin_key_2))</c>. Without bitcoin keys (P2TR only) the aggregate is
/// <c>KeyAgg(KeySort(node_id_1, node_id_2, taproot_output_key))</c>.
/// </para>
/// <para>
/// Deviation (NL-1130): simple taproot channels fund a BIP 86 output (<c>P_internal + H_TapTweak(P_internal)·G</c>,
/// no script tree), which the draft's two P2TR forms cannot express (its "no merkle root" form wants the untweaked key,
/// its merkle-root form hashes <c>p || merkle_root</c>). An announcement with both keys and no merkle root is therefore
/// accepted when the output is either the untweaked aggregate or its BIP 86 tweak; that is how our own taproot
/// channels are announced.
/// </para>
/// </remarks>
public sealed class GossipV2SignatureVerifier : IGossipV2SignatureVerifier
{
    private const byte OpTwo = 0x52;
    private const byte OpCheckMultiSig = 0xae;

    private readonly IMusig2Service _musig2;

    public GossipV2SignatureVerifier(IMusig2Service musig2)
    {
        _musig2 = musig2;
    }

    /// <inheritdoc />
    public bool VerifyBip340(Hash messageHash, CompactSignature signature, CompactPubKey publicKey)
    {
        if (signature is not { Value.Length: MusigConstants.SchnorrSignatureLen })
            return false;

        byte[] key = publicKey;
        if (key is not { Length: CryptoConstants.CompactPubkeyLen })
            return false;

        if (!ECXOnlyPubKey.TryCreate(key.AsSpan(1), NLightningCryptoContext.Instance, out var xOnly) || xOnly is null
         || !SecpSchnorrSignature.TryCreate(signature.Value, out var schnorr) || schnorr is null)
            return false;

        return xOnly.SigVerifyBIP340(schnorr, (byte[])messageHash);
    }

    /// <inheritdoc />
    public GossipV2ProofResult CheckChannelProof(ChannelAnnouncement2Payload announcement,
                                                 ReadOnlySpan<byte> fundingScriptPubKey)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        var isP2wsh = fundingScriptPubKey.Length == 34 && fundingScriptPubKey[0] == 0x00
                                                       && fundingScriptPubKey[1] == 0x20;
        var isP2tr = fundingScriptPubKey.Length == 34 && fundingScriptPubKey[0] == 0x51
                                                      && fundingScriptPubKey[1] == 0x20;
        if (!isP2wsh && !isP2tr)
            return GossipV2ProofResult.UnsupportedScript;

        var witnessProgram = fundingScriptPubKey[2..].ToArray();
        List<CompactPubKey> keys = [announcement.NodeId1, announcement.NodeId2];
        try
        {
            if (announcement.BitcoinKey1 is { } key1 && announcement.BitcoinKey2 is { } key2)
            {
                if (isP2wsh)
                {
                    if (announcement.MerkleRootHash is not null)
                        return GossipV2ProofResult.MalformedProof;
                    if (!witnessProgram.AsSpan().SequenceEqual(P2wshProgram(key1, key2)))
                        return GossipV2ProofResult.KeyMismatch;
                }
                else if (!MatchesTaprootOutput(key1, key2, announcement.MerkleRootHash, witnessProgram))
                {
                    return GossipV2ProofResult.KeyMismatch;
                }

                keys.Add(key1);
                keys.Add(key2);
            }
            else
            {
                if (announcement.BitcoinKey1 is not null || announcement.BitcoinKey2 is not null || isP2wsh)
                    return GossipV2ProofResult.MalformedProof;

                // The x-only output key as a plain (even) key: the third MuSig2 participant
                keys.Add(new CompactPubKey([0x02, .. witnessProgram]));
            }

            return VerifyAggregateSignature(announcement, keys);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                       or Domain.Exceptions.MusigException)
        {
            // A key that is not on the curve, or an aggregate at infinity
            return GossipV2ProofResult.KeyMismatch;
        }
    }

    /// <inheritdoc />
    public GossipV2ProofResult CheckChannelSignature(ChannelAnnouncement2Payload announcement)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        if (announcement.BitcoinKey1 is not { } key1 || announcement.BitcoinKey2 is not { } key2)
            return GossipV2ProofResult.MalformedProof;

        try
        {
            return VerifyAggregateSignature(announcement, [announcement.NodeId1, announcement.NodeId2, key1, key2]);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                       or Domain.Exceptions.MusigException)
        {
            // A key that is not on the curve, or an aggregate at infinity
            return GossipV2ProofResult.KeyMismatch;
        }
    }

    /// <summary>The announcement's signature against the MuSig2 aggregate of the sorted <paramref name="keys"/>.</summary>
    private GossipV2ProofResult VerifyAggregateSignature(ChannelAnnouncement2Payload announcement,
                                                         List<CompactPubKey> keys)
    {
        var aggregate = _musig2.AggregatePubKeys(_musig2.SortPubKeys(keys));
        return _musig2.VerifySignature(announcement.Signature.Value, aggregate.XOnlyOutputKey,
                                       (byte[])announcement.GetSignatureHash())
                   ? GossipV2ProofResult.Valid
                   : GossipV2ProofResult.BadSignature;
    }

    private bool MatchesTaprootOutput(CompactPubKey key1, CompactPubKey key2, ReadOnlyMemory<byte>? merkleRoot,
                                      byte[] outputKey)
    {
        var sorted = _musig2.SortPubKeys([key1, key2]);
        var internalAggregate = _musig2.AggregatePubKeys(sorted);
        if (merkleRoot is { } root)
        {
            // BIP 341: t = H_TapTweak(p || merkle_root), Q = P + t·G
            var xOnlyInternal = internalAggregate.XOnlyOutputKey;
            var tweak = TapTweak([.. xOnlyInternal, .. root.Span]);
            var tweaked = _musig2.AggregatePubKeys(sorted, [new MusigTweak(tweak, true)]);
            return tweaked.XOnlyOutputKey.AsSpan().SequenceEqual(outputKey);
        }

        // The draft's untweaked form, or the BIP 86 key path of a simple taproot channel (see the remarks)
        return internalAggregate.XOnlyOutputKey.AsSpan().SequenceEqual(outputKey)
            || _musig2.AggregateTaprootKeyPath(key1, key2).XOnlyOutputKey.AsSpan().SequenceEqual(outputKey);
    }

    private static byte[] P2wshProgram(CompactPubKey key1, CompactPubKey key2)
    {
        // BOLT 3: 2 <lesser> <greater> 2 OP_CHECKMULTISIG, keys ordered lexicographically
        var (lesser, greater) = ((ReadOnlySpan<byte>)key1).SequenceCompareTo(key2) < 0 ? (key1, key2) : (key2, key1);
        byte[] script =
        [
            OpTwo, CryptoConstants.CompactPubkeyLen, .. (byte[])lesser, CryptoConstants.CompactPubkeyLen,
            .. (byte[])greater, OpTwo, OpCheckMultiSig
        ];
        return SHA256.HashData(script);
    }

    private static byte[] TapTweak(ReadOnlySpan<byte> data)
    {
        var tag = SHA256.HashData("TapTweak"u8);
        return SHA256.HashData([.. tag, .. tag, .. data]);
    }
}
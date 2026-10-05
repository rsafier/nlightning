using System.Diagnostics.CodeAnalysis;
using NBitcoin.Secp256k1;

namespace NLightning.Tests.Utils.Gossip;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Addresses;
using Domain.Gossip.Interfaces;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Musig2;
using Infrastructure.Bitcoin.Gossip;

/// <summary>A secp256k1 key for signing taproot gossip in tests (BIP 340 and MuSig2, NL-878).</summary>
[ExcludeFromCodeCoverage]
public sealed class GossipV2TestKey
{
    private readonly ECPrivKey _key;

    public GossipV2TestKey(byte seed)
    {
        var bytes = new byte[32];
        bytes[0] = 0x22;
        bytes[31] = seed;
        PrivKey = new PrivKey(bytes);
        _key = Context.Instance.CreateECPrivKey(bytes);
        var pubKey = new byte[33];
        _key.CreatePubKey().WriteToSpan(true, pubKey, out _);
        PubKey = new CompactPubKey(pubKey);
    }

    public CompactPubKey PubKey { get; }

    public PrivKey PrivKey { get; }

    /// <summary>A BIP 340 signature of <paramref name="hash"/> by this key's x-only form.</summary>
    public CompactSignature SignBip340(Hash hash)
    {
        var signature = _key.SignBIP340((byte[])hash);
        var bytes = new byte[64];
        signature.WriteToSpan(bytes);
        return new CompactSignature(bytes);
    }
}

/// <summary>
/// Builds signed taproot gossip (BOLTs PR #1059, NL-878) with the production MuSig2 and the production verifier: a
/// <c>channel_announcement_2</c> signed by the 4-key aggregate of its node and bitcoin keys, BIP 340
/// <c>channel_update_2</c>s and <c>node_announcement_2</c>s.
/// </summary>
[ExcludeFromCodeCoverage]
public static class GossipV2TestSigner
{
    /// <summary>The production BIP 327 MuSig2.</summary>
    public static IMusig2Service Musig2 { get; } = new Musig2Service();

    /// <summary>The production v2 verifier over <see cref="Musig2"/>.</summary>
    public static IGossipV2SignatureVerifier Verifier { get; } = new GossipV2SignatureVerifier(Musig2);

    /// <summary>The simple taproot channel funding output of two bitcoin keys (BIP 86 key path).</summary>
    public static byte[] TaprootFundingScript(CompactPubKey bitcoinKey1, CompactPubKey bitcoinKey2) =>
        Musig2.AggregateTaprootKeyPath(bitcoinKey1, bitcoinKey2).GetTaprootScriptPubKey();

    /// <summary>
    /// A <c>channel_announcement_2</c> between two nodes (ordered by node id) with their bitcoin keys, signed by the
    /// MuSig2 aggregate of all four keys over its signature hash.
    /// </summary>
    public static ChannelAnnouncement2Payload SignedChannelAnnouncement2(
        ChainHash chainHash, ShortChannelId shortChannelId, ulong capacitySatoshis, GossipV2TestKey nodeA,
        GossipV2TestKey nodeB, GossipV2TestKey bitcoinA, GossipV2TestKey bitcoinB, TxId fundingTxId,
        ReadOnlyMemory<byte> features = default)
    {
        var aFirst = ((ReadOnlySpan<byte>)nodeA.PubKey).SequenceCompareTo(nodeB.PubKey) < 0;
        var (node1, node2, bitcoin1, bitcoin2) =
            aFirst ? (nodeA, nodeB, bitcoinA, bitcoinB) : (nodeB, nodeA, bitcoinB, bitcoinA);
        var unsigned = ChannelAnnouncement2Payload.Create(chainHash, features.Span, shortChannelId, capacitySatoshis,
                                                          node1.PubKey, node2.PubKey, bitcoin1.PubKey,
                                                          bitcoin2.PubKey, ReadOnlySpan<byte>.Empty, fundingTxId,
                                                          shortChannelId.OutputIndex);
        var signature = MusigSign([node1, node2, bitcoin1, bitcoin2], (byte[])unsigned.GetSignatureHash());
        return unsigned.WithSignature(new CompactSignature(signature));
    }

    /// <summary>A <c>channel_update_2</c> of <paramref name="origin"/> for <paramref name="direction"/>.</summary>
    public static ChannelUpdate2Payload SignedChannelUpdate2(ChainHash chainHash, ShortChannelId shortChannelId,
                                                             byte direction, uint blockHeight, GossipV2TestKey origin,
                                                             uint feeBaseMsat = 1_000, uint feePpm = 100,
                                                             ulong htlcMaximumMsat = 500_000_000,
                                                             uint inboundFeeBaseMsat = 0, uint inboundFeePpm = 0,
                                                             byte disableFlags = 0, ushort cltvExpiryDelta = 40)
    {
        var unsigned = ChannelUpdate2Payload.Create(chainHash, shortChannelId, direction, blockHeight, disableFlags,
                                                    cltvExpiryDelta, 1_000, htlcMaximumMsat, feeBaseMsat, feePpm,
                                                    inboundFeeBaseMsat, inboundFeePpm);
        return unsigned.WithSignature(origin.SignBip340(unsigned.GetSignatureHash()));
    }

    /// <summary>A <c>node_announcement_2</c> of <paramref name="node"/> with one IPv4 address.</summary>
    public static NodeAnnouncement2Payload SignedNodeAnnouncement2(GossipV2TestKey node, uint blockHeight,
                                                                   string alias = "v2node")
    {
        var address = AddressDescriptorCodec.DecodeList([1, 127, 0, 0, 1, 0x26, 0x07]).Addresses;
        var unsigned = NodeAnnouncement2Payload.Create(ReadOnlySpan<byte>.Empty, blockHeight, node.PubKey,
                                                       [1, 2, 3], System.Text.Encoding.UTF8.GetBytes(alias), address);
        return unsigned.WithSignature(node.SignBip340(unsigned.GetSignatureHash()));
    }

    /// <summary>A BIP 340 signature of <paramref name="message"/> by the MuSig2 aggregate of the keys.</summary>
    public static byte[] MusigSign(IReadOnlyList<GossipV2TestKey> signers, byte[] message)
    {
        var aggregate = Musig2.AggregatePubKeys(Musig2.SortPubKeys(signers.Select(s => s.PubKey)));
        var nonces = signers.Select(s => Musig2.GenerateNonce(s.PubKey, s.PrivKey, aggregate.XOnlyOutputKey,
                                                              message))
                            .ToList();
        var session = Musig2.CreateSession(aggregate, nonces.Select(n => n.PublicNonce).ToList(), message);
        var partials = signers.Select((s, i) => Musig2.Sign(nonces[i].SecretNonce, s.PrivKey, session)).ToList();
        return Musig2.AggregatePartialSignatures(partials, session);
    }
}
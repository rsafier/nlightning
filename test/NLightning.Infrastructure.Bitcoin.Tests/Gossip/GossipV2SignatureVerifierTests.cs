using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Protocol.Constants;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Crypto.Musig2;
using Infrastructure.Bitcoin.Gossip;
using Key = NBitcoin.Key;

/// <summary>
/// The channel proofs of <c>channel_announcement_2</c> (BOLTs PR #1059 "Channel announcement validation", NL-878):
/// P2WSH with both keys, P2TR with both keys (untweaked, BIP 86, merkle-root tweak) and P2TR without keys (the 3-key
/// aggregate with the output key), each signed by a real MuSig2 session, and every rejection branch.
/// </summary>
public class GossipV2SignatureVerifierTests
{
    private static readonly ShortChannelId s_scid = new(700, 1, 0);
    private static readonly TxId s_txId = new(Enumerable.Repeat((byte)0x77, 32).ToArray());

    private readonly Musig2Service _musig2 = new();
    private readonly GossipV2SignatureVerifier _verifier;
    private readonly Key _node1;
    private readonly Key _node2;
    private readonly Key _bitcoin1 = new();
    private readonly Key _bitcoin2 = new();

    public GossipV2SignatureVerifierTests()
    {
        _verifier = new GossipV2SignatureVerifier(_musig2);
        var a = new Key();
        var b = new Key();
        var aFirst = a.PubKey.ToBytes().AsSpan().SequenceCompareTo(b.PubKey.ToBytes()) < 0;
        (_node1, _node2) = aFirst ? (a, b) : (b, a);
    }

    [Fact]
    public void Given_AP2wshChannelWithBothKeys_When_Checked_Then_Valid()
    {
        // Arrange
        var unsigned = Unsigned(withKeys: true);
        var signed = Sign(unsigned, _node1, _node2, _bitcoin1, _bitcoin2);

        // Act / Assert
        Assert.Equal(GossipV2ProofResult.Valid, _verifier.CheckChannelProof(signed, P2wsh(_bitcoin1, _bitcoin2)));
        Assert.Equal(GossipV2ProofResult.KeyMismatch,
                     _verifier.CheckChannelProof(signed, P2wsh(_bitcoin1, new Key())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_AP2trChannelWithBothKeys_When_Checked_Then_TheUntweakedOrBip86OutputIsValid(bool bip86)
    {
        // Arrange
        var signed = Sign(Unsigned(withKeys: true), _node1, _node2, _bitcoin1, _bitcoin2);
        var aggregate = bip86
                            ? _musig2.AggregateTaprootKeyPath(Pub(_bitcoin1), Pub(_bitcoin2))
                            : _musig2.AggregatePubKeys(_musig2.SortPubKeys([Pub(_bitcoin1), Pub(_bitcoin2)]));

        // Act / Assert
        Assert.Equal(GossipV2ProofResult.Valid,
                     _verifier.CheckChannelProof(signed, aggregate.GetTaprootScriptPubKey()));
    }

    [Fact]
    public void Given_AP2trChannelWithAMerkleRoot_When_Checked_Then_TheTweakedOutputIsValid()
    {
        // Arrange
        var merkleRoot = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        var signed = Sign(Unsigned(withKeys: true, merkleRoot: merkleRoot), _node1, _node2, _bitcoin1, _bitcoin2);
        var sorted = _musig2.SortPubKeys([Pub(_bitcoin1), Pub(_bitcoin2)]);
        var internalKey = _musig2.AggregatePubKeys(sorted).XOnlyOutputKey;
        var tag = SHA256.HashData("TapTweak"u8);
        var tweak = SHA256.HashData([.. tag, .. tag, .. internalKey, .. merkleRoot]);
        var output = _musig2.AggregatePubKeys(sorted, [new MusigTweak(tweak, true)]);

        // Act / Assert
        Assert.Equal(GossipV2ProofResult.Valid, _verifier.CheckChannelProof(signed, output.GetTaprootScriptPubKey()));
        Assert.Equal(GossipV2ProofResult.KeyMismatch,
                     _verifier.CheckChannelProof(signed, _musig2.AggregateTaprootKeyPath(Pub(_bitcoin1),
                                                                                         Pub(_bitcoin2))
                                                                .GetTaprootScriptPubKey()));
        Assert.Equal(GossipV2ProofResult.MalformedProof, _verifier.CheckChannelProof(signed, P2wsh(_bitcoin1,
                                                                                                    _bitcoin2)));
    }

    [Fact]
    public void Given_AP2trChannelWithoutKeys_When_Checked_Then_TheThreeKeyAggregateWithTheOutputKeyIsValid()
    {
        // Arrange: the output key is some x-only key the signers own (here an even-y key of its own)
        var outputKey = new Key();
        var even = outputKey.PubKey.ToBytes()[0] == 0x02 ? outputKey : NegatedKey(outputKey);
        var signed = Sign(Unsigned(withKeys: false), _node1, _node2, even);
        byte[] script = [0x51, 0x20, .. even.PubKey.ToBytes().AsSpan(1)];

        // Act / Assert
        Assert.Equal(GossipV2ProofResult.Valid, _verifier.CheckChannelProof(signed, script));
        Assert.Equal(GossipV2ProofResult.MalformedProof, _verifier.CheckChannelProof(signed, P2wsh(_bitcoin1,
                                                                                                    _bitcoin2)));
    }

    [Fact]
    public void Given_BadInputs_When_Checked_Then_EachRejectionIsReported()
    {
        // Arrange
        var signed = Sign(Unsigned(withKeys: true), _node1, _node2, _bitcoin1, _bitcoin2);
        var otherSigners = Sign(Unsigned(withKeys: true), _node1, _node2, _bitcoin1, new Key());
        var oneKey = ChannelAnnouncement2Payload.Parse(signed.Stream.With(
                                                           new Domain.Protocol.GossipV2.PureTlvRecord(
                                                               Domain.Protocol.GossipV2.GossipV2Constants
                                                                     .ChannelAnnouncement2.BitcoinKey1,
                                                               Pub(_bitcoin1)))
                                                       .GetBytes());
        var withoutKey2 = ChannelAnnouncement2Payload.Parse(
            new Domain.Protocol.GossipV2.PureTlvStream(oneKey.Stream.Records.Where(
                                                           r => r.Type != Domain.Protocol.GossipV2.GossipV2Constants
                                                                               .ChannelAnnouncement2.BitcoinKey2))
               .GetBytes());
        var p2tr = _musig2.AggregateTaprootKeyPath(Pub(_bitcoin1), Pub(_bitcoin2)).GetTaprootScriptPubKey();

        // Act / Assert
        Assert.Equal(GossipV2ProofResult.UnsupportedScript,
                     _verifier.CheckChannelProof(signed, [0x00, 0x14, .. new byte[20]]));
        Assert.Equal(GossipV2ProofResult.BadSignature, _verifier.CheckChannelProof(otherSigners, p2tr));
        Assert.Equal(GossipV2ProofResult.MalformedProof, _verifier.CheckChannelProof(withoutKey2, p2tr));
    }

    private ChannelAnnouncement2Payload Unsigned(bool withKeys, byte[]? merkleRoot = null) =>
        ChannelAnnouncement2Payload.Create(ChainConstants.Regtest, [], s_scid, 1_000_000, Pub(_node1), Pub(_node2),
                                           withKeys ? Pub(_bitcoin1) : null, withKeys ? Pub(_bitcoin2) : null,
                                           merkleRoot ?? [], s_txId, 0);

    /// <summary>A MuSig2 session of the given private keys over the announcement's message.</summary>
    private ChannelAnnouncement2Payload Sign(ChannelAnnouncement2Payload unsigned, params Key[] keys)
    {
        var message = (byte[])unsigned.GetSignatureHash();
        var aggregate = _musig2.AggregatePubKeys(_musig2.SortPubKeys(keys.Take(4).Select(Pub)));
        var nonces = keys.Select(k => _musig2.GenerateNonce(Pub(k), k.ToBytes(), aggregate.XOnlyOutputKey, message))
                         .ToList();
        var session = _musig2.CreateSession(aggregate, nonces.Take(4).Select(n => n.PublicNonce).ToList(), message);
        var partials = keys.Take(4).Select((k, i) => _musig2.Sign(nonces[i].SecretNonce, k.ToBytes(), session))
                           .ToList();
        return unsigned.WithSignature(new CompactSignature(_musig2.AggregatePartialSignatures(partials, session)));
    }

    private static CompactPubKey Pub(Key key) => key.PubKey.ToBytes();

    private static byte[] P2wsh(Key key1, Key key2)
    {
        var (lesser, greater) = key1.PubKey.ToBytes().AsSpan().SequenceCompareTo(key2.PubKey.ToBytes()) < 0
                                    ? (key1, key2)
                                    : (key2, key1);
        byte[] witnessScript =
            [0x52, 0x21, .. lesser.PubKey.ToBytes(), 0x21, .. greater.PubKey.ToBytes(), 0x52, 0xae];
        return [0x00, 0x20, .. SHA256.HashData(witnessScript)];
    }

    private static Key NegatedKey(Key key)
    {
        var scalar = new NBitcoin.Secp256k1.Scalar(key.ToBytes()).Negate();
        var bytes = new byte[32];
        scalar.WriteToSpan(bytes);
        return new Key(bytes);
    }
}
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Enums;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Crypto.Musig2;
using Infrastructure.Bitcoin.Gossip;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;
using Key = NBitcoin.Key;

/// <summary>
/// Taproot gossip (BOLTs PR #1059, NL-878 T7): two signers run the 4-of-4 MuSig2 session of a public simple taproot
/// channel's <c>channel_announcement_2</c> (two nonces and two partial signatures each); the aggregated signature
/// verifies against <c>KeyAgg(KeySort(node_id_1, node_id_2, bitcoin_key_1, bitcoin_key_2))</c> and the BIP 86
/// funding output, nonces are single-use, and the signer refuses anything that is not this public taproot channel.
/// </summary>
public class LocalLightningSignerChannelAnnouncement2Tests
{
    private const uint ChannelKeyIndex = 5;
    private const ushort FundingOutputIndex = 0;
    private const ulong FundingSatoshis = 2_000_000;

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0xD2, 32).ToArray());
    private static readonly ShortChannelId s_scid = new(512, 3, FundingOutputIndex);
    private static readonly TxId s_fundingTxId = new(Enumerable.Repeat((byte)0xAB, 32).ToArray());

    private readonly Musig2Service _musig2 = new();
    private readonly Party _alice = new("1111111111111111111111111111111111111111111111111111111111111111",
                                         "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
    private readonly Party _bob = new("2222222222222222222222222222222222222222222222222222222222222222",
                                       "1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100");

    public LocalLightningSignerChannelAnnouncement2Tests()
    {
        _alice.Register(_bob);
        _bob.Register(_alice);
    }

    [Fact]
    public void Given_BothSigners_When_TheyRunTheSession_Then_TheAggregatedSignatureIsAValidChannelProof()
    {
        // Arrange
        var announcement = BuildUnsigned();
        var aliceNonces = _alice.Signer.CreateChannelAnnouncement2Nonces(s_channelId, announcement);
        var bobNonces = _bob.Signer.CreateChannelAnnouncement2Nonces(s_channelId, announcement);

        // Act
        var aliceSigs = _alice.Signer.SignChannelAnnouncement2(s_channelId, announcement, bobNonces.NodeNonce,
                                                               bobNonces.BitcoinNonce);
        var bobSigs = _bob.Signer.SignChannelAnnouncement2(s_channelId, announcement, aliceNonces.NodeNonce,
                                                           aliceNonces.BitcoinNonce);

        // Assert: each partial verifies under its signer's key and nonce, and the four aggregate to a valid proof
        var aggregate = _musig2.AggregatePubKeys(_musig2.SortPubKeys([
            announcement.NodeId1, announcement.NodeId2, announcement.BitcoinKey1!.Value,
            announcement.BitcoinKey2!.Value
        ]));
        var session = _musig2.CreateSession(aggregate,
                                            [
                                                aliceNonces.NodeNonce, aliceNonces.BitcoinNonce, bobNonces.NodeNonce,
                                                bobNonces.BitcoinNonce
                                            ], (byte[])announcement.GetSignatureHash());
        Assert.True(_musig2.VerifyPartialSignature(aliceSigs.NodeSignature, aliceNonces.NodeNonce, _alice.NodeId,
                                                   session));
        Assert.True(_musig2.VerifyPartialSignature(aliceSigs.BitcoinSignature, aliceNonces.BitcoinNonce,
                                                   _alice.FundingKey, session));
        Assert.True(_musig2.VerifyPartialSignature(bobSigs.BitcoinSignature, bobNonces.BitcoinNonce,
                                                   _bob.FundingKey, session));
        Assert.False(_musig2.VerifyPartialSignature(bobSigs.NodeSignature, bobNonces.NodeNonce, _alice.NodeId,
                                                    session));

        var signature = _musig2.AggregatePartialSignatures(
            [aliceSigs.NodeSignature, aliceSigs.BitcoinSignature, bobSigs.NodeSignature, bobSigs.BitcoinSignature],
            session);
        var signed = announcement.WithSignature(new CompactSignature(signature));
        var fundingScript = _musig2.AggregateTaprootKeyPath(_alice.FundingKey, _bob.FundingKey)
                                   .GetTaprootScriptPubKey();
        var verifier = new GossipV2SignatureVerifier(_musig2);
        Assert.Equal(GossipV2ProofResult.Valid, verifier.CheckChannelProof(signed, fundingScript));

        // A tampered record breaks it (the signature covers the signed range)
        var tampered = ChannelAnnouncement2Payload.Parse(signed.Stream.With(
                                                             new Domain.Protocol.GossipV2.PureTlvRecord(101, [1]))
                                                         .GetBytes());
        Assert.Equal(GossipV2ProofResult.BadSignature, verifier.CheckChannelProof(tampered, fundingScript));
    }

    [Fact]
    public void Given_ANoncePair_When_ItSignedOnce_Then_ASecondSignatureIsRefused()
    {
        // Arrange
        var announcement = BuildUnsigned();
        _alice.Signer.CreateChannelAnnouncement2Nonces(s_channelId, announcement);
        var bobNonces = _bob.Signer.CreateChannelAnnouncement2Nonces(s_channelId, announcement);
        _alice.Signer.SignChannelAnnouncement2(s_channelId, announcement, bobNonces.NodeNonce,
                                               bobNonces.BitcoinNonce);

        // Act / Assert
        var exception = Assert.Throws<SignerException>(() => _alice.Signer.SignChannelAnnouncement2(
                                                           s_channelId, announcement, bobNonces.NodeNonce,
                                                           bobNonces.BitcoinNonce));
        Assert.Contains("No live", exception.Message);
    }

    [Fact]
    public void Given_NewNoncesOrADiscard_When_Signing_Then_TheOldPairIsGone()
    {
        // Arrange
        var announcement = BuildUnsigned();
        var first = _alice.Signer.CreateChannelAnnouncement2Nonces(s_channelId, announcement);
        var second = _alice.Signer.CreateChannelAnnouncement2Nonces(s_channelId, announcement);
        var bobNonces = _bob.Signer.CreateChannelAnnouncement2Nonces(s_channelId, announcement);

        // Assert: fresh randomness every time
        Assert.NotEqual(first.NodeNonce, second.NodeNonce);
        Assert.NotEqual(first.BitcoinNonce, second.BitcoinNonce);

        // Act: a discard drops the live pair
        _alice.Signer.DiscardChannelAnnouncement2Nonces(s_channelId);
        Assert.Throws<SignerException>(() => _alice.Signer.SignChannelAnnouncement2(
                                           s_channelId, announcement, bobNonces.NodeNonce, bobNonces.BitcoinNonce));
    }

    [Fact]
    public void Given_NoncesForOneAnnouncement_When_SigningAnother_Then_RefusedAndThePairIsSpent()
    {
        // Arrange: nonces bound to one message, then a different announcement of the same channel (features added)
        var announcement = BuildUnsigned();
        var other = ChannelAnnouncement2Payload.Create(ChainConstants.Regtest, [0x01], s_scid, FundingSatoshis,
                                                       announcement.NodeId1, announcement.NodeId2,
                                                       announcement.BitcoinKey1, announcement.BitcoinKey2, [],
                                                       s_fundingTxId, FundingOutputIndex);
        _alice.Signer.CreateChannelAnnouncement2Nonces(s_channelId, announcement);
        var bobNonces = _bob.Signer.CreateChannelAnnouncement2Nonces(s_channelId, announcement);

        // Act / Assert
        var exception = Assert.Throws<SignerException>(() => _alice.Signer.SignChannelAnnouncement2(
                                                           s_channelId, other, bobNonces.NodeNonce,
                                                           bobNonces.BitcoinNonce));
        Assert.Contains("another announcement", exception.Message);
        Assert.Throws<SignerException>(() => _alice.Signer.SignChannelAnnouncement2(
                                           s_channelId, announcement, bobNonces.NodeNonce, bobNonces.BitcoinNonce));
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "scid", "short channel id" },
        { "capacity", "capacity" },
        { "outpoint", "funding outpoint" },
        { "chain", "another chain" },
        { "keys", "funding keys" },
        { "merkle", "merkle root" }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Given_AnAnnouncementThatIsNotThisChannel_When_CreatingNonces_Then_Refused(string change,
        string message)
    {
        // Arrange
        var good = BuildUnsigned();
        var key1 = good.BitcoinKey1!.Value;
        var key2 = good.BitcoinKey2!.Value;
        var bad = change switch
        {
            "scid" => Create(scid: new ShortChannelId(513, 3, FundingOutputIndex)),
            "capacity" => Create(capacity: FundingSatoshis - 1),
            "outpoint" => Create(fundingTxId: new TxId(Enumerable.Repeat((byte)0xAC, 32).ToArray())),
            "chain" => Create(chain: ChainConstants.Testnet),
            "keys" => Create(key1: key2, key2: key1),
            _ => Create(merkleRoot: new byte[32])
        };

        // Act / Assert
        var exception = Assert.Throws<SignerException>(() => _alice.Signer.CreateChannelAnnouncement2Nonces(
                                                           s_channelId, bad));
        Assert.Contains(message, exception.Message);
        return;

        ChannelAnnouncement2Payload Create(ShortChannelId? scid = null, ulong? capacity = null, TxId? fundingTxId = null,
                                           ChainHash? chain = null, CompactPubKey? key1 = null,
                                           CompactPubKey? key2 = null, byte[]? merkleRoot = null) =>
            ChannelAnnouncement2Payload.Create(chain ?? ChainConstants.Regtest, [], scid ?? s_scid,
                                               capacity ?? FundingSatoshis, good.NodeId1, good.NodeId2,
                                               key1 ?? good.BitcoinKey1, key2 ?? good.BitcoinKey2,
                                               merkleRoot ?? [], fundingTxId ?? s_fundingTxId, FundingOutputIndex);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Given_APrivateOrNonTaprootChannel_When_CreatingNonces_Then_Refused(bool announce, bool taproot)
    {
        // Arrange
        var channelId = new ChannelId(Enumerable.Repeat((byte)0xE3, 32).ToArray());
        _alice.Signer.RegisterChannel(channelId, _alice.SigningInfo(_bob) with
        {
            AnnounceChannel = announce,
            IsSimpleTaproot = taproot
        });

        // Act / Assert
        Assert.Throws<SignerException>(() => _alice.Signer.CreateChannelAnnouncement2Nonces(channelId,
                                                                                           BuildUnsigned()));
    }

    [Fact]
    public void Given_APendingSplice_When_CreatingNonces_Then_AllowedButSigningWaitsForTheLock()
    {
        // Arrange (NL-1131): a registered pending splice with the rotated funding keys (index 1 on both sides)
        var spliceTxId = new TxId(Enumerable.Repeat((byte)0xAD, 32).ToArray());
        var spliceScid = new ShortChannelId(520, 2, FundingOutputIndex);
        const ulong spliceSatoshis = FundingSatoshis + 100_000;
        var aliceKey = _alice.Signer.GetFundingPubKey(s_channelId, 1);
        var bobKey = _bob.Signer.GetFundingPubKey(s_channelId, 1);
        _alice.Signer.RegisterFunding(s_channelId,
                                      new ChannelFunding(spliceTxId, FundingOutputIndex, spliceSatoshis, aliceKey,
                                                         bobKey, 1, 0, 0, ChannelFundingKind.Splice,
                                                         ChannelFundingStatus.Pending));
        var aliceIsNode1 = ((ReadOnlySpan<byte>)_alice.NodeId).SequenceCompareTo(_bob.NodeId) < 0;
        var splice = aliceIsNode1
                         ? ChannelAnnouncement2Payload.Create(ChainConstants.Regtest, [], spliceScid, spliceSatoshis,
                                                              _alice.NodeId, _bob.NodeId, aliceKey, bobKey, [],
                                                              spliceTxId, FundingOutputIndex)
                         : ChannelAnnouncement2Payload.Create(ChainConstants.Regtest, [], spliceScid, spliceSatoshis,
                                                              _bob.NodeId, _alice.NodeId, bobKey, aliceKey, [],
                                                              spliceTxId, FundingOutputIndex);
        var bobNonces = _bob.Signer.CreateChannelAnnouncement2Nonces(s_channelId, BuildUnsigned());

        // Act: the nonces of a splice_locked are made for the splice before it locks
        var nonces = _alice.Signer.CreateChannelAnnouncement2Nonces(s_channelId, splice);

        // Assert: but nothing is signed for a funding that is not the channel's current one
        Assert.NotEqual(nonces.NodeNonce, bobNonces.NodeNonce);
        var exception = Assert.Throws<SignerException>(() => _alice.Signer.SignChannelAnnouncement2(
                                                           s_channelId, splice, bobNonces.NodeNonce,
                                                           bobNonces.BitcoinNonce));
        Assert.Contains("short channel id", exception.Message);

        // A splice's announcement with the wrong keys is refused at the nonces already
        var wrongKeys = ChannelAnnouncement2Payload.Create(ChainConstants.Regtest, [], spliceScid, spliceSatoshis,
                                                           splice.NodeId1, splice.NodeId2, _alice.FundingKey,
                                                           _bob.FundingKey, [], spliceTxId, FundingOutputIndex);
        Assert.Throws<SignerException>(() => _alice.Signer.CreateChannelAnnouncement2Nonces(s_channelId, wrongKeys));
    }

    [Fact]
    public void Given_TheNodeKey_When_SigningBip340_Then_TheXOnlyNodeIdVerifies()
    {
        // Arrange
        var hash = new Hash(Enumerable.Repeat((byte)0x42, 32).ToArray());

        // Act
        var signature = _alice.Signer.SignNodeMessageBip340(hash);

        // Assert
        var verifier = new GossipV2SignatureVerifier(_musig2);
        Assert.True(verifier.VerifyBip340(hash, signature, _alice.NodeId));
        Assert.False(verifier.VerifyBip340(hash, signature, _bob.NodeId));
        Assert.False(verifier.VerifyBip340(new Hash(new byte[32]), signature, _alice.NodeId));
    }

    private ChannelAnnouncement2Payload BuildUnsigned()
    {
        var aliceIsNode1 = ((ReadOnlySpan<byte>)_alice.NodeId).SequenceCompareTo(_bob.NodeId) < 0;
        var (node1, node2) = aliceIsNode1 ? (_alice, _bob) : (_bob, _alice);
        return ChannelAnnouncement2Payload.Create(ChainConstants.Regtest, [], s_scid, FundingSatoshis, node1.NodeId,
                                                  node2.NodeId, node1.FundingKey, node2.FundingKey, [], s_fundingTxId,
                                                  FundingOutputIndex);
    }

    private sealed class Party
    {
        private readonly Mock<ISecureKeyManager> _keyManager = new();

        public Party(string nodeKeyHex, string seedHex)
        {
            var nodePrivateKey = Convert.FromHexString(nodeKeyHex);
            using var nodeKey = new Key(nodePrivateKey);
            NodeId = nodeKey.PubKey.ToBytes();
            var seed = ExtKey.CreateFromSeed(Convert.FromHexString(seedHex));
            _keyManager.Setup(x => x.GetNodeKeyPair())
                       .Returns(() => new CryptoKeyPair(nodePrivateKey.ToArray(), NodeId));
            _keyManager.Setup(x => x.GetChannelKeyAtIndex(It.IsAny<uint>()))
                       .Returns((uint index) => seed.Derive((int)index, true).ToBytes());
            Signer = new LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(new Secp256K1Math()),
                                              NullLogger<LocalLightningSigner>.Instance,
                                              new NodeOptions { BitcoinNetwork = "regtest" }, _keyManager.Object,
                                              Mock.Of<IUtxoMemoryRepository>());
            FundingKey = Signer.GetChannelBasepoints(ChannelKeyIndex).FundingPubKey;
        }

        public CompactPubKey NodeId { get; }

        public CompactPubKey FundingKey { get; }

        public LocalLightningSigner Signer { get; }

        public ChannelSigningInfo SigningInfo(Party peer) =>
            // The signing info's funding amount is in msat (ChannelModel.GetSigningInfo passes the LightningMoney)
            new(s_fundingTxId, FundingOutputIndex, FundingSatoshis * 1_000, FundingKey, peer.FundingKey, ChannelKeyIndex)
            {
                RemoteNodeId = peer.NodeId,
                ShortChannelId = s_scid,
                AnnounceChannel = true,
                IsSimpleTaproot = true
            };

        public void Register(Party peer) => Signer.RegisterChannel(s_channelId, SigningInfo(peer));
    }
}
using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;
using Key = NBitcoin.Key;

/// <summary>
/// BOLT 7 plan G1-T2: <see cref="ILightningSigner.SignChannelAnnouncement"/> signs our half of a
/// <c>channel_announcement</c> (node key and funding key over the double-SHA256 of the bytes after the signatures) and
/// refuses any announcement that is not exactly our channel on our chain.
/// </summary>
public class LocalLightningSignerChannelAnnouncementTests
{
    private const uint ChannelKeyIndex = 3;
    private const ushort FundingOutputIndex = 1;

    private static readonly byte[] s_nodePrivateKey =
        Convert.FromHexString("1111111111111111111111111111111111111111111111111111111111111111");

    private static readonly ExtKey s_seed =
        ExtKey.CreateFromSeed(Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"));

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0xC1, 32).ToArray());
    private static readonly ShortChannelId s_scid = new(812_345, 678, FundingOutputIndex);

    private readonly Mock<ISecureKeyManager> _keyManager = new();
    private readonly CompactPubKey _ourNodeId;
    private readonly Key _peerNodeKey = new();
    private readonly Key _peerFundingKey = new();

    public LocalLightningSignerChannelAnnouncementTests()
    {
        using var nodeKey = new Key(s_nodePrivateKey);
        _ourNodeId = nodeKey.PubKey.ToBytes();
        _keyManager.Setup(x => x.GetNodeKeyPair())
                   .Returns(() => new CryptoKeyPair(s_nodePrivateKey.ToArray(), _ourNodeId));
        _keyManager.Setup(x => x.GetChannelKeyAtIndex(It.IsAny<uint>()))
                   .Returns((uint index) => s_seed.Derive((int)index, true).ToBytes());
    }

    [Fact]
    public void Given_OurChannel_When_SigningTheAnnouncement_Then_BothSignaturesCoverTheDoubleSha256()
    {
        // Arrange
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        var announcement = BuildAnnouncement(ourFundingKey);
        var hash = SHA256.HashData(SHA256.HashData(announcement));

        // Act
        var signatures = signer.SignChannelAnnouncement(s_channelId, announcement, s_scid);

        // Assert: node signature with the node key, bitcoin signature with our funding key, both low-S
        Assert.True(signer.VerifyNodeMessage(hash, signatures.NodeSignature, _ourNodeId));
        Assert.True(signer.VerifyNodeMessage(hash, signatures.BitcoinSignature, ourFundingKey));
        Assert.False(signer.VerifyNodeMessage(hash, signatures.BitcoinSignature, _ourNodeId));
        Assert.True(ParseCompact(signatures.NodeSignature).IsLowS);
        Assert.True(ParseCompact(signatures.BitcoinSignature).IsLowS);

        // Deterministic (RFC 6979)
        Assert.Equal(signatures, signer.SignChannelAnnouncement(s_channelId, announcement, s_scid));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_BothHalves_When_Assembled_Then_EveryBolt7SignatureOfTheAnnouncementVerifies(bool weAreNode1)
    {
        // Arrange: a peer whose node id sorts after (or before) ours
        var peerNodeKey = FindPeerKey(weAreNode1);
        var (signer, ourFundingKey) = CreateRegisteredSigner(peerNodeKey.PubKey.ToBytes());
        var announcement = BuildAnnouncement(ourFundingKey, peerNodeId: peerNodeKey.PubKey.ToBytes(),
                                             trailing: [0xDE, 0xAD, 0xBE, 0xEF]);
        var hash = SHA256.HashData(SHA256.HashData(announcement));

        // Act: our half from the signer, the peer's half with its keys
        var ours = signer.SignChannelAnnouncement(s_channelId, announcement, s_scid);
        var peerNodeSignature = peerNodeKey.Sign(new uint256(hash), false).MakeCanonical().ToCompact();
        var peerBitcoinSignature = _peerFundingKey.Sign(new uint256(hash), false).MakeCanonical().ToCompact();
        byte[] full =
        [
            .. weAreNode1 ? ours.NodeSignature.Value : peerNodeSignature,
            .. weAreNode1 ? peerNodeSignature : ours.NodeSignature.Value,
            .. weAreNode1 ? ours.BitcoinSignature.Value : peerBitcoinSignature,
            .. weAreNode1 ? peerBitcoinSignature : ours.BitcoinSignature.Value,
            .. announcement
        ];

        // Assert: BOLT 7 receiver checks on the assembled message (the hash of the bytes from offset 256)
        var signedHash = SHA256.HashData(SHA256.HashData(full.AsSpan(256)));
        Assert.Equal(hash, signedHash);
        var (nodeId1, nodeId2, bitcoinKey1, bitcoinKey2) = ReadKeys(announcement);
        Assert.True(signer.VerifyNodeMessage(signedHash, full.AsSpan(0, 64).ToArray(), nodeId1));
        Assert.True(signer.VerifyNodeMessage(signedHash, full.AsSpan(64, 64).ToArray(), nodeId2));
        Assert.True(signer.VerifyNodeMessage(signedHash, full.AsSpan(128, 64).ToArray(), bitcoinKey1));
        Assert.True(signer.VerifyNodeMessage(signedHash, full.AsSpan(192, 64).ToArray(), bitcoinKey2));
        Assert.Equal(weAreNode1 ? _ourNodeId : peerNodeKey.PubKey.ToBytes(), (byte[])nodeId1);
    }

    [Fact]
    public void Given_ForeignFundingKey_When_Signing_Then_Refused()
    {
        // Arrange: the slot of our bitcoin key holds another key
        var (signer, _) = CreateRegisteredSigner();
        var announcement = BuildAnnouncement(new Key().PubKey.ToBytes());

        // Act & Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(
                                                           s_channelId, announcement, s_scid));
        Assert.Contains("our funding key", exception.Message);
    }

    [Fact]
    public void Given_PeersFundingKeySwapped_When_Signing_Then_Refused()
    {
        // Arrange
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        var announcement = BuildAnnouncement(ourFundingKey, peerFundingKey: new Key().PubKey.ToBytes());

        // Act & Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(
                                                           s_channelId, announcement, s_scid));
        Assert.Contains("peer's funding key", exception.Message);
    }

    [Fact]
    public void Given_ForeignNodeId_When_Signing_Then_Refused()
    {
        // Arrange: neither node id is ours
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        var announcement = BuildAnnouncement(ourFundingKey, ourNodeId: new Key().PubKey.ToBytes());

        // Act & Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(
                                                           s_channelId, announcement, s_scid));
        Assert.Contains("our node id", exception.Message);
    }

    [Fact]
    public void Given_AnotherPeer_When_Signing_Then_Refused()
    {
        // Arrange: the other node is not the channel's peer
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        var announcement = BuildAnnouncement(ourFundingKey, peerNodeId: new Key().PubKey.ToBytes());

        // Act & Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(
                                                           s_channelId, announcement, s_scid));
        Assert.Contains("another peer", exception.Message);
    }

    [Fact]
    public void Given_NodeIdsNotAscending_When_Signing_Then_Refused()
    {
        // Arrange
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        var announcement = BuildAnnouncement(ourFundingKey, sortNodeIds: false);

        // Act & Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(
                                                           s_channelId, announcement, s_scid));
        Assert.Contains("ascending", exception.Message);
    }

    [Fact]
    public void Given_ForeignShortChannelIdInTheBytes_When_Signing_Then_Refused()
    {
        // Arrange: the caller names our SCID but the bytes announce another one
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        var announcement = BuildAnnouncement(ourFundingKey, scid: new ShortChannelId(812_346, 678, FundingOutputIndex));

        // Act & Assert
        Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(s_channelId, announcement, s_scid));
    }

    [Fact]
    public void Given_ShortChannelIdOtherThanTheChannels_When_Signing_Then_Refused()
    {
        // Arrange: bytes and argument agree, but the channel's real SCID is another one
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        var other = new ShortChannelId(900_000, 1, FundingOutputIndex);
        var announcement = BuildAnnouncement(ourFundingKey, scid: other);

        // Act & Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(
                                                           s_channelId, announcement, other));
        Assert.Contains("the channel's short channel id is", exception.Message);
    }

    [Fact]
    public void Given_ShortChannelIdOfAnotherOutput_When_Signing_Then_Refused()
    {
        // Arrange
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        var wrongOutput = new ShortChannelId(812_345, 678, 0);
        var announcement = BuildAnnouncement(ourFundingKey, scid: wrongOutput);

        // Act & Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(
                                                           s_channelId, announcement, wrongOutput));
        Assert.Contains("funding output index", exception.Message);
    }

    [Fact]
    public void Given_AnotherChain_When_Signing_Then_Refused()
    {
        // Arrange: a regtest signer asked to sign for mainnet
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        var announcement = BuildAnnouncement(ourFundingKey, chainHash: ChainConstants.Main);

        // Act & Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(
                                                           s_channelId, announcement, s_scid));
        Assert.Contains("another chain", exception.Message);
    }

    [Fact]
    public void Given_TruncatedAnnouncement_When_Signing_Then_Refused()
    {
        // Arrange: one byte short of bitcoin_key_2, and a features length running past the end
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        var announcement = BuildAnnouncement(ourFundingKey);
        var overflowing = announcement.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(overflowing, 0xFFFF);

        // Act & Assert
        Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(s_channelId, announcement[..^1], s_scid));
        Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(s_channelId, overflowing, s_scid));
        Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(s_channelId, new byte[1], s_scid));
    }

    [Fact]
    public void Given_ShortChannelIdNotKnownYet_When_Signing_Then_RefusedUntilRegisteredAgain()
    {
        // Arrange: registered at funding_created, before the funding confirmed
        var signer = CreateSigner();
        var ourFundingKey = signer.GetChannelBasepoints(ChannelKeyIndex).FundingPubKey;
        signer.RegisterChannel(s_channelId, SigningInfo(ourFundingKey) with { ShortChannelId = null });
        var announcement = BuildAnnouncement(ourFundingKey);

        // Act
        var beforeConfirmation = Record.Exception(() => signer.SignChannelAnnouncement(s_channelId, announcement,
                                                                                       s_scid));
        signer.RegisterChannel(s_channelId, SigningInfo(ourFundingKey));
        var afterConfirmation = signer.SignChannelAnnouncement(s_channelId, announcement, s_scid);

        // Assert
        Assert.IsType<SignerException>(beforeConfirmation);
        Assert.NotNull(afterConfirmation);
    }

    [Fact]
    public void Given_RegisteredAgainWithOtherKeys_When_Signing_Then_TheFirstRegistrationIsKept()
    {
        // Arrange: a registration with another funding key must not replace the channel's keys
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        var rejected = Record.Exception(() => signer.RegisterChannel(s_channelId, SigningInfo(ourFundingKey) with
        {
            RemoteFundingPubKey = new Key().PubKey.ToBytes()
        }));

        // Act
        var signatures = signer.SignChannelAnnouncement(s_channelId, BuildAnnouncement(ourFundingKey), s_scid);

        // Assert: the caller learns about the rejected registration, and the channel still signs with its own keys
        Assert.IsType<SignerException>(rejected);
        Assert.NotNull(signatures);
    }

    [Fact]
    public void Given_PrivateChannel_When_Signing_Then_Refused()
    {
        // Arrange: BOLT 7 forbids announcement_signatures without announce_channel
        var signer = CreateSigner();
        var ourFundingKey = signer.GetChannelBasepoints(ChannelKeyIndex).FundingPubKey;
        signer.RegisterChannel(s_channelId, SigningInfo(ourFundingKey) with { AnnounceChannel = false });

        // Act & Assert
        var exception = Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(
                                                           s_channelId, BuildAnnouncement(ourFundingKey), s_scid));
        Assert.Contains("private channel", exception.Message);
    }

    [Fact]
    public void Given_DataLoss_When_Signing_Then_Refused()
    {
        // Arrange
        var (signer, ourFundingKey) = CreateRegisteredSigner();
        signer.MarkDataLoss(s_channelId);

        // Act & Assert
        Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(s_channelId,
                                                                           BuildAnnouncement(ourFundingKey), s_scid));
    }

    [Fact]
    public void Given_UnknownChannel_When_Signing_Then_Refused()
    {
        // Arrange
        var signer = CreateSigner();
        var ourFundingKey = signer.GetChannelBasepoints(ChannelKeyIndex).FundingPubKey;

        // Act & Assert
        Assert.Throws<SignerException>(() => signer.SignChannelAnnouncement(s_channelId,
                                                                           BuildAnnouncement(ourFundingKey), s_scid));
    }

    [Fact]
    public void Given_PersistedChannel_When_SigningWithASource_Then_ThePersistedShortChannelIdIsUsed()
    {
        // Arrange: registered before the confirmation; the source (the database) knows the real SCID now
        var signerWithoutSource = CreateSigner();
        var ourFundingKey = signerWithoutSource.GetChannelBasepoints(ChannelKeyIndex).FundingPubKey;
        var persisted = SigningInfo(ourFundingKey);
        var source = new Mock<IChannelSigningInfoSource>();
        source.Setup(x => x.TryGet(s_channelId, out persisted)).Returns(true);
        var signer = CreateSigner(source.Object);
        signer.RegisterChannel(s_channelId, persisted with { ShortChannelId = null });

        // Act
        var signatures = signer.SignChannelAnnouncement(s_channelId, BuildAnnouncement(ourFundingKey), s_scid);

        // Assert
        Assert.NotNull(signatures);
        source.Verify(x => x.TryGet(s_channelId, out persisted), Times.Once);
    }

    private (LocalLightningSigner Signer, CompactPubKey OurFundingKey) CreateRegisteredSigner(
        CompactPubKey? peerNodeId = null)
    {
        var signer = CreateSigner();
        var ourFundingKey = signer.GetChannelBasepoints(ChannelKeyIndex).FundingPubKey;
        signer.RegisterChannel(s_channelId, SigningInfo(ourFundingKey, peerNodeId));
        return (signer, ourFundingKey);
    }

    private ChannelSigningInfo SigningInfo(CompactPubKey ourFundingKey, CompactPubKey? peerNodeId = null) =>
        new(new TxId(Enumerable.Repeat((byte)0xF0, 32).ToArray()), FundingOutputIndex, 1_000_000_000,
            ourFundingKey, _peerFundingKey.PubKey.ToBytes(), ChannelKeyIndex)
        {
            RemoteNodeId = peerNodeId ?? _peerNodeKey.PubKey.ToBytes(),
            ShortChannelId = s_scid,
            AnnounceChannel = true
        };

    private LocalLightningSigner CreateSigner(IChannelSigningInfoSource? source = null) =>
        new(new FundingOutputBuilder(), new KeyDerivationService(new Secp256K1Math()),
            NullLogger<LocalLightningSigner>.Instance, new NodeOptions { BitcoinNetwork = "regtest" },
            _keyManager.Object, Mock.Of<IUtxoMemoryRepository>(), source);

    /// <summary>
    /// The unsigned <c>channel_announcement</c> (the payload after the four signatures): <c>len</c>, <c>features</c>,
    /// <c>chain_hash</c>, <c>short_channel_id</c>, <c>node_id_1</c>, <c>node_id_2</c>, <c>bitcoin_key_1</c>,
    /// <c>bitcoin_key_2</c>, then any unknown trailing bytes.
    /// </summary>
    private byte[] BuildAnnouncement(CompactPubKey ourFundingKey, CompactPubKey? ourNodeId = null,
                                     CompactPubKey? peerNodeId = null, CompactPubKey? peerFundingKey = null,
                                     ShortChannelId? scid = null, ChainHash? chainHash = null,
                                     bool sortNodeIds = true, byte[]? trailing = null)
    {
        var us = ourNodeId ?? _ourNodeId;
        var peer = peerNodeId ?? _peerNodeKey.PubKey.ToBytes();
        var weAreNode1 = ((ReadOnlySpan<byte>)us).SequenceCompareTo(peer) < 0;
        if (!sortNodeIds)
            weAreNode1 = !weAreNode1;

        var ourBitcoinKey = (byte[])ourFundingKey;
        var peerBitcoinKey = (byte[])(peerFundingKey ?? _peerFundingKey.PubKey.ToBytes());
        byte[] features = [0x01, 0x00];
        return
        [
            0x00, (byte)features.Length, .. features,
            .. (byte[])(chainHash ?? ChainConstants.Regtest),
            .. (byte[])(scid ?? s_scid),
            .. weAreNode1 ? (byte[])us : (byte[])peer,
            .. weAreNode1 ? (byte[])peer : (byte[])us,
            .. weAreNode1 ? ourBitcoinKey : peerBitcoinKey,
            .. weAreNode1 ? peerBitcoinKey : ourBitcoinKey,
            .. trailing ?? []
        ];
    }

    private static (CompactPubKey, CompactPubKey, CompactPubKey, CompactPubKey) ReadKeys(byte[] announcement)
    {
        var offset = 2 + BinaryPrimitives.ReadUInt16BigEndian(announcement) + 32 + 8;
        return (announcement[offset..(offset + 33)], announcement[(offset + 33)..(offset + 66)],
                announcement[(offset + 66)..(offset + 99)], announcement[(offset + 99)..(offset + 132)]);
    }

    private Key FindPeerKey(bool peerSortsAfterUs)
    {
        while (true)
        {
            var key = new Key();
            var after = ((ReadOnlySpan<byte>)_ourNodeId).SequenceCompareTo(key.PubKey.ToBytes()) < 0;
            if (after == peerSortsAfterUs)
                return key;
        }
    }

    private static ECDSASignature ParseCompact(CompactSignature signature)
    {
        Assert.True(ECDSASignature.TryParseFromCompact(signature, out var parsed));
        return parsed;
    }
}
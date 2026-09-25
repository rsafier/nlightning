using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Crypto.Ciphers;

public class SphinxServiceTests
{
    private static readonly byte[] s_associatedData = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private readonly Ecdh _ecdh = new();
    private readonly SphinxService _sphinxService = new(new Ecdh(), new Secp256K1Math());

    public static TheoryData<int, int> RoundTripCases
    {
        get
        {
            var data = new TheoryData<int, int>();
            foreach (var length in new[] { OnionConstants.HopPayloadsLength, 900, 32768 })
            {
                foreach (var hopCount in new[] { 1, 2, 3, 5, 8, 13, 20 })
                    data.Add(hopCount, length);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Given_RandomRoute_When_ConstructingAndPeeling_Then_EveryHopRecoversItsPayload(int hopCount,
        int hopPayloadsLength)
    {
        // Arrange
        var random = new Random(hopCount * 31 + hopPayloadsLength);
        var nodeKeys = Enumerable.Range(0, hopCount).Select(_ => _ecdh.GenerateKeyPair()).ToList();
        var maxPayload = Math.Min(hopPayloadsLength / hopCount - OnionConstants.HmacLength - 3, 600);
        var hops = nodeKeys.Select(k =>
        {
            var payload = new byte[random.Next(2, maxPayload + 1)];
            random.NextBytes(payload);
            return new OnionHop(k.CompactPubKey, payload);
        }).ToList();
        var sessionKey = _ecdh.GenerateKeyPair().PrivKey;
        var expectedSecrets = _sphinxService.ComputeSharedSecrets(hops.Select(h => h.NodeId).ToList(), sessionKey);

        // Act
        var packet = _sphinxService.Construct(hops, sessionKey, s_associatedData, hopPayloadsLength);

        // Assert
        Assert.Equal(OnionConstants.PacketOverheadLength + hopPayloadsLength, packet.Length);
        OnionPacket? current = packet;
        for (var i = 0; i < hopCount; i++)
        {
            Assert.NotNull(current);
            var peeled = _sphinxService.Peel(current.Value, s_associatedData, nodeKeys[i].PrivKey);

            Assert.Equal(hops[i].Payload.ToArray(), peeled.Payload.ToArray());
            Assert.Equal(expectedSecrets[i], peeled.SharedSecret);
            Assert.Equal(i == hopCount - 1, peeled.IsFinal);
            Assert.Equal(peeled.IsFinal, peeled.NextPacket is null);
            if (peeled.NextPacket is not null)
                Assert.Equal(hopPayloadsLength, peeled.NextPacket.Value.HopPayloadsLength);

            current = peeled.NextPacket;
        }
    }

    [Fact]
    public void Given_PayloadFillingAllSpace_When_ConstructingAndPeeling_Then_Succeeds()
    {
        // Arrange: 3-byte bigsize + payload + 32-byte hmac == 1300
        var nodeKey = _ecdh.GenerateKeyPair();
        var payload = RandomNumberGenerator.GetBytes(OnionConstants.HopPayloadsLength - 3 - OnionConstants.HmacLength);
        var hops = new List<OnionHop> { new(nodeKey.CompactPubKey, payload) };

        // Act
        var packet = _sphinxService.Construct(hops, _ecdh.GenerateKeyPair().PrivKey, s_associatedData);
        var peeled = _sphinxService.Peel(packet, s_associatedData, nodeKey.PrivKey);

        // Assert
        Assert.True(peeled.IsFinal);
        Assert.Equal(payload, peeled.Payload.ToArray());
    }

    [Fact]
    public void Given_PayloadsTooLarge_When_Constructing_Then_ThrowsArgumentException()
    {
        // Arrange: one byte too many
        var nodeKey = _ecdh.GenerateKeyPair();
        var payload = new byte[OnionConstants.HopPayloadsLength - 3 - OnionConstants.HmacLength + 1];
        var hops = new List<OnionHop> { new(nodeKey.CompactPubKey, payload) };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sphinxService.Construct(hops, _ecdh.GenerateKeyPair().PrivKey,
                                                                        s_associatedData));
    }

    [Fact]
    public void Given_EmptyRoute_When_Constructing_Then_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sphinxService.Construct([], _ecdh.GenerateKeyPair().PrivKey,
                                                                        s_associatedData));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Given_PayloadShorterThanTwoBytes_When_Constructing_Then_ThrowsArgumentException(int length)
    {
        // Arrange
        var hops = new List<OnionHop> { new(_ecdh.GenerateKeyPair().CompactPubKey, new byte[length]) };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sphinxService.Construct(hops, _ecdh.GenerateKeyPair().PrivKey,
                                                                        s_associatedData));
    }

    [Fact]
    public void Given_NodeIdNotOnCurve_When_Constructing_Then_ThrowsArgumentException()
    {
        // Arrange
        var badNodeId = new CompactPubKey(Convert.FromHexString(
                                              "02ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"));
        var hops = new List<OnionHop> { new(badNodeId, new byte[10]) };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _sphinxService.Construct(hops, _ecdh.GenerateKeyPair().PrivKey,
                                                                        s_associatedData));
    }

    [Fact]
    public void Given_Packet_When_Peeling_Then_InputPacketIsNotMutated()
    {
        // Arrange
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var before = packet.ToBytes();

        // Act
        var peeled = _sphinxService.Peel(packet, s_associatedData, nodeKeys[0].PrivKey);

        // Assert
        Assert.Equal(before, packet.ToBytes());
        Assert.NotNull(peeled.NextPacket);
        Assert.NotEqual(packet, peeled.NextPacket.Value);
    }

    [Fact]
    public void Given_DifferentAssociatedData_When_Peeling_Then_ThrowsInvalidOnionHmac()
    {
        // Arrange
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var otherAssociatedData = Enumerable.Repeat((byte)0x43, 32).ToArray();

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(packet, otherAssociatedData,
                                                                                 nodeKeys[0].PrivKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionHmac, exception.FailureCode);
        Assert.Equal(SHA256.HashData(packet.ToBytes()), exception.FailureData?.ToArray());
    }

    [Fact]
    public void Given_WrongNodeKey_When_Peeling_Then_ThrowsInvalidOnionHmac()
    {
        // Arrange
        var (packet, nodeKeys) = BuildTwoHopPacket();

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(packet, s_associatedData,
                                                                                 nodeKeys[1].PrivKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionHmac, exception.FailureCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(34)]
    [InlineData(700)]
    [InlineData(1333)]
    [InlineData(1334)]
    [InlineData(1365)]
    public void Given_FlippedBit_When_Peeling_Then_ThrowsInvalidOnionHmacOrKey(int byteIndex)
    {
        // Arrange
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var bytes = packet.ToBytes();
        bytes[byteIndex] ^= 0x01;
        var tampered = new OnionPacket(bytes);

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(tampered, s_associatedData,
                                                                                 nodeKeys[0].PrivKey));

        // Assert: flipping the key's lowest bit may yield an invalid point (invalid_onion_key) or another valid point
        // (invalid_onion_hmac); both are BADONION
        Assert.Contains(exception.FailureCode, new[] { FailureCode.InvalidOnionHmac, FailureCode.InvalidOnionKey });
        Assert.Equal(SHA256.HashData(bytes), exception.FailureData?.ToArray());
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0xFF)]
    public void Given_UnknownVersion_When_Peeling_Then_ThrowsInvalidOnionVersion(byte version)
    {
        // Arrange
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var bytes = packet.ToBytes();
        bytes[0] = version;
        var tampered = new OnionPacket(bytes);

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(tampered, s_associatedData,
                                                                                 nodeKeys[0].PrivKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionVersion, exception.FailureCode);
        Assert.Equal(SHA256.HashData(bytes), exception.FailureData?.ToArray());
    }

    [Theory]
    [InlineData("04")]
    [InlineData("00")]
    public void Given_InvalidKeyPrefix_When_Peeling_Then_ThrowsInvalidOnionKey(string prefix)
    {
        // Arrange
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var bytes = packet.ToBytes();
        bytes[1] = Convert.FromHexString(prefix)[0];
        var tampered = new OnionPacket(bytes);

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(tampered, s_associatedData,
                                                                                 nodeKeys[0].PrivKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionKey, exception.FailureCode);
        Assert.Equal(SHA256.HashData(bytes), exception.FailureData?.ToArray());
    }

    [Fact]
    public void Given_KeyNotOnCurve_When_Peeling_Then_ThrowsInvalidOnionKey()
    {
        // Arrange
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var bytes = packet.ToBytes();
        bytes.AsSpan(2, 32).Fill(0xFF);
        var tampered = new OnionPacket(bytes);

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(tampered, s_associatedData,
                                                                                 nodeKeys[0].PrivKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionKey, exception.FailureCode);
    }

    public static TheoryData<string> BadFramings => new()
    {
        "00", // length 0 (legacy)
        "01", // length 1 (reserved)
        "fd00fc", // non-canonical bigsize
        "fd0514", // 1300 > available
        "fd04f2", // 1266 + 3 + 32 = 1301 > 1300
        "fe00010000", // 65536
        "ff0000000100000000" // 2^32
    };

    [Theory]
    [MemberData(nameof(BadFramings))]
    public void Given_ValidHmacButBadFraming_When_Peeling_Then_ThrowsInvalidOnionPayload(string prefixHex)
    {
        // Arrange
        var nodeKey = _ecdh.GenerateKeyPair();
        var plaintext = new byte[OnionConstants.HopPayloadsLength];
        Convert.FromHexString(prefixHex).CopyTo(plaintext, 0);
        var packet = CraftSingleHopPacket(nodeKey.CompactPubKey, plaintext, out var expectedSharedSecret);

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(packet, s_associatedData,
                                                                                 nodeKey.PrivKey));

        // Assert: invalid_onion_payload is not BADONION, so the caller needs the shared secret to encrypt it
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00 }, exception.FailureData?.ToArray());
        Assert.NotNull(exception.SharedSecret);
        Assert.Equal(expectedSharedSecret, (byte[])exception.SharedSecret.Value);
    }

    [Theory]
    [MemberData(nameof(BadFramings))]
    public void Given_ValidHmacButBadFramingInsideBlindedRoute_When_Peeling_Then_ThrowsInvalidOnionBlinding(
        string prefixHex)
    {
        // Arrange
        var nodeKey = _ecdh.GenerateKeyPair();
        var pathKey = _ecdh.GenerateKeyPair();
        var plaintext = new byte[OnionConstants.HopPayloadsLength];
        Convert.FromHexString(prefixHex).CopyTo(plaintext, 0);
        var packet = CraftSingleHopPacket(BlindNodeId(nodeKey.CompactPubKey, pathKey.PrivKey), plaintext, out _);

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(packet, s_associatedData,
                                                                                 nodeKey.PrivKey,
                                                                                 pathKey.CompactPubKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, exception.FailureCode);
        Assert.Equal(SHA256.HashData(packet.ToBytes()), exception.FailureData?.ToArray());
        Assert.Null(exception.SharedSecret);
    }

    public static TheoryData<string> BadOnionMessageFramings => new()
    {
        "fd00fc", // non-canonical bigsize
        "fd0514", // 1300 > available
        "fd04f2" // 1266 + 3 + 32 = 1301 > 1300
    };

    [Theory]
    [MemberData(nameof(BadOnionMessageFramings))]
    public void Given_BadFramingInOnionMessageWithPathKey_When_Peeling_Then_ThrowsInvalidOnionPayload(
        string prefixHex)
    {
        // Arrange: onion messages never return errors, so no invalid_onion_blinding remapping is applied
        var nodeKey = _ecdh.GenerateKeyPair();
        var pathKey = _ecdh.GenerateKeyPair();
        var plaintext = new byte[OnionConstants.HopPayloadsLength];
        Convert.FromHexString(prefixHex).CopyTo(plaintext, 0);
        var packet = CraftSingleHopPacket(BlindNodeId(nodeKey.CompactPubKey, pathKey.PrivKey), plaintext, out _,
                                          []);

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(packet, [], nodeKey.PrivKey,
                                                                                 pathKey.CompactPubKey,
                                                                                 OnionPacketKind.OnionMessage));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
    }

    [Fact]
    public void Given_LargestValidFraming_When_Peeling_Then_Succeeds()
    {
        // Arrange: fd04f1 = 1265; 3 + 1265 + 32 = 1300
        var nodeKey = _ecdh.GenerateKeyPair();
        var plaintext = new byte[OnionConstants.HopPayloadsLength];
        Convert.FromHexString("fd04f1").CopyTo(plaintext, 0);
        var packet = CraftSingleHopPacket(nodeKey.CompactPubKey, plaintext, out _);

        // Act
        var peeled = _sphinxService.Peel(packet, s_associatedData, nodeKey.PrivKey);

        // Assert
        Assert.True(peeled.IsFinal);
        Assert.Equal(1265, peeled.Payload.Length);
        Assert.Null(peeled.PathKeySharedSecret);
    }

    [Fact]
    public void Given_PathKeyNotOnCurve_When_Peeling_Then_ThrowsInvalidOnionBlinding()
    {
        // Arrange
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var pathKey = new CompactPubKey(Convert.FromHexString(
                                            "02ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"));

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(packet, s_associatedData,
                                                                                 nodeKeys[0].PrivKey, pathKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, exception.FailureCode);
        Assert.Equal(SHA256.HashData(packet.ToBytes()), exception.FailureData?.ToArray());
    }

    [Fact]
    public void Given_UnexpectedPathKey_When_Peeling_Then_ThrowsInvalidOnionBlinding()
    {
        // Arrange: BOLT 4 "Returning Errors": with a path_key in update_add_htlc every failure (here a bad HMAC,
        // since the onion is not encrypted to the blinded key) MUST be reported as invalid_onion_blinding
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var pathKey = _ecdh.GenerateKeyPair().CompactPubKey;

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(packet, s_associatedData,
                                                                                 nodeKeys[0].PrivKey, pathKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, exception.FailureCode);
        Assert.Equal(SHA256.HashData(packet.ToBytes()), exception.FailureData?.ToArray());
        var inner = Assert.IsType<OnionException>(exception.InnerException);
        Assert.Equal(FailureCode.InvalidOnionHmac, inner.FailureCode);
    }

    [Fact]
    public void Given_UnknownVersionInsideBlindedRoute_When_Peeling_Then_ThrowsInvalidOnionBlinding()
    {
        // Arrange
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var bytes = packet.ToBytes();
        bytes[0] = 0x01;
        var tampered = new OnionPacket(bytes);

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(tampered, s_associatedData,
                                                                                 nodeKeys[0].PrivKey,
                                                                                 _ecdh.GenerateKeyPair()
                                                                                      .CompactPubKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, exception.FailureCode);
        Assert.Equal(SHA256.HashData(bytes), exception.FailureData?.ToArray());
    }

    [Fact]
    public void Given_UnexpectedPathKeyOnOnionMessage_When_Peeling_Then_ThrowsInvalidOnionHmac()
    {
        // Arrange: the invalid_onion_blinding remapping is payment-only
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var pathKey = _ecdh.GenerateKeyPair().CompactPubKey;

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(packet, s_associatedData,
                                                                                 nodeKeys[0].PrivKey, pathKey,
                                                                                 OnionPacketKind.OnionMessage));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionHmac, exception.FailureCode);
    }

    [Fact]
    public void Given_BlindedHop_When_Peeling_Then_PathKeySharedSecretIsExposed()
    {
        // Arrange
        var nodeKey = _ecdh.GenerateKeyPair();
        var pathKey = _ecdh.GenerateKeyPair();
        var hops = new List<OnionHop>
        {
            new(BlindNodeId(nodeKey.CompactPubKey, pathKey.PrivKey), Enumerable.Repeat((byte)0x01, 20).ToArray())
        };
        var packet = _sphinxService.Construct(hops, _ecdh.GenerateKeyPair().PrivKey, s_associatedData);
        var expected = new byte[32];
        _ecdh.SecP256K1Dh(pathKey.PrivKey, nodeKey.CompactPubKey, expected);

        // Act
        var peeled = _sphinxService.Peel(packet, s_associatedData, nodeKey.PrivKey, pathKey.CompactPubKey);

        // Assert
        Assert.True(peeled.IsFinal);
        Assert.NotNull(peeled.PathKeySharedSecret);
        Assert.Equal(expected, (byte[])peeled.PathKeySharedSecret.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Given_OnionMessagePayloadShorterThanTwoBytes_When_ConstructingAndPeeling_Then_Succeeds(int length)
    {
        // Arrange: onion messages have no legacy length, so 0 means an empty onionmsg_payload
        var nodeKeys = new List<CryptoKeyPair> { _ecdh.GenerateKeyPair(), _ecdh.GenerateKeyPair() };
        var hops = nodeKeys.Select(k => new OnionHop(k.CompactPubKey, new byte[length])).ToList();

        // Act
        var packet = _sphinxService.Construct(hops, _ecdh.GenerateKeyPair().PrivKey, [],
                                              packetKind: OnionPacketKind.OnionMessage);
        var first = _sphinxService.Peel(packet, [], nodeKeys[0].PrivKey, packetKind: OnionPacketKind.OnionMessage);
        var second = _sphinxService.Peel(first.NextPacket!.Value, [], nodeKeys[1].PrivKey,
                                         packetKind: OnionPacketKind.OnionMessage);

        // Assert
        Assert.Equal(length, first.Payload.Length);
        Assert.False(first.IsFinal);
        Assert.Equal(length, second.Payload.Length);
        Assert.True(second.IsFinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Given_PaymentOnionWithShortPayload_When_Peeling_Then_ThrowsInvalidOnionPayload(int length)
    {
        // Arrange: the same bytes that are a valid onion message are an invalid payment onion
        var nodeKey = _ecdh.GenerateKeyPair();
        var hops = new List<OnionHop> { new(nodeKey.CompactPubKey, new byte[length]) };
        var packet = _sphinxService.Construct(hops, _ecdh.GenerateKeyPair().PrivKey, s_associatedData,
                                              packetKind: OnionPacketKind.OnionMessage);

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(packet, s_associatedData,
                                                                                 nodeKey.PrivKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
    }

    [Fact]
    public void Given_Route_When_ConstructingWithSharedSecrets_Then_PacketAndSecretsMatchSeparateCalls()
    {
        // Arrange
        var nodeKeys = Enumerable.Range(0, 3).Select(_ => _ecdh.GenerateKeyPair()).ToList();
        var hops = nodeKeys.Select(k => new OnionHop(k.CompactPubKey, new byte[] { 0x02, 0x01, 0x01 })).ToList();
        var sessionKey = _ecdh.GenerateKeyPair().PrivKey;

        // Act
        var constructed = _sphinxService.ConstructWithSharedSecrets(hops, sessionKey, s_associatedData);

        // Assert
        Assert.Equal(_sphinxService.Construct(hops, sessionKey, s_associatedData), constructed.Packet);
        Assert.Equal(_sphinxService.ComputeSharedSecrets(nodeKeys.Select(k => k.CompactPubKey).ToList(), sessionKey),
                     constructed.SharedSecrets);
        OnionPacket? current = constructed.Packet;
        for (var i = 0; i < nodeKeys.Count; i++)
        {
            var peeled = _sphinxService.Peel(current!.Value, s_associatedData, nodeKeys[i].PrivKey);
            Assert.Equal(constructed.SharedSecrets[i], peeled.SharedSecret);
            current = peeled.NextPacket;
        }
    }

    [Fact]
    public void Given_SecureKeyManager_When_PeelingWithoutExplicitKey_Then_UsesNodeKey()
    {
        // Arrange
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(m => m.GetNodeKeyPair()).Returns(nodeKeys[0]);
        var service = new SphinxService(new Ecdh(), new Secp256K1Math(), keyManager.Object);

        // Act
        var peeled = service.PeelAsLocalNode(packet, s_associatedData);

        // Assert: the key manager hands out a fresh copy of the node key, which is wiped after the peel
        Assert.False(peeled.IsFinal);
        keyManager.Verify(m => m.GetNodeKeyPair(), Times.Once);
        Assert.All(nodeKeys[0].PrivKey.Value, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Given_NoSecureKeyManager_When_PeelingWithoutExplicitKey_Then_ThrowsInvalidOperationException()
    {
        // Arrange
        var (packet, _) = BuildTwoHopPacket();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _sphinxService.PeelAsLocalNode(packet, s_associatedData));
    }

    private (OnionPacket Packet, List<CryptoKeyPair> NodeKeys) BuildTwoHopPacket()
    {
        var nodeKeys = new List<CryptoKeyPair> { _ecdh.GenerateKeyPair(), _ecdh.GenerateKeyPair() };
        var hops = nodeKeys.Select((k, i) => new OnionHop(k.CompactPubKey, Enumerable.Repeat((byte)(i + 1), 20)
                                                                                    .ToArray()))
                           .ToList();
        var packet = _sphinxService.Construct(hops, _ecdh.GenerateKeyPair().PrivKey, s_associatedData);
        return (packet, nodeKeys);
    }

    /// <summary>
    /// Builds a single-hop packet whose decrypted hop_payloads equal <paramref name="plaintext"/>, with a valid HMAC,
    /// so that framing checks can be exercised independently of the builder.
    /// </summary>
    private OnionPacket CraftSingleHopPacket(CompactPubKey nodeId, byte[] plaintext, out byte[] sharedSecret,
                                             byte[]? associatedData = null)
    {
        var sessionKey = _ecdh.GenerateKeyPair();
        sharedSecret = new byte[32];
        _ecdh.SecP256K1Dh(sessionKey.PrivKey, nodeId, sharedSecret);

        using var keyGenerator = new SphinxKeyGenerator();
        using var chaCha20 = new ChaCha20Stream();
        var hopPayloads = new byte[plaintext.Length];
        chaCha20.Xor(keyGenerator.DeriveKey(OnionConstants.Rho, sharedSecret), plaintext, hopPayloads);

        var hmac = new byte[32];
        keyGenerator.ComputeHmac(keyGenerator.DeriveKey(OnionConstants.Mu, sharedSecret), hopPayloads,
                                 associatedData ?? s_associatedData, hmac);

        return new OnionPacket(OnionConstants.Version, sessionKey.CompactPubKey, hopPayloads, hmac);
    }

    /// <summary>
    /// Computes the blinded node id <c>HMAC("blinded_node_id", ECDH(path_key, node_id)) * node_id</c>.
    /// </summary>
    private CompactPubKey BlindNodeId(CompactPubKey nodeId, PrivKey pathKey)
    {
        var blindingSharedSecret = new byte[32];
        _ecdh.SecP256K1Dh(pathKey, nodeId, blindingSharedSecret);
        using var keyGenerator = new SphinxKeyGenerator();
        var tweak = keyGenerator.DeriveKey(OnionConstants.BlindedNodeId, blindingSharedSecret);
        return new Secp256K1Math().MultiplyPubKey(nodeId, tweak);
    }
}
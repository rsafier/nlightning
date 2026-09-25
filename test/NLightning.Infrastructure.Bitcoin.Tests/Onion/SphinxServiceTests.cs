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
        var packet = CraftSingleHopPacket(nodeKey.CompactPubKey, plaintext);

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(packet, s_associatedData,
                                                                                 nodeKey.PrivKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00 }, exception.FailureData?.ToArray());
    }

    [Fact]
    public void Given_LargestValidFraming_When_Peeling_Then_Succeeds()
    {
        // Arrange: fd04f1 = 1265; 3 + 1265 + 32 = 1300
        var nodeKey = _ecdh.GenerateKeyPair();
        var plaintext = new byte[OnionConstants.HopPayloadsLength];
        Convert.FromHexString("fd04f1").CopyTo(plaintext, 0);
        var packet = CraftSingleHopPacket(nodeKey.CompactPubKey, plaintext);

        // Act
        var peeled = _sphinxService.Peel(packet, s_associatedData, nodeKey.PrivKey);

        // Assert
        Assert.True(peeled.IsFinal);
        Assert.Equal(1265, peeled.Payload.Length);
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
    public void Given_UnexpectedPathKey_When_Peeling_Then_ThrowsInvalidOnionHmac()
    {
        // Arrange
        var (packet, nodeKeys) = BuildTwoHopPacket();
        var pathKey = _ecdh.GenerateKeyPair().CompactPubKey;

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(packet, s_associatedData,
                                                                                 nodeKeys[0].PrivKey, pathKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionHmac, exception.FailureCode);
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

        // Assert
        Assert.False(peeled.IsFinal);
        keyManager.Verify(m => m.GetNodeKeyPair(), Times.Once);
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
    private OnionPacket CraftSingleHopPacket(CompactPubKey nodeId, byte[] plaintext)
    {
        var sessionKey = _ecdh.GenerateKeyPair();
        var sharedSecret = new byte[32];
        _ecdh.SecP256K1Dh(sessionKey.PrivKey, nodeId, sharedSecret);

        using var keyGenerator = new SphinxKeyGenerator();
        using var chaCha20 = new ChaCha20Stream();
        var hopPayloads = new byte[plaintext.Length];
        chaCha20.Xor(keyGenerator.DeriveKey(OnionConstants.Rho, sharedSecret), plaintext, hopPayloads);

        var hmac = new byte[32];
        keyGenerator.ComputeHmac(keyGenerator.DeriveKey(OnionConstants.Mu, sharedSecret), hopPayloads,
                                 s_associatedData, hmac);

        return new OnionPacket(OnionConstants.Version, sessionKey.CompactPubKey, hopPayloads, hmac);
    }
}
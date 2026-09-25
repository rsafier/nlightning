using System.Security.Cryptography;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT4;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion;

/// <summary>
/// BOLT 4 Sphinx vectors: onion-test.json (construction and peeling), onion-error-test.json (per-hop keys) and the
/// peel chain of blinded-payment-onion-test.json.
/// </summary>
public class OnionVectorTests
{
    private readonly SphinxService _sphinxService = new(new Ecdh(), new Secp256K1Math());

    [Fact]
    public void Given_OnionErrorTestHops_When_ComputingSharedSecrets_Then_AllHopSecretsMatch()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionErrorTest();
        var nodeIds = vector.Hops.Select(h => new CompactPubKey(h.PubKey)).ToList();

        // Act
        var sharedSecrets = _sphinxService.ComputeSharedSecrets(nodeIds, vector.SessionKey);

        // Assert
        Assert.Equal(vector.Hops.Count, sharedSecrets.Count);
        for (var i = 0; i < vector.Hops.Count; i++)
            Assert.Equal(vector.Hops[i].SharedSecret, (byte[])sharedSecrets[i]);
    }

    [Fact]
    public void Given_OnionErrorTestHops_When_DerivingAmmagAndUmKeys_Then_AllKeysMatch()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionErrorTest();
        using var keyGenerator = new SphinxKeyGenerator();

        foreach (var hop in vector.Hops)
        {
            // Act
            var ammagKey = keyGenerator.DeriveKey(OnionConstants.Ammag, hop.SharedSecret);

            // Assert
            Assert.Equal(hop.AmmagKey, ammagKey);
            if (hop.UmKey is not null)
                Assert.Equal(hop.UmKey, keyGenerator.DeriveKey(OnionConstants.Um, hop.SharedSecret));
        }

        Assert.NotNull(vector.Hops[^1].UmKey);
    }

    [Fact]
    public void Given_OnionTestVector_When_Constructing_Then_OnionMatchesExactly()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionTest();
        var hops = vector.Hops.Select(h => new OnionHop(h.PubKey, StripLengthPrefix(h.Payload))).ToList();

        // Act
        var packet = _sphinxService.Construct(hops, vector.SessionKey, vector.AssociatedData);

        // Assert
        Assert.Equal(Convert.ToHexStringLower(vector.Onion), Convert.ToHexStringLower(packet.ToBytes()));
    }

    [Fact]
    public void Given_OnionTestVector_When_PeelingWithEachHopKey_Then_EachHopGetsItsPayloadAndLastIsFinal()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionTest();
        var errorVector = Bolt4Vectors.LoadOnionErrorTest();
        var (ephemeralPubKeys, _) = new OnionBuilder(new Ecdh(), new Secp256K1Math())
           .ComputeHopKeys(vector.Hops.Select(h => new CompactPubKey(h.PubKey)).ToList(), vector.SessionKey);
        OnionPacket? current = new OnionPacket(vector.Onion);

        for (var i = 0; i < vector.Hops.Count; i++)
        {
            // Act
            Assert.NotNull(current);
            var peeled = _sphinxService.Peel(current.Value, vector.AssociatedData, vector.DecodePrivateKeys[i]);

            // Assert
            Assert.Equal(StripLengthPrefix(vector.Hops[i].Payload), peeled.Payload.ToArray());
            Assert.Equal(errorVector.Hops[i].SharedSecret, (byte[])peeled.SharedSecret);
            var isLast = i == vector.Hops.Count - 1;
            Assert.Equal(isLast, peeled.IsFinal);
            Assert.Equal(isLast, peeled.NextPacket is null);
            if (!isLast)
            {
                // The forwarded key is E_i * SHA256(E_i || ss_i), which must equal the sender's E_{i+1} = e_{i+1} * G
                Assert.Equal(ephemeralPubKeys[i + 1], peeled.NextPacket!.Value.PublicKey.ToArray());
                Assert.Equal(OnionConstants.Version, peeled.NextPacket.Value.Version);
            }

            current = peeled.NextPacket;
        }

        Assert.Null(current);
    }

    [Fact]
    public void Given_OnionTestVector_When_Peeling_Then_InputPacketIsNotMutated()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionTest();
        var packet = new OnionPacket(vector.Onion);

        // Act
        _ = _sphinxService.Peel(packet, vector.AssociatedData, vector.DecodePrivateKeys[0]);

        // Assert
        Assert.Equal(vector.Onion, packet.ToBytes());
    }

    [Theory]
    [InlineData(34)] // first byte of hop_payloads
    [InlineData(650)]
    [InlineData(1333)] // last byte of hop_payloads
    [InlineData(1334)] // first byte of hmac
    [InlineData(1365)] // last byte of hmac
    public void Given_OnionTestVectorWithFlippedBit_When_Peeling_Then_ThrowsInvalidOnionHmac(int byteIndex)
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionTest();
        var bytes = (byte[])vector.Onion.Clone();
        bytes[byteIndex] ^= 0x80;

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(new OnionPacket(bytes),
            vector.AssociatedData, vector.DecodePrivateKeys[0]));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionHmac, exception.FailureCode);
        Assert.Equal(SHA256.HashData(bytes), exception.FailureData?.ToArray());
    }

    [Fact]
    public void Given_OnionTestVectorWithVersion1_When_Peeling_Then_ThrowsInvalidOnionVersion()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionTest();
        var bytes = (byte[])vector.Onion.Clone();
        bytes[0] = 0x01;

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(new OnionPacket(bytes),
            vector.AssociatedData, vector.DecodePrivateKeys[0]));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionVersion, exception.FailureCode);
        Assert.Equal(SHA256.HashData(bytes), exception.FailureData?.ToArray());
    }

    [Theory]
    [InlineData(0, 0x04)] // uncompressed prefix
    [InlineData(0, 0x05)] // bogus prefix
    [InlineData(1, 0xFF)] // x coordinate >= p / not on curve
    public void Given_OnionTestVectorWithBadPubKey_When_Peeling_Then_ThrowsInvalidOnionKey(int mode, byte value)
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionTest();
        var bytes = (byte[])vector.Onion.Clone();
        if (mode == 0)
            bytes[1] = value;
        else
            bytes.AsSpan(2, 32).Fill(value);

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(new OnionPacket(bytes),
            vector.AssociatedData, vector.DecodePrivateKeys[0]));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionKey, exception.FailureCode);
        Assert.Equal(SHA256.HashData(bytes), exception.FailureData?.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0x01 })]
    [InlineData(new byte[] { 0xFD, 0x00, 0x20 })] // non-canonical
    [InlineData(new byte[] { 0xFD, 0x05, 0x00 })] // 1280 + 3 + 32 > 1300
    public void Given_RouteWithBadLengthPrefix_When_PeelingValidHmac_Then_ThrowsInvalidOnionPayload(byte[] prefix)
    {
        // Arrange: build a real onion for the vector's first hop, then patch the decrypted length prefix by XORing
        // the ciphertext (the stream is known only to the hop, so recompute it) and re-MAC it for that hop.
        var vector = Bolt4Vectors.LoadOnionTest();
        var packet = new OnionPacket(vector.Onion);
        var peeled = _sphinxService.Peel(packet, vector.AssociatedData, vector.DecodePrivateKeys[0]);
        var bytes = packet.ToBytes();
        var original = new byte[prefix.Length];
        vector.Hops[0].Payload.AsSpan(0, prefix.Length).CopyTo(original);
        for (var i = 0; i < prefix.Length; i++)
            bytes[OnionConstants.VersionLength + OnionConstants.PublicKeyLength + i] ^= (byte)(original[i] ^ prefix[i]);

        using var keyGenerator = new SphinxKeyGenerator();
        var muKey = keyGenerator.DeriveKey(OnionConstants.Mu, peeled.SharedSecret);
        keyGenerator.ComputeHmac(muKey, bytes.AsSpan(34, OnionConstants.HopPayloadsLength), vector.AssociatedData,
                                 bytes.AsSpan(1334, OnionConstants.HmacLength));

        // Act
        var exception = Assert.Throws<OnionException>(() => _sphinxService.Peel(new OnionPacket(bytes),
            vector.AssociatedData, vector.DecodePrivateKeys[0]));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
    }

    [Fact]
    public void Given_BlindedPaymentOnionVector_When_PeelingChainWithPathKeys_Then_EachNextOnionMatches()
    {
        // Arrange: decrypt.hops[i] = { onion (as received), node_privkey, next_path_key (path_key for hop i+1) }.
        // Alice is a normal hop; Bob is the introduction point (current_path_key is inside his payload, so his onion
        // is encrypted to his real node id); Carol, Dave and Eve receive path_key in update_add_htlc.
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.BlindedPaymentOnionTestPath);
        var root = document.RootElement;
        var associatedData = Bolt4Vectors.GetHex(Bolt4Vectors.GetRequired(root, "generate"), "associated_data");
        var hops = Bolt4Vectors.GetRequired(Bolt4Vectors.GetRequired(root, "decrypt"), "hops")
                               .EnumerateArray()
                               .Select(h => (Onion: Bolt4Vectors.GetHex(h, "onion"),
                                             NodeKey: Bolt4Vectors.GetHex(h, "node_privkey"),
                                             NextPathKey: Bolt4Vectors.GetOptionalHex(h, "next_path_key")))
                               .ToList();

        CompactPubKey? pathKey = null;
        for (var i = 0; i < hops.Count; i++)
        {
            // Act
            var peeled = _sphinxService.Peel(new OnionPacket(hops[i].Onion), associatedData, hops[i].NodeKey,
                                             pathKey);

            // Assert
            if (i < hops.Count - 1)
            {
                Assert.False(peeled.IsFinal);
                Assert.Equal(Convert.ToHexStringLower(hops[i + 1].Onion),
                             Convert.ToHexStringLower(peeled.NextPacket!.Value.ToBytes()));
            }
            else
            {
                Assert.True(peeled.IsFinal);
            }

            // next_path_key is what hop i sends in update_add_htlc to hop i+1 (absent for Alice)
            // (cast needed: a bare null would convert to CompactPubKey through its implicit byte[] operator)
            pathKey = hops[i].NextPathKey is null ? (CompactPubKey?)null : new CompactPubKey(hops[i].NextPathKey!);
        }
    }

    /// <summary>
    /// The onion-test.json payloads already include their bigsize length; the Sphinx API takes them without it.
    /// </summary>
    private static byte[] StripLengthPrefix(byte[] framed)
    {
        var (length, prefixLength) = framed[0] switch
        {
            < 0xFD => (framed[0], 1),
            0xFD => ((framed[1] << 8) | framed[2], 3),
            _ => throw new InvalidDataException("Unexpected bigsize prefix in vector payload")
        };

        Assert.Equal(framed.Length, prefixLength + length);
        return framed[prefixLength..];
    }
}
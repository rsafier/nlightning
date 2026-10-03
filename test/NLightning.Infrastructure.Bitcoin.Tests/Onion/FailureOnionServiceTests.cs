using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Onion;

public class FailureOnionServiceTests
{
    private readonly FailureOnionService _service = new(new FakeFailureMessageSerializer());

    [Theory]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(20, 13)]
    [InlineData(27, 26)]
    [InlineData(30, 29)]
    public void Given_ErringHop_When_CreatingWrappingAndDecrypting_Then_ErringHopAndMessageAreRecovered(
        int routeLength, int erringHop)
    {
        // Arrange
        var route = CreateRoute(routeLength);
        var packet = _service.CreateErrorPacket(route[erringHop], FailureMessage.UnknownNextPeer());
        for (var i = erringHop - 1; i >= 0; i--)
            packet = _service.WrapErrorPacket(route[i], packet);

        // Act
        var decrypted = _service.DecryptErrorPacket(route, packet);

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(erringHop, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.UnknownNextPeer, decrypted.Code);
        Assert.NotNull(decrypted.Message);
        Assert.Equal("400a", Convert.ToHexStringLower(decrypted.RawMessage.Span));
    }

    [Fact]
    public void Given_ShortMessage_When_CreatingErrorPacket_Then_PacketIsHmacPlus260Bytes()
    {
        // Act
        var packet = _service.CreateErrorPacket(CreateRoute(1)[0], FailureMessage.TemporaryNodeFailure());

        // Assert: 32 + 2 + 2 + 2 + 254
        Assert.Equal(292, packet.Length);
    }

    [Fact]
    public void Given_FlippedBit_When_Decrypting_Then_NoHopMatches()
    {
        // Arrange
        var route = CreateRoute(3);
        var packet = _service.CreateErrorPacket(route[2], FailureMessage.TemporaryNodeFailure());
        packet = _service.WrapErrorPacket(route[1], packet);
        packet = _service.WrapErrorPacket(route[0], packet);
        packet[100] ^= 0x01;

        // Act
        var decrypted = _service.DecryptErrorPacket(route, packet);

        // Assert
        Assert.Null(decrypted);
    }

    [Fact]
    public void Given_PacketAuthenticatedPastTheRoute_When_Decrypting_Then_DummyIterationIsNotAttributed()
    {
        // Arrange: an "erring hop" whose secret equals the constant dummy secret, beyond the end of the route
        var route = CreateRoute(2);
        var packet = FailureOnionService.CreateErrorPacket(new byte[32], Frame("2002"));
        packet = _service.WrapErrorPacket(route[1], packet);
        packet = _service.WrapErrorPacket(route[0], packet);

        // Act
        var decrypted = _service.DecryptErrorPacket(route, packet);

        // Assert
        Assert.Null(decrypted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void Given_PacketShorterThanHmac_When_Decrypting_Then_ReturnsNull(int length)
    {
        // Act
        var decrypted = _service.DecryptErrorPacket(CreateRoute(2), new byte[length]);

        // Assert
        Assert.Null(decrypted);
    }

    [Fact]
    public void Given_EmptyRoute_When_Decrypting_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => _service.DecryptErrorPacket([], new byte[292]));
    }

    [Fact]
    public void Given_DefaultSecretInRoute_When_Decrypting_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => _service.DecryptErrorPacket([CreateRoute(1)[0], default],
                                                                            new byte[292]));
    }

    [Fact]
    public void Given_BadFailureLen_When_Decrypting_Then_HopIsAttributedWithoutMessage()
    {
        // Arrange: failure_len (0xffff) is longer than the body, but the HMAC is valid
        var route = CreateRoute(2);
        var packet = FailureOnionService.CreateErrorPacket(route[1], Convert.FromHexString("ffff2002"));
        packet = _service.WrapErrorPacket(route[0], packet);

        // Act
        var decrypted = _service.DecryptErrorPacket(route, packet);

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(1, decrypted.ErringHopIndex);
        Assert.True(decrypted.RawMessage.IsEmpty);
        Assert.Null(decrypted.Message);
        Assert.Null(decrypted.Code);
    }

    [Fact]
    public void Given_TruncatedKnownData_When_Decrypting_Then_CodeComesFromRawMessage()
    {
        // Arrange: invalid_onion_hmac with a 1-byte sha256_of_onion
        var route = CreateRoute(1);
        var packet = FailureOnionService.CreateErrorPacket(route[0], Frame("c00500"));

        // Act
        var decrypted = _service.DecryptErrorPacket(route, packet);

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Null(decrypted.Message);
        Assert.Equal(FailureCode.InvalidOnionHmac, decrypted.Code);
        Assert.Equal("c00500", Convert.ToHexStringLower(decrypted.RawMessage.Span));
    }

    [Fact]
    public void Given_Packet_When_Wrapping_Then_InputIsUntouchedAndWrappingTwiceRestoresIt()
    {
        // Arrange
        var secret = CreateRoute(1)[0];
        var packet = RandomNumberGenerator.GetBytes(292);
        var copy = packet.ToArray();

        // Act
        var wrapped = _service.WrapErrorPacket(secret, packet);
        var unwrapped = _service.WrapErrorPacket(secret, wrapped);

        // Assert
        Assert.Equal(copy, packet);
        Assert.NotEqual(packet, wrapped);
        Assert.Equal(packet, unwrapped);
    }

    [Fact]
    public void Given_PacketOver32768_When_Wrapping_Then_ItIsTruncatedFirst()
    {
        // Arrange
        var secret = CreateRoute(1)[0];
        var packet = RandomNumberGenerator.GetBytes(OnionConstants.MaxErrorPacketLength + 100);

        // Act
        var wrapped = _service.WrapErrorPacket(secret, packet);

        // Assert
        Assert.Equal(OnionConstants.MaxErrorPacketLength, wrapped.Length);
        Assert.Equal(_service.WrapErrorPacket(secret, packet.AsSpan(0, OnionConstants.MaxErrorPacketLength)),
                     wrapped);
    }

    [Fact]
    public void Given_InvalidSecret_When_CreatingErrorPacket_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => _service.CreateErrorPacket(default, FailureMessage.MppTimeout()));
    }

    [Fact]
    public void Given_BodyOver32768_When_CreatingErrorPacket_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => FailureOnionService.CreateErrorPacket(
                                             CreateRoute(1)[0],
                                             new byte[OnionConstants.MaxErrorPacketLength - 31]));
    }

    [Fact]
    public void Given_MalformedFromDownstream_When_Converting_Then_OriginAttributesConvertingHopWithSha256OfOnion()
    {
        // Arrange: hop 2 got update_fail_malformed_htlc from hop 3 and converts it with its incoming secret
        var route = CreateRoute(4);
        var sha256OfOnion = RandomNumberGenerator.GetBytes(32);

        // Act
        var packet = _service.CreateErrorPacketFromMalformed(route[2], FailureCode.InvalidOnionHmac, sha256OfOnion);
        packet = _service.WrapErrorPacket(route[1], packet);
        packet = _service.WrapErrorPacket(route[0], packet);
        var decrypted = _service.DecryptErrorPacket(route, packet);

        // Assert
        Assert.Equal(292, packet.Length);
        Assert.NotNull(decrypted);
        Assert.Equal(2, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.InvalidOnionHmac, decrypted.Code);
        Assert.Equal("c005" + Convert.ToHexStringLower(sha256OfOnion),
                     Convert.ToHexStringLower(decrypted.RawMessage.Span));
    }

    [Fact]
    public void Given_MalformedAndCreate_When_Comparing_Then_PacketsAreIdentical()
    {
        // Arrange
        var secret = CreateRoute(1)[0];
        var sha256OfOnion = RandomNumberGenerator.GetBytes(32);

        // Act
        var converted = _service.CreateErrorPacketFromMalformed(secret, FailureCode.InvalidOnionKey, sha256OfOnion);
        var created = _service.CreateErrorPacket(secret, FailureMessage.InvalidOnionKey(sha256OfOnion));

        // Assert
        Assert.Equal(created, converted);
    }

    [Fact]
    public void Given_MalformedWithoutBadOnionBit_When_Converting_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => _service.CreateErrorPacketFromMalformed(
                                             CreateRoute(1)[0], FailureCode.PermanentChannelFailure, new byte[32]));
    }

    private static List<Secret> CreateRoute(int length)
    {
        return Enumerable.Range(0, length).Select(_ => new Secret(RandomNumberGenerator.GetBytes(32))).ToList();
    }

    private static byte[] Frame(string failureMessageHex)
    {
        return FakeFailureMessageSerializer.Frame(Convert.FromHexString(failureMessageHex),
                                                  OnionConstants.MinFailurePadLength);
    }

    /// <summary>
    /// Minimal stand-in for the Serialization implementation (not referenced by this test project): code || data,
    /// no TLV extension, and the BOLT 4 framing.
    /// </summary>
    private sealed class FakeFailureMessageSerializer : IFailureMessageSerializer
    {
        public byte[] Serialize(FailureMessage message)
        {
            var bytes = new byte[2 + message.Data.Length];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)message.Code);
            message.Data.Span.CopyTo(bytes.AsSpan(2));
            return bytes;
        }

        public FailureMessage Deserialize(ReadOnlyMemory<byte> failureMessage)
        {
            return new FailureMessage((FailureCode)BinaryPrimitives.ReadUInt16BigEndian(failureMessage.Span),
                                      failureMessage[2..]);
        }

        public bool TryDeserialize(ReadOnlyMemory<byte> failureMessage, out FailureMessage? message)
        {
            try
            {
                message = Deserialize(failureMessage);
                return true;
            }
            catch (ArgumentException)
            {
                message = null;
                return false;
            }
        }

        public byte[] SerializeErrorPayload(FailureMessage message,
                                            int minFailurePadLength = OnionConstants.MinFailurePadLength)
        {
            return Frame(Serialize(message), minFailurePadLength);
        }

        public bool TryReadErrorPayload(ReadOnlyMemory<byte> errorPayload, out ReadOnlyMemory<byte> failureMessage)
        {
            failureMessage = ReadOnlyMemory<byte>.Empty;
            if (errorPayload.Length < 2)
                return false;

            var length = BinaryPrimitives.ReadUInt16BigEndian(errorPayload.Span);
            if (errorPayload.Length - 2 < length)
                return false;

            failureMessage = errorPayload.Slice(2, length);
            return true;
        }

        public static byte[] Frame(byte[] failureMessage, int minFailurePadLength)
        {
            var padLength = Math.Max(0, minFailurePadLength - failureMessage.Length);
            var payload = new byte[4 + failureMessage.Length + padLength];
            BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)failureMessage.Length);
            failureMessage.CopyTo(payload, 2);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2 + failureMessage.Length), (ushort)padLength);
            return payload;
        }
    }
}
namespace NLightning.Domain.Tests.Protocol.Onion;

using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.ValueObjects;

public class OnionPacketTests
{
    private static byte[] CreatePacketBytes(int hopPayloadsLength = OnionConstants.HopPayloadsLength)
    {
        var bytes = new byte[OnionConstants.PacketOverheadLength + hopPayloadsLength];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i % 251);

        return bytes;
    }

    [Fact]
    public void Given_1366Bytes_When_Constructing_Then_ComponentsAreSlicedCorrectly()
    {
        // Arrange
        var bytes = CreatePacketBytes();
        bytes[0] = OnionConstants.Version;

        // Act
        var packet = new OnionPacket(bytes);

        // Assert
        Assert.Equal(OnionConstants.PacketLength, packet.Length);
        Assert.Equal(1366, packet.Length);
        Assert.Equal(OnionConstants.Version, packet.Version);
        Assert.Equal(bytes[1..34], packet.PublicKey.ToArray());
        Assert.Equal(bytes[34..1334], packet.HopPayloads.ToArray());
        Assert.Equal(bytes[1334..], packet.Hmac.ToArray());
        Assert.Equal(OnionConstants.HopPayloadsLength, packet.HopPayloadsLength);
        Assert.Equal(bytes, packet.ToBytes());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1365)]
    [InlineData(1367)]
    [InlineData(66)]
    public void Given_WrongTotalLength_When_Constructing_Then_Throws(int length)
    {
        // Arrange
        var bytes = new byte[length];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new OnionPacket(bytes));
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0xFF)]
    public void Given_UnknownVersionByte_When_Constructing_Then_PacketIsAccepted(byte version)
    {
        // Arrange
        var bytes = CreatePacketBytes();
        bytes[0] = version;

        // Act
        var packet = new OnionPacket(bytes);

        // Assert
        Assert.Equal(version, packet.Version);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x04)]
    [InlineData(0xFF)]
    public void Given_InvalidPubKeyPrefix_When_Constructing_Then_PacketIsAccepted(byte prefix)
    {
        // Arrange
        var bytes = CreatePacketBytes();
        bytes[1] = prefix;

        // Act
        var packet = new OnionPacket(bytes);

        // Assert
        Assert.Equal(prefix, packet.PublicKey.Span[0]);
        Assert.Equal(OnionConstants.PublicKeyLength, packet.PublicKey.Length);
    }

    [Fact]
    public void Given_CustomHopPayloadsLength_When_Constructing_Then_LengthIsParameterized()
    {
        // Arrange
        const int hopPayloadsLength = 32768;
        var bytes = CreatePacketBytes(hopPayloadsLength);

        // Act
        var packet = new OnionPacket(bytes, hopPayloadsLength);

        // Assert
        Assert.Equal(hopPayloadsLength, packet.HopPayloadsLength);
        Assert.Equal(bytes[^32..], packet.Hmac.ToArray());
        Assert.Throws<ArgumentException>(() => new OnionPacket(bytes));
    }

    [Fact]
    public void Given_Components_When_Constructing_Then_BytesMatchRawConstruction()
    {
        // Arrange
        var bytes = CreatePacketBytes();
        var expected = new OnionPacket(bytes);

        // Act
        var packet = new OnionPacket(bytes[0], bytes.AsSpan(1, 33), bytes.AsSpan(34, 1300), bytes.AsSpan(1334));

        // Assert
        Assert.Equal(expected, packet);
        Assert.True(expected == packet);
        Assert.Equal(expected.GetHashCode(), packet.GetHashCode());
        Assert.Equal(bytes, packet.ToBytes());
    }

    [Fact]
    public void Given_InvalidComponentLengths_When_Constructing_Then_Throws()
    {
        // Arrange
        var pubKey = new byte[33];
        var hopPayloads = new byte[1300];
        var hmac = new byte[32];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new OnionPacket(0, new byte[32], hopPayloads, hmac));
        Assert.Throws<ArgumentException>(() => new OnionPacket(0, pubKey, [], hmac));
        Assert.Throws<ArgumentException>(() => new OnionPacket(0, pubKey, hopPayloads, new byte[31]));
    }

    [Fact]
    public void Given_SourceArrayMutated_When_PacketWasConstructed_Then_PacketIsUnchanged()
    {
        // Arrange
        var bytes = CreatePacketBytes();
        var packet = new OnionPacket(bytes);
        var copy = packet.ToBytes();

        // Act
        bytes[100] ^= 0xFF;
        copy[101] ^= 0xFF;

        // Assert
        Assert.NotEqual(bytes[100], packet.HopPayloads.Span[100 - 34]);
        Assert.NotEqual(copy[101], packet.HopPayloads.Span[101 - 34]);
    }

    [Fact]
    public void Given_DifferentHmac_When_Comparing_Then_NotEqual()
    {
        // Arrange
        var bytes = CreatePacketBytes();
        var other = (byte[])bytes.Clone();
        other[^1] ^= 0x01;

        // Act
        var a = new OnionPacket(bytes);
        var b = new OnionPacket(other);

        // Assert
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }
}
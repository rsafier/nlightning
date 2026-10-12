namespace NLightning.Domain.Tests.Protocol.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.OnionMessages;

/// <summary>
/// BOLT 1 <c>sciddir_or_pubkey</c> (OM0-T3, OM-W-06): first byte 0/1 → direction + 8-byte SCID, 2/3 → point, any
/// other byte refused.
/// </summary>
public class SciddirOrPubkeyCodecTests
{
    private const string NodeIdHex = "02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619";
    private const string ScidHex = "0001020003040005";

    public static TheoryData<string> ValidEncodings => new()
    {
        "00" + ScidHex,
        "01" + ScidHex,
        NodeIdHex,
        "03" + NodeIdHex[2..]
    };

    [Theory]
    [MemberData(nameof(ValidEncodings))]
    public void Given_ValidEncoding_When_RoundTripped_Then_BytesAreIdentical(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var value = SciddirOrPubkeyCodec.Decode(bytes);
        var encoded = SciddirOrPubkeyCodec.Encode(value);

        // Assert
        Assert.Equal(bytes, encoded);
        Assert.Equal(bytes.Length, SciddirOrPubkeyCodec.GetLength(value));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void Given_ScidForm_When_Decoded_Then_DirectionAndScidAreRead(byte direction)
    {
        // Arrange
        var bytes = Convert.FromHexString($"{direction:x2}{ScidHex}");

        // Act
        var value = SciddirOrPubkeyCodec.Decode(bytes);

        // Assert
        Assert.False(value.IsNodeId);
        Assert.Null(value.NodeId);
        Assert.Equal(direction, value.Direction);
        Assert.Equal(new ShortChannelId(0x000102, 0x000304, 0x0005), value.ShortChannelId);
    }

    [Fact]
    public void Given_PointForm_When_Decoded_Then_NodeIdIsRead()
    {
        // Arrange
        var bytes = Convert.FromHexString(NodeIdHex);

        // Act
        var value = SciddirOrPubkeyCodec.Decode(bytes);

        // Assert
        Assert.True(value.IsNodeId);
        Assert.Equal(new CompactPubKey(bytes), value.NodeId);
        Assert.Null(value.ShortChannelId);
    }

    [Theory]
    [InlineData("04")]
    [InlineData("05")]
    [InlineData("ff")]
    [InlineData("")]
    public void Given_UndefinedOrMissingFirstByte_When_Read_Then_Refused(string prefix)
    {
        // Arrange: the prefix, padded to 33 bytes so only the first byte can be wrong
        var bytes = prefix.Length == 0 ? [] : Convert.FromHexString(prefix + new string('1', 64));

        // Act
        var ok = SciddirOrPubkeyCodec.TryRead(bytes, out var value, out var bytesRead, out var reason);

        // Assert
        Assert.False(ok);
        Assert.Null(value);
        Assert.Equal(0, bytesRead);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("00000102000304")] // SCID form, 7 bytes
    [InlineData("01")]
    [InlineData("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f2836866")] // point, 32 bytes
    public void Given_TruncatedValue_When_Read_Then_Refused(string hex)
    {
        // Act
        var ok = SciddirOrPubkeyCodec.TryRead(Convert.FromHexString(hex), out _, out _, out var reason);

        // Assert
        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Given_TrailingBytes_When_Read_Then_OnlyTheValueIsConsumed()
    {
        // Arrange
        var bytes = Convert.FromHexString("01" + ScidHex + "aabb");

        // Act
        var ok = SciddirOrPubkeyCodec.TryRead(bytes, out var value, out var bytesRead, out _);

        // Assert
        Assert.True(ok);
        Assert.Equal(9, bytesRead);
        Assert.Equal((byte)1, value!.Direction);
        Assert.Throws<FormatException>(() => SciddirOrPubkeyCodec.Decode(bytes));
    }

    [Fact]
    public void Given_ShortDestination_When_Written_Then_Throws()
    {
        // Arrange
        var value = SciddirOrPubkey.FromNodeId(new CompactPubKey(Convert.FromHexString(NodeIdHex)));

        // Act & Assert
        Assert.Throws<ArgumentException>(() => SciddirOrPubkeyCodec.Write(value, new byte[32]));
    }
}
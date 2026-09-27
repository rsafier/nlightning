namespace NLightning.Domain.Tests.Offers;

using Domain.Offers;

public class Bolt12TlvStreamTests
{
    [Theory]
    [InlineData("")]
    [InlineData("0000")]
    [InlineData("0001ff0a03414243")]
    [InlineData("fd00fd00")]
    [InlineData("fe3b9aca0102abcd")]
    [InlineData("ff00000001000000000100")]
    public void Given_ValidStream_When_ParsingAndEncoding_Then_ItRoundTrips(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var stream = Bolt12TlvStream.Parse(bytes);

        // Assert
        Assert.Equal(bytes, stream.Encode());
        Assert.Equal(bytes, stream.Records.SelectMany(Bolt12TlvStream.EncodeRecord).ToArray());
    }

    [Theory]
    [InlineData("0a", "truncated type length")]
    [InlineData("0a02ff", "value past the end")]
    [InlineData("0a00", "valid")]
    [InlineData("0a000a00", "duplicate type")]
    [InlineData("0c000a00", "decreasing types")]
    [InlineData("fd00fc00", "non-minimal type")]
    [InlineData("0afd000100", "non-minimal length")]
    [InlineData("fe0000ffff00", "non-minimal 5-byte type")]
    [InlineData("ff00000000ffffffff00", "non-minimal 9-byte type")]
    [InlineData("fd00", "truncated bigsize")]
    public void Given_Stream_When_Parsing_Then_OnlyValidOnesAreAccepted(string hex, string reason)
    {
        // Act
        var ok = Bolt12TlvStream.TryParse(Convert.FromHexString(hex), out var stream, out var why);

        // Assert
        Assert.Equal(reason == "valid", ok);
        Assert.Equal(ok, stream is not null);
        Assert.Equal(ok, why is null);
        if (!ok)
            Assert.Throws<FormatException>(() => Bolt12TlvStream.Parse(Convert.FromHexString(hex)));
    }

    [Fact]
    public void Given_UnorderedRecords_When_Constructing_Then_ItThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new Bolt12TlvStream([new(2, new byte[1]), new(2, new byte[1])]));
        Assert.Throws<ArgumentException>(() => new Bolt12TlvStream([new(3, new byte[1]), new(2, new byte[1])]));
    }

    [Fact]
    public void Given_Stream_When_Filtering_Then_OrderAndValuesAreKept()
    {
        // Arrange
        var stream = Bolt12TlvStream.Parse(Convert.FromHexString("0001aa0a01bb5001ccf00100"));

        // Act
        var offerPart = stream.Filter(t => t is >= 1 and <= 79);

        // Assert
        Assert.Equal([10UL], offerPart.Records.Select(r => r.Type));
        Assert.True(stream.TryGetValue(80, out var value));
        Assert.Equal([0xcc], value.ToArray());
        Assert.False(stream.TryGetValue(11, out _));
        Assert.True(stream.Contains(240));
        Assert.Equal(4, stream.Count);
    }

    [Fact]
    public void Given_TwoStreams_When_ComparingContent_Then_OnlyIdenticalRecordsAreEqual()
    {
        // Arrange
        var a = Bolt12TlvStream.Parse(Convert.FromHexString("0001aa0a01bb"));
        var b = Bolt12TlvStream.Parse(Convert.FromHexString("0001aa0a01bb"));
        var differentValue = Bolt12TlvStream.Parse(Convert.FromHexString("0001aa0a01bc"));
        var differentType = Bolt12TlvStream.Parse(Convert.FromHexString("0001aa0c01bb"));
        var shorter = Bolt12TlvStream.Parse(Convert.FromHexString("0001aa"));

        // Assert
        Assert.True(a.ContentEquals(b));
        Assert.False(a.ContentEquals(differentValue));
        Assert.False(a.ContentEquals(differentType));
        Assert.False(a.ContentEquals(shorter));
    }

    [Fact]
    public void Given_ReceivedStream_When_CopiedThroughTheBuilder_Then_UnknownRecordsSurvive()
    {
        // Arrange
        var stream = Bolt12TlvStream.Parse(Convert.FromHexString("0a01bb2101cc"));

        // Act
        var copy = new Bolt12TlvStreamBuilder(stream).Set(0, [1]).Build();

        // Assert
        Assert.Equal(Convert.FromHexString("0001010a01bb2101cc"), copy.Encode());
        Assert.Equal(Convert.FromHexString("0a01bb"), new Bolt12TlvStreamBuilder(stream, t => t < 20).Build().Encode());
        Assert.False(new Bolt12TlvStreamBuilder(stream).Remove(33).Contains(33));
    }
}
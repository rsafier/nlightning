namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Helpers;
using Serialization.Messages.Types;

/// <summary>
/// BOLT 2: a missing <c>channel_type</c> in open_channel/accept_channel fails the channel (handled by the
/// channel-open validator), so it must not be a wire deserialization error.
/// </summary>
public class OpenChannel1MessageTests
{
    private const string Point = "02C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75";
    private const string Zero32 = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly string s_openChannelPayloadHex =
        Zero32 + Zero32 + new string('0', 6 * 16) + "000003E8" + "0090" + "01E3"
      + string.Concat(Enumerable.Repeat(Point, 6)) + "00";

    private static readonly string s_acceptChannelPayloadHex =
        Zero32 + new string('0', 4 * 16) + "00000003" + "0090" + "01E3" + string.Concat(Enumerable.Repeat(Point, 6));

    private readonly OpenChannel1MessageTypeSerializer _openChannel1Serializer =
        new(SerializerHelper.PayloadSerializerFactory, SerializerHelper.TlvConverterFactory,
            SerializerHelper.TlvStreamSerializer);

    private readonly AcceptChannel1MessageTypeSerializer _acceptChannel1Serializer =
        new(SerializerHelper.PayloadSerializerFactory, SerializerHelper.TlvConverterFactory,
            SerializerHelper.TlvStreamSerializer);

    [Theory]
    [InlineData("")]
    [InlineData("0000")] // empty upfront_shutdown_script only
    public async Task Given_OpenChannelWithoutChannelType_When_DeserializeAsync_Then_ReturnsMessageWithNullChannelType(
        string extensionHex)
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(s_openChannelPayloadHex + extensionHex));

        // Act
        var message = await _openChannel1Serializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message);
        Assert.Null(message.ChannelTypeTlv);
    }

    [Fact]
    public async Task Given_OpenChannelWithChannelType_When_DeserializeAsync_Then_ReturnsMessageWithChannelType()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(s_openChannelPayloadHex + "01021000"));

        // Act
        var message = await _openChannel1Serializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message.ChannelTypeTlv);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0000")] // empty upfront_shutdown_script only
    public async Task
        Given_AcceptChannelWithoutChannelType_When_DeserializeAsync_Then_ReturnsMessageWithNullChannelType(
            string extensionHex)
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(s_acceptChannelPayloadHex + extensionHex));

        // Act
        var message = await _acceptChannel1Serializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message);
        Assert.Null(message.ChannelTypeTlv);
    }
}
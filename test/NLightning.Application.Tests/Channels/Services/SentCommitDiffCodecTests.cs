namespace NLightning.Application.Tests.Channels.Services;

using Application.Channels.Services;
using Domain.Channels.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;

public class SentCommitDiffCodecTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x01, 32).ToArray());

    [Fact]
    public async Task Given_Messages_When_EncodedAndDecoded_Then_EachComesBackInOrderFromItsOwnBoundedStream()
    {
        // Arrange - a fake wire format: type (2 bytes) + feerate (4 bytes) for update_fee
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.SerializeAsync(It.IsAny<IMessage>(), It.IsAny<Stream>()))
                  .Callback((IMessage message, Stream stream) =>
                   {
                       var fee = ((UpdateFeeMessage)message).Payload.FeeratePerKw;
                       stream.Write([(byte)((ushort)message.Type >> 8), (byte)message.Type]);
                       stream.Write(BitConverter.GetBytes(fee));
                   })
                  .Returns(Task.CompletedTask);
        var streamLengths = new List<long>();
        serializer.Setup(s => s.DeserializeMessageAsync(It.IsAny<Stream>()))
                  .Returns((Stream stream) =>
                   {
                       streamLengths.Add(stream.Length);
                       var bytes = new byte[stream.Length];
                       stream.ReadExactly(bytes);
                       return Task.FromResult<IMessage?>(
                           new UpdateFeeMessage(new UpdateFeePayload(s_channelId, BitConverter.ToUInt32(bytes, 2))));
                   });
        IMessage[] messages =
        [
            new UpdateFeeMessage(new UpdateFeePayload(s_channelId, 1_000)),
            new UpdateFeeMessage(new UpdateFeePayload(s_channelId, 2_000))
        ];

        // Act
        var diff = await SentCommitDiffCodec.EncodeAsync(serializer.Object, messages);
        var decoded = await SentCommitDiffCodec.DecodeAsync(serializer.Object, diff);

        // Assert
        Assert.Equal(2 * (2 + 6), diff.Length);
        Assert.Equal([0x00, 0x06, 0x00, (byte)MessageTypes.UpdateFee], diff[..4]);
        Assert.Equal([1_000U, 2_000U], decoded.Select(m => ((UpdateFeeMessage)m).Payload.FeeratePerKw));
        Assert.Equal([6L, 6L], streamLengths);
    }

    [Theory]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x00, 0x05, 0x01, 0x02 })]
    public void Given_TruncatedDiff_When_Split_Then_FormatException(byte[] diff)
    {
        // Act / Assert
        Assert.Throws<FormatException>(() => SentCommitDiffCodec.Split(diff));
    }
}
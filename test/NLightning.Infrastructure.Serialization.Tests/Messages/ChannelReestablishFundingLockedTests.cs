using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Exceptions;
using Factories;
using Helpers;
using Serialization.Messages;

/// <summary>
/// BOLT 2 <c>channel_reestablish_tlvs</c> type 5 (<c>my_current_funding_locked</c>, SP-RE-02; SP1-A-T2): read and
/// written next to type 1 (<c>next_funding</c>), with the strict known set {1, 5}.
/// </summary>
public class ChannelReestablishFundingLockedTests
{
    private const string ChannelIdHex = "0303030303030303030303030303030303030303030303030303030303030303";
    private const string SecretHex = "567CBDADB00B825448B2E414487D73A97F657F0634166D3AB3F3A2CC1042EDA5";
    private const string PointHex = "02C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75";
    private const string NextFundingTxIdHex = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string LockedTxIdHex = "A0A1A2A3A4A5A6A7A8A9AAABACADAEAFB0B1B2B3B4B5B6B7B8B9BABBBCBDBEBF";

    // channel_id, next_commitment_number = 5, next_revocation_number = 4, your_last_per_commitment_secret,
    // my_current_per_commitment_point
    private const string PayloadHex =
        ChannelIdHex + "0000000000000005" + "0000000000000004" + SecretHex + PointHex;

    private readonly MessageSerializer _messageSerializer =
        new(NullLogger<MessageSerializer>.Instance,
            new MessageTypeSerializerFactory(SerializerHelper.PayloadSerializerFactory,
                                             SerializerHelper.TlvStreamSerializer));

    private readonly IMessageTypeSerializer<ChannelReestablishMessage> _serializer =
        SerializerHelper.MessageTypeSerializerFactory.GetSerializer<ChannelReestablishMessage>()!;

    [Fact]
    public async Task Given_MyCurrentFundingLocked_When_RoundTripped_Then_ItIsByteExact()
    {
        // Arrange
        var message = new ChannelReestablishMessage(CreatePayload(), null,
                                                    new MyCurrentFundingLockedTlv(
                                                        new TxId(Convert.FromHexString(LockedTxIdHex)),
                                                        MyCurrentFundingLockedTlv.AnnouncementSignaturesFlag));

        // Act
        var bytes = await SerializeAsync(message);
        var parsed = await DeserializeAsync(bytes);

        // Assert (136 = 0x0088; type 5, length 33, txid, retransmit_flags = 1)
        Assert.Equal("0088" + PayloadHex + "0521" + LockedTxIdHex + "01", Convert.ToHexString(bytes));
        Assert.Null(parsed.NextFundingTlv);
        Assert.NotNull(parsed.MyCurrentFundingLockedTlv);
        Assert.Equal(LockedTxIdHex, Convert.ToHexString(parsed.MyCurrentFundingLockedTlv.FundingTxId));
        Assert.Equal(MyCurrentFundingLockedTlv.AnnouncementSignaturesFlag,
                     parsed.MyCurrentFundingLockedTlv.RetransmitFlags);
    }

    [Fact]
    public async Task Given_NextFundingAndMyCurrentFundingLocked_When_RoundTripped_Then_BothAreKeptInTypeOrder()
    {
        // Arrange
        var message = new ChannelReestablishMessage(CreatePayload(),
                                                    new NextFundingTlv(Convert.FromHexString(NextFundingTxIdHex), 1),
                                                    new MyCurrentFundingLockedTlv(
                                                        new TxId(Convert.FromHexString(LockedTxIdHex))));

        // Act
        var bytes = await SerializeAsync(message);
        var parsed = await DeserializeAsync(bytes);

        // Assert
        Assert.Equal("0088" + PayloadHex + "0121" + NextFundingTxIdHex + "01" + "0521" + LockedTxIdHex + "00",
                     Convert.ToHexString(bytes));
        Assert.NotNull(parsed.NextFundingTlv);
        Assert.Equal(NextFundingTxIdHex, Convert.ToHexString(parsed.NextFundingTlv.NextFundingTxId));
        Assert.Equal((byte)1, parsed.NextFundingTlv.RetransmitFlags);
        Assert.NotNull(parsed.MyCurrentFundingLockedTlv);
        Assert.Equal(LockedTxIdHex, Convert.ToHexString(parsed.MyCurrentFundingLockedTlv.FundingTxId));
        Assert.Equal((byte)0, parsed.MyCurrentFundingLockedTlv.RetransmitFlags);
    }

    [Fact]
    public async Task Given_NoTlvs_When_Deserialized_Then_BothAreNull()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.Null(message.NextFundingTlv);
        Assert.Null(message.MyCurrentFundingLockedTlv);
    }

    [Fact]
    public async Task Given_UnknownEvenTlv_When_Deserialized_Then_ThrowsMessageSerializationException()
    {
        // Arrange (type 4 is not in {1, 5}: BOLT 1 fails the message)
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + "0521" + LockedTxIdHex + "00"
                                                          + "06012A"));

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => _serializer.DeserializeAsync(stream));
    }

    [Fact]
    public async Task Given_UnknownOddTlv_When_Deserialized_Then_ItIsIgnored()
    {
        // Arrange
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + "03012A" + "0521" + LockedTxIdHex + "00"));

        // Act
        var message = await _serializer.DeserializeAsync(stream);

        // Assert
        Assert.NotNull(message.MyCurrentFundingLockedTlv);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Fact]
    public async Task Given_MyCurrentFundingLockedWithWrongLength_When_Deserialized_Then_Throws()
    {
        // Arrange (33 bytes expected; 32 given)
        var stream = new MemoryStream(Convert.FromHexString(PayloadHex + "0520" + LockedTxIdHex));

        // Act & Assert
        await Assert.ThrowsAnyAsync<Exception>(() => _serializer.DeserializeAsync(stream));
    }

    private static ChannelReestablishPayload CreatePayload()
    {
        return new ChannelReestablishPayload(new ChannelId(Convert.FromHexString(ChannelIdHex)),
                                             new CompactPubKey(Convert.FromHexString(PointHex)), 5, 4,
                                             Convert.FromHexString(SecretHex));
    }

    private async Task<byte[]> SerializeAsync(ChannelReestablishMessage message)
    {
        using var stream = new MemoryStream();
        await _messageSerializer.SerializeAsync(message, stream);
        return stream.ToArray();
    }

    private async Task<ChannelReestablishMessage> DeserializeAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return Assert.IsType<ChannelReestablishMessage>(await _messageSerializer.DeserializeMessageAsync(stream));
    }
}
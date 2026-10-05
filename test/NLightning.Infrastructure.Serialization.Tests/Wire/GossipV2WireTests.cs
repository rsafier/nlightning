namespace NLightning.Infrastructure.Serialization.Tests.Wire;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Addresses;
using Domain.Protocol.Constants;
using Domain.Protocol.GossipV2;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Helpers;
using Infrastructure.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The taproot gossip wire definitions (BOLTs PR #1059 draft head <c>4eef3dfa</c>, NL-878): the four pure TLV
/// messages round-trip byte for byte, unknown odd records in either signed range are kept verbatim (the signature
/// covers them), the strict reader rejects unknown even types, bad fixed lengths, non-minimal truncated integers and
/// missing required records, and LND's own <c>lnwire</c> encode/decode vectors (lnd #11164 head <c>5082b81c</c>)
/// decode and re-encode identically.
/// </summary>
public class GossipV2WireTests
{
    // LND lnwire/channel_announcement_2_test.go TestChanAnn2EncodeDecode (lnd #11164 at 5082b81c)
    private const string LndChannelAnnouncement2Hex =
        "0020010101010101010101010101010101010101010101010101010101010101010102020102040800000100000200030502abcd06"
      + "030186a008210228f2af0abe322403480fb3ee172f7f1601e67d1da6cad40b54c4468d48236c390a210328f2af0abe322403480f"
      + "b3ee172f7f1601e67d1da6cad40b54c4468d48236c3912220102030405060708090a0b0c0d0e0f101112131415161718191a1b1c"
      + "1d1e1f2030396f027979f040000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20212223242526"
      + "2728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3ffe3b9aca01027979";

    // LND lnwire/node_announcement_2_test.go TestNodeAnn2EncodeDecode
    private const string LndNodeAnnouncement2Hex =
        "000201020103ff00800204000186a003204c696768746e696e67204e6f6465205465737420416c696173204e616d65210004210228"
      + "f2af0abe322403480fb3ee172f7f1601e67d1da6cad40b54c4468d48236c39050c0a00000123280a0000011f90071220010db8000"
      + "0000000000000000000011f4009250102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f2021222323280b"
      + "1900156c696768746e696e672e6578616d706c652e636f6d2328f040000102030405060708090a0b0c0d0e0f1011121314151617"
      + "18191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3ffe3b9aca01027979";

    // LND lnwire/channel_update_2_test.go TestChanUpdate2EncodeDecode; LND accepts its unknown EVEN record 0x18
    private const string LndChannelUpdate2Hex =
        "0020010101010101010101010101010101010101010101010101010101010101010102090100000100000200030404000001000601"
      + "010902abcd0a0200100c030f42400e030f4240100201001202010014010516010318027979f040000102030405060708090a0b0c0d"
      + "0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3ffe3b"
      + "9aca01027979";

    // LND lnwire/announcement_signatures_2_test.go TestAnnSigs2EncodeDecode; LND accepts its unknown EVEN record 0x30
    private const string LndAnnouncementSignatures2Hex =
        "00200000000000000000000000000000000000000000000000000000000000000000020800000100000200030440000000000000"
      + "00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
      + "00000000000006200102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f203002abcdfe3b9aca01027979";

    private readonly NLightning.Infrastructure.Serialization.Messages.MessageSerializer _serializer =
        new(NullLogger<NLightning.Infrastructure.Serialization.Messages.MessageSerializer>.Instance,
            SerializerHelper.MessageTypeSerializerFactory);

    private static readonly CompactPubKey s_node1 =
        new(Convert.FromHexString("0228f2af0abe322403480fb3ee172f7f1601e67d1da6cad40b54c4468d48236c39"));

    private static readonly CompactPubKey s_node2 =
        new(Convert.FromHexString("0328f2af0abe322403480fb3ee172f7f1601e67d1da6cad40b54c4468d48236c39"));

    private static byte[] WithType(MessageTypes type, ReadOnlySpan<byte> body)
    {
        var bytes = new byte[2 + body.Length];
        bytes[0] = (byte)((ushort)type >> 8);
        bytes[1] = (byte)type;
        body.CopyTo(bytes.AsSpan(2));
        return bytes;
    }

    private async Task<IMessage?> DecodeAsync(byte[] wire)
    {
        using var stream = new MemoryStream(wire);
        return await _serializer.DeserializeMessageAsync(stream);
    }

    private async Task<byte[]> EncodeAsync(IMessage message)
    {
        using var stream = new MemoryStream();
        await _serializer.SerializeAsync(message, stream);
        return stream.ToArray();
    }

    private async Task<TMessage> RoundTripAsync<TMessage>(MessageTypes type, string bodyHex) where TMessage : IMessage
    {
        var wire = WithType(type, Convert.FromHexString(bodyHex));
        var decoded = Assert.IsType<TMessage>(await DecodeAsync(wire));
        Assert.Equal(wire, await EncodeAsync(decoded));
        return decoded;
    }

    [Fact]
    public async Task Given_LndChannelAnnouncement2Vector_When_RoundTripped_Then_BytesAndFieldsMatch()
    {
        // Act
        var message = await RoundTripAsync<ChannelAnnouncement2Message>(MessageTypes.ChannelAnnouncement2,
                                                                        LndChannelAnnouncement2Hex);

        // Assert
        var payload = message.Payload;
        Assert.True(payload.HasChainHash);
        Assert.Equal(new ChainHash(Enumerable.Repeat((byte)1, 32).ToArray()), payload.ChainHash);
        Assert.Equal(new byte[] { 1, 2 }, payload.Features!.Value.ToArray());
        Assert.Equal(new ShortChannelId(1, 2, 3), payload.ShortChannelId);
        Assert.Equal(100_000UL, payload.CapacitySatoshis);
        Assert.Equal(s_node1, payload.NodeId1);
        Assert.Equal(s_node2, payload.NodeId2);
        Assert.Null(payload.BitcoinKey1);
        Assert.Null(payload.MerkleRootHash);
        Assert.Equal(new TxId(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()), payload.FundingTxId);
        Assert.Equal((ushort)12345, payload.FundingOutputIndex);

        // The signed range keeps the unknown odd records (5, 111, 1,000,000,001) and leaves the signature out
        var signed = payload.GetSignedData();
        Assert.Equal(Convert.FromHexString(LndChannelAnnouncement2Hex).Length - (2 + 64), signed.Length);
        Assert.Contains(payload.Stream.Records, r => r.Type == 1_000_000_001);
    }

    [Fact]
    public async Task Given_LndNodeAnnouncement2Vector_When_RoundTripped_Then_BytesAndAddressesMatch()
    {
        // Act
        var message = await RoundTripAsync<NodeAnnouncement2Message>(MessageTypes.NodeAnnouncement2,
                                                                     LndNodeAnnouncement2Hex);

        // Assert
        var payload = message.Payload;
        Assert.Equal(100_000U, payload.BlockHeight);
        Assert.Equal(s_node1, payload.NodeId);
        Assert.Equal(32, payload.Alias!.Value.Length);
        Assert.Equal(new byte[] { 0xff, 0x00, 0x80 }, payload.Color!.Value.ToArray());
        var addresses = payload.Addresses.ToList();
        Assert.Equal(5, addresses.Count);
        Assert.Equal("10.0.0.1", addresses[0].Host);
        Assert.Equal((ushort)9000, addresses[0].Port);
        Assert.Equal((ushort)8080, addresses[1].Port);
        Assert.Equal(AddressDescriptorType.IPv6, addresses[2].Type);
        Assert.Equal(AddressDescriptorType.TorV3, addresses[3].Type);
        Assert.Equal("lightning.example.com", addresses[4].Host);
    }

    [Fact]
    public async Task Given_LndChannelUpdate2VectorWithItsEvenRecordMadeOdd_When_RoundTripped_Then_BytesAndFieldsMatch()
    {
        // Arrange: LND tolerates the unknown even type 24 of its vector; BOLT 1 makes us refuse it (next test), so the
        // byte-exact check runs on the vector with 24 turned into the odd 25
        var hex = LndChannelUpdate2Hex.Replace("1601031802", "1601031902", StringComparison.Ordinal);
        Assert.NotEqual(LndChannelUpdate2Hex, hex);

        // Act
        var message = await RoundTripAsync<ChannelUpdate2Message>(MessageTypes.ChannelUpdate2, hex);

        // Assert
        var payload = message.Payload;
        Assert.Equal(new ShortChannelId(1, 2, 3), payload.ShortChannelId);
        Assert.Equal((byte)1, payload.Direction);
        Assert.Equal(256U, payload.BlockHeight);
        Assert.Equal((byte)1, payload.DisableFlags);
        Assert.Equal((ushort)16, payload.CltvExpiryDelta);
        Assert.Equal(1_000_000UL, payload.HtlcMinimumMsat);
        Assert.Equal(1_000_000UL, payload.HtlcMaximumMsat);
        Assert.Equal(256U, payload.FeeBaseMsat);
        Assert.Equal(256U, payload.FeeProportionalMillionths);
        Assert.Equal(5U, payload.InboundFeeBaseMsat);
        Assert.Equal(3U, payload.InboundFeeProportionalMillionths);
    }

    [Fact]
    public async Task Given_LndVectorsWithUnknownEvenRecords_When_Decoded_Then_TheStrictReaderRejectsThem()
    {
        // Act / Assert: BOLT 1, an unknown even type fails the stream (LND's lnwire tolerates it; drift noted in NL-878)
        await Assert.ThrowsAsync<MessageSerializationException>(() => DecodeAsync(
            WithType(MessageTypes.ChannelUpdate2, Convert.FromHexString(LndChannelUpdate2Hex))));
        await Assert.ThrowsAsync<MessageSerializationException>(() => DecodeAsync(
            WithType(MessageTypes.AnnouncementSignatures2, Convert.FromHexString(LndAnnouncementSignatures2Hex))));
    }

    [Fact]
    public async Task Given_LndAnnouncementSignatures2VectorWithItsEvenRecordMadeOdd_When_RoundTripped_Then_Matches()
    {
        // Arrange
        var hex = LndAnnouncementSignatures2Hex.Replace("203002abcd", "203102abcd", StringComparison.Ordinal);
        Assert.NotEqual(LndAnnouncementSignatures2Hex, hex);

        // Act
        var message = await RoundTripAsync<AnnouncementSignatures2Message>(MessageTypes.AnnouncementSignatures2, hex);

        // Assert: the funding txid lands in internal byte order, unchanged (as LND's test checks)
        Assert.Equal(new TxId(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()), message.Payload.FundingTxId);
        Assert.Equal(new ShortChannelId(1, 2, 3), message.Payload.ShortChannelId);
        Assert.Equal(new MusigPartialSignature(new byte[32]), message.Payload.NodePartialSignature);
    }

    [Fact]
    public async Task Given_OurBuiltMessages_When_RoundTripped_Then_EveryFieldSurvives()
    {
        // Arrange
        var announcement = ChannelAnnouncement2Payload.Create(
            ChainConstants.Regtest, [], new ShortChannelId(500, 1, 0), 1_000_000, s_node1, s_node2, s_node1, s_node2,
            [], new TxId(Enumerable.Repeat((byte)7, 32).ToArray()), 1);
        var update = ChannelUpdate2Payload.Create(ChainConstants.Regtest, new ShortChannelId(500, 1, 0), 1, 510,
                                                  ChannelUpdate2Payload.DisableIncoming, 40, 1_000, 990_000_000, 1000,
                                                  1);
        var node = NodeAnnouncement2Payload.Create([0x02, 0x00], 600, s_node1, [1, 2, 3], "alias"u8,
                                                   [AddressDescriptor.FromHost(AddressDescriptorType.IPv4,
                                                                               "127.0.0.1", 9735)]);
        var signatures = AnnouncementSignatures2Payload.Create(
            new ChannelId(Enumerable.Repeat((byte)9, 32).ToArray()), new ShortChannelId(500, 1, 0),
            new MusigPartialSignature(Enumerable.Repeat((byte)3, 32).ToArray()),
            new MusigPartialSignature(Enumerable.Repeat((byte)4, 32).ToArray()),
            new TxId(Enumerable.Repeat((byte)7, 32).ToArray()));

        // Act
        var a = Assert.IsType<ChannelAnnouncement2Message>(
            await DecodeAsync(await EncodeAsync(new ChannelAnnouncement2Message(announcement)))).Payload;
        var u = Assert.IsType<ChannelUpdate2Message>(
            await DecodeAsync(await EncodeAsync(new ChannelUpdate2Message(update)))).Payload;
        var n = Assert.IsType<NodeAnnouncement2Message>(
            await DecodeAsync(await EncodeAsync(new NodeAnnouncement2Message(node)))).Payload;
        var s = Assert.IsType<AnnouncementSignatures2Message>(
            await DecodeAsync(await EncodeAsync(new AnnouncementSignatures2Message(signatures)))).Payload;

        // Assert
        Assert.Equal(ChainConstants.Regtest, a.ChainHash);
        Assert.Null(a.Features);
        Assert.Equal(s_node1, a.BitcoinKey1);
        Assert.Equal(s_node2, a.BitcoinKey2);
        Assert.Equal(1_000_000UL, a.CapacitySatoshis);
        Assert.Equal((ushort)1, a.FundingOutputIndex);
        Assert.Equal(announcement.GetSignatureHash(), a.GetSignatureHash());

        Assert.Equal(510U, u.BlockHeight);
        Assert.Equal(ChannelUpdate2Payload.DisableIncoming, u.DisableFlags);
        Assert.Equal((ushort)40, u.CltvExpiryDelta);
        Assert.Equal(1_000UL, u.HtlcMinimumMsat);
        Assert.Equal(990_000_000UL, u.HtlcMaximumMsat);
        Assert.Equal(1000U, u.FeeBaseMsat);
        Assert.Equal(1U, u.FeeProportionalMillionths);
        // the defaults are left out on the wire (fee base 1000, proportional 1)
        Assert.False(u.Stream.Contains(GossipV2Constants.ChannelUpdate2.FeeBaseMsat));
        Assert.False(u.Stream.Contains(GossipV2Constants.ChannelUpdate2.FeeProportionalMillionths));

        Assert.Equal(600U, n.BlockHeight);
        Assert.Equal("alias"u8.ToArray(), n.Alias!.Value.ToArray());
        Assert.Equal("127.0.0.1", Assert.Single(n.Addresses).Host);

        Assert.Equal(signatures.ChannelId, s.ChannelId);
        Assert.Equal(signatures.BitcoinPartialSignature, s.BitcoinPartialSignature);
    }

    [Fact]
    public async Task Given_AnUnknownOddRecordInEachSignedRange_When_RoundTripped_Then_ItIsKeptAndSigned()
    {
        // Arrange: our update, plus 25 (first signed range), 241 (unsigned) and 2,000,000,001 (second signed range)
        var update = ChannelUpdate2Payload.Create(ChainConstants.Regtest, new ShortChannelId(9, 9, 9), 0, 100, 0, 80,
                                                  1, 5_000, 1000, 1);
        var stream = update.Stream.With(new PureTlvRecord(25, [0xaa]))
                           .With(new PureTlvRecord(241, [0xbb]))
                           .With(new PureTlvRecord(2_000_000_001, [0xcc]));
        var wire = WithType(MessageTypes.ChannelUpdate2, stream.GetBytes());

        // Act
        var decoded = Assert.IsType<ChannelUpdate2Message>(await DecodeAsync(wire));

        // Assert
        Assert.Equal(wire, await EncodeAsync(decoded));
        var signedTypes = PureTlvStream.Parse(decoded.Payload.GetSignedData(), new HashSet<ulong>([0, 2, 4, 14]))
                                       .Records.Select(r => r.Type).ToList();
        Assert.Contains(25UL, signedTypes);
        Assert.Contains(2_000_000_001UL, signedTypes);
        Assert.DoesNotContain(241UL, signedTypes);
        Assert.DoesNotContain(GossipV2Constants.SignatureType, signedTypes);
    }

    public static TheoryData<MessageTypes, string> MalformedBodies => new()
    {
        // unknown even type 32 in channel_announcement_2
        { MessageTypes.ChannelAnnouncement2, "2000" },
        // node_id_1 of 32 bytes
        { MessageTypes.ChannelAnnouncement2, "0820" + new string('0', 64) },
        // decreasing types
        { MessageTypes.NodeAnnouncement2, "040100" + "0201" + "00" },
        // a truncated record
        { MessageTypes.ChannelUpdate2, "0209000000" },
        // a non-canonical bigsize type (0xfd 0x00 0x05 encodes 5)
        { MessageTypes.ChannelUpdate2, "fd000501aa" },
        // block_height of 3 bytes
        { MessageTypes.ChannelUpdate2, "0403000000" }
    };

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task Given_AStreamBreakingABolt1Rule_When_Decoded_Then_ItFails(MessageTypes type, string bodyHex)
    {
        // Act / Assert
        await Assert.ThrowsAnyAsync<Exception>(() => DecodeAsync(WithType(type, Convert.FromHexString(bodyHex))));
    }

    [Fact]
    public async Task Given_MissingRequiredRecordsOrBadFields_When_Decoded_Then_PayloadSerializationExceptionIsThrown()
    {
        // Arrange
        var update = ChannelUpdate2Payload.Create(ChainConstants.Main, new ShortChannelId(9, 9, 9), 0, 100, 0, 80, 1,
                                                  5_000, 1000, 1);
        var withoutBlockHeight = new PureTlvStream(update.Stream.Records.Where(
                                                       r => r.Type != GossipV2Constants.ChannelUpdate2.BlockHeight));
        var nonMinimalTu64 = update.Stream.With(new PureTlvRecord(GossipV2Constants.ChannelUpdate2.HtlcMaximumMsat,
                                                                  [0x00, 0x05]));
        var pubkeyForm = update.Stream.With(new PureTlvRecord(GossipV2Constants.ChannelUpdate2.ShortChannelId,
                                                              [0x02, 0, 0, 0, 0, 0, 0, 0, 0]));

        // Act / Assert
        await Assert.ThrowsAsync<PayloadSerializationException>(() => DecodeAsync(
            WithType(MessageTypes.ChannelUpdate2, withoutBlockHeight.GetBytes())));
        await Assert.ThrowsAsync<PayloadSerializationException>(() => DecodeAsync(
            WithType(MessageTypes.ChannelUpdate2, nonMinimalTu64.GetBytes())));
        await Assert.ThrowsAsync<PayloadSerializationException>(() => DecodeAsync(
            WithType(MessageTypes.ChannelUpdate2, pubkeyForm.GetBytes())));
        await Assert.ThrowsAsync<PayloadSerializationException>(() => DecodeAsync(
            WithType(MessageTypes.NodeAnnouncement2, [])));
    }

    [Fact]
    public async Task Given_ChannelReadyAndSpliceLockedAndReestablishWithAnnouncementNonces_When_RoundTripped_Then_Kept()
    {
        // Arrange
        var nodeNonce = new MusigPublicNonce(Enumerable.Repeat((byte)2, 66).ToArray());
        var bitcoinNonce = new MusigPublicNonce(Enumerable.Repeat((byte)3, 66).ToArray());
        var channelId = new ChannelId(Enumerable.Repeat((byte)9, 32).ToArray());
        var ready = new ChannelReadyMessage(new ChannelReadyPayload(channelId, s_node1),
                                            new ShortChannelIdTlv(new ShortChannelId(1, 1, 1)), null,
                                            new AnnouncementNodeNonceTlv(nodeNonce),
                                            new AnnouncementBitcoinNonceTlv(bitcoinNonce));
        var locked = new SpliceLockedMessage(new SpliceLockedPayload(channelId, new TxId(new byte[32])),
                                             new AnnouncementNodeNonceTlv(nodeNonce),
                                             new AnnouncementBitcoinNonceTlv(bitcoinNonce));
        var reestablish = new ChannelReestablishMessage(
            new ChannelReestablishPayload(channelId, s_node1, 1, 0, new byte[32]), null,
            new MyCurrentFundingLockedTlv(new TxId(new byte[32]), MyCurrentFundingLockedTlv.AnnouncementSignatures2Flag),
            null, null, new AnnouncementNoncesTlv(nodeNonce, bitcoinNonce));

        // Act
        var r = Assert.IsType<ChannelReadyMessage>(await DecodeAsync(await EncodeAsync(ready)));
        var l = Assert.IsType<SpliceLockedMessage>(await DecodeAsync(await EncodeAsync(locked)));
        var e = Assert.IsType<ChannelReestablishMessage>(await DecodeAsync(await EncodeAsync(reestablish)));

        // Assert
        Assert.Equal(nodeNonce, r.AnnouncementNodeNonceTlv!.Nonce);
        Assert.Equal(bitcoinNonce, r.AnnouncementBitcoinNonceTlv!.Nonce);
        Assert.Equal(new ShortChannelId(1, 1, 1), r.ShortChannelIdTlv!.ShortChannelId);
        Assert.Equal(nodeNonce, l.AnnouncementNodeNonceTlv!.Nonce);
        Assert.Equal(bitcoinNonce, l.AnnouncementBitcoinNonceTlv!.Nonce);
        Assert.Equal(nodeNonce, e.AnnouncementNoncesTlv!.NodeNonce);
        Assert.Equal(bitcoinNonce, e.AnnouncementNoncesTlv!.BitcoinNonce);
        Assert.Equal(MyCurrentFundingLockedTlv.AnnouncementSignatures2Flag,
                     e.MyCurrentFundingLockedTlv!.RetransmitFlags);
    }

    [Fact]
    public async Task Given_AnAnnouncementNonceOfTheWrongLength_When_Decoded_Then_TheMessageFails()
    {
        // Arrange: channel_ready with a 65-byte announcement_node_pubnonce
        var channelId = new ChannelId(Enumerable.Repeat((byte)9, 32).ToArray());
        var body = new List<byte>(((ReadOnlySpan<byte>)channelId).ToArray());
        body.AddRange((byte[])s_node1);
        body.AddRange([0x00, 65]);
        body.AddRange(new byte[65]);

        // Act / Assert
        await Assert.ThrowsAsync<MessageSerializationException>(() => DecodeAsync(
            WithType(MessageTypes.ChannelReady, body.ToArray())));
    }
}
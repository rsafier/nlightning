namespace NLightning.Infrastructure.Serialization.Tests.Wire;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Helpers;
using Infrastructure.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Property-based round trips over the wire codec (plan <c>docs/agents/CODEC_REDESIGN_PLAN.md</c>): randomized
/// messages of the migrated slice decode back to equal values and re-encode byte-identically, and every
/// strict-TLV/BigSize/truncation rule of the hand-written codecs fails the new codec the same way. The exact-bytes
/// equivalence with the old codecs is pinned by the pre-existing round-trip and captured-vector tests, which assert
/// full hex and run against the same registry.
/// </summary>
public class WirePropertyTests
{
    private readonly Random _random = new(20261004);

    private readonly NLightning.Infrastructure.Serialization.Messages.MessageSerializer _serializer =
        new(NullLogger<NLightning.Infrastructure.Serialization.Messages.MessageSerializer>.Instance,
            SerializerHelper.WireRegistry);

    private async Task<(IMessage Decoded, byte[] Bytes)> RoundTripAsync(IMessage message)
    {
        using var encoded = new MemoryStream();
        await _serializer.SerializeAsync(message, encoded);
        var bytes = encoded.ToArray();

        using var decodeStream = new MemoryStream(bytes);
        var decoded = await _serializer.DeserializeMessageAsync(decodeStream);

        using var reencoded = new MemoryStream();
        await _serializer.SerializeAsync(decoded!, reencoded);

        Assert.Equal(bytes, reencoded.ToArray());
        return (decoded!, bytes);
    }

    private ChannelId RandomChannelId() => new(RandomBytes(32));

    private CompactSignature RandomSignature() => new(RandomBytes(CryptoConstants.MaxSignatureSize));

    private byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        _random.NextBytes(bytes);
        return bytes;
    }

    private CompactPubKey RandomPoint()
    {
        var bytes = RandomBytes(CryptoConstants.CompactPubkeyLen);
        bytes[0] = (byte)(_random.Next(2) == 0 ? 0x02 : 0x03);
        return new CompactPubKey(bytes);
    }

    [Fact]
    public async Task Given_RandomUpdateAddHtlc_When_RoundTripped_Then_ValuesEqualAndBytesStable()
    {
        for (var i = 0; i < 16; i++)
        {
            var payload = new UpdateAddHtlcPayload(LightningMoney.MilliSatoshis((ulong)_random.NextInt64(1, 1_000_000)),
                                                   RandomChannelId(), (uint)_random.Next(0, 1 << 20),
                                                   (ulong)_random.NextInt64(0, 1 << 30), RandomBytes(32),
                                                   RandomBytes(Domain.Protocol.Onion.Constants.OnionConstants
                                                                   .PacketLength));
            var (decoded, _) = await RoundTripAsync(new UpdateAddHtlcMessage(payload));
            var back = Assert.IsType<UpdateAddHtlcMessage>(decoded).Payload;
            Assert.Equal(payload.ChannelId, back.ChannelId);
            Assert.Equal(payload.Id, back.Id);
            Assert.Equal(payload.Amount, back.Amount);
            Assert.Equal(payload.PaymentHash.ToArray(), back.PaymentHash.ToArray());
            Assert.Equal(payload.CltvExpiry, back.CltvExpiry);
            Assert.Equal(payload.OnionRoutingPacket.ToArray(), back.OnionRoutingPacket.ToArray());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(17)]
    public async Task Given_RandomCommitmentSigned_When_RoundTripped_Then_ValuesEqualAndBytesStable(int htlcCount)
    {
        for (var i = 0; i < 8; i++)
        {
            var htlcSignatures = new List<CompactSignature>();
            for (var j = 0; j < htlcCount; j++)
                htlcSignatures.Add(RandomSignature());
            var payload = new CommitmentSignedPayload(RandomChannelId(), htlcSignatures, RandomSignature());
            var fundingTxIdTlv = i % 2 == 0 ? new FundingTxIdTlv(new TxId(RandomBytes(32))) : null;

            var (decoded, _) = await RoundTripAsync(new CommitmentSignedMessage(payload, fundingTxIdTlv));
            var back = Assert.IsType<CommitmentSignedMessage>(decoded).Payload;
            Assert.Equal(payload.ChannelId, back.ChannelId);
            Assert.Equal(payload.Signature, back.Signature);
            Assert.Equal(payload.HtlcSignatures, back.HtlcSignatures);
            Assert.Equal(fundingTxIdTlv, Assert.IsType<CommitmentSignedMessage>(decoded).FundingTxIdTlv);
        }
    }

    [Fact]
    public async Task Given_RandomUpdateFee_When_RoundTripped_Then_ValuesEqualAndBytesStable()
    {
        for (var i = 0; i < 32; i++)
        {
            var channelId = RandomChannelId();
            var feeratePerKw = (uint)_random.Next(253, 100_000);
            var (decoded, _) = await RoundTripAsync(new UpdateFeeMessage(new UpdateFeePayload(channelId, feeratePerKw)));
            var back = Assert.IsType<UpdateFeeMessage>(decoded).Payload;
            Assert.Equal(channelId, back.ChannelId);
            Assert.Equal(feeratePerKw, back.FeeratePerKw);
        }
    }

    [Fact]
    public async Task Given_RandomChannelReestablish_When_RoundTripped_Then_ValuesEqualAndBytesStable()
    {
        for (var i = 0; i < 16; i++)
        {
            var payload = new ChannelReestablishPayload(RandomChannelId(), RandomPoint(),
                                                        (ulong)_random.NextInt64(0, 1 << 30),
                                                        (ulong)_random.NextInt64(0, 1 << 30), RandomBytes(32));
            var nextFundingTlv = i % 3 == 0 ? new NextFundingTlv(RandomBytes(32), (byte)_random.Next(0, 3)) : null;
            var myCurrentFundingLockedTlv = i % 3 == 1
                ? new MyCurrentFundingLockedTlv(new TxId(RandomBytes(32)), 1)
                : null;

            var (decoded, _) = await RoundTripAsync(new ChannelReestablishMessage(payload, nextFundingTlv,
                                                                                  myCurrentFundingLockedTlv));
            var message = Assert.IsType<ChannelReestablishMessage>(decoded);
            Assert.Equal(payload.ChannelId, message.Payload.ChannelId);
            Assert.Equal(payload.NextCommitmentNumber, message.Payload.NextCommitmentNumber);
            Assert.Equal(payload.NextRevocationNumber, message.Payload.NextRevocationNumber);
            Assert.Equal(payload.YourLastPerCommitmentSecret.ToArray(),
                         message.Payload.YourLastPerCommitmentSecret.ToArray());
            Assert.Equal(payload.MyCurrentPerCommitmentPoint, message.Payload.MyCurrentPerCommitmentPoint);
            Assert.Equal(nextFundingTlv, message.NextFundingTlv);
            Assert.Equal(myCurrentFundingLockedTlv, message.MyCurrentFundingLockedTlv);
        }
    }

    [Fact]
    public async Task Given_RandomTxAddInput_When_RoundTripped_Then_ValuesEqualAndBytesStable()
    {
        for (var i = 0; i < 16; i++)
        {
            var payload = new TxAddInputPayload(RandomChannelId(), (ulong)_random.NextInt64(0, 1L << 40),
                                                RandomBytes(_random.Next(60, 250)), (uint)_random.Next(0, 64),
                                                (uint)_random.Next(0, 0xFFFFFD));
            var sharedInputTxIdTlv = i % 2 == 0 ? new SharedInputTxIdTlv(new TxId(RandomBytes(32))) : null;

            var (decoded, _) = await RoundTripAsync(new TxAddInputMessage(payload, sharedInputTxIdTlv));
            var message = Assert.IsType<TxAddInputMessage>(decoded);
            Assert.Equal(payload.ChannelId, message.Payload.ChannelId);
            Assert.Equal(payload.SerialId, message.Payload.SerialId);
            Assert.Equal(payload.PrevTx, message.Payload.PrevTx);
            Assert.Equal(payload.PrevTxVout, message.Payload.PrevTxVout);
            Assert.Equal(payload.Sequence, message.Payload.Sequence);
            Assert.Equal(sharedInputTxIdTlv, message.SharedInputTxIdTlv);
        }
    }

    [Fact]
    public async Task Given_RandomErrorWarningPingPong_When_RoundTripped_Then_ValuesEqualAndBytesStable()
    {
        for (var i = 0; i < 16; i++)
        {
            var data = RandomBytes(_random.Next(0, 128));
            var (error, _) = await RoundTripAsync(new ErrorMessage(new ErrorPayload(RandomChannelId(), data)));
            Assert.Equal(data, Assert.IsType<ErrorMessage>(error).Payload.Data);

            var (warning, _) = await RoundTripAsync(new WarningMessage(new ErrorPayload(RandomChannelId(), data)));
            Assert.Equal(data, Assert.IsType<WarningMessage>(warning).Payload.Data);

            var ignored = RandomBytes(_random.Next(0, 256));
            var (pong, _) = await RoundTripAsync(
                new PongMessage(new PongPayload((ushort)ignored.Length) { Ignored = ignored }));
            Assert.Equal(ignored, Assert.IsType<PongMessage>(pong).Payload.Ignored);
        }
    }

    [Fact]
    public async Task Given_RandomInit_When_RoundTripped_Then_ValuesEqualAndBytesStable()
    {
        for (var i = 0; i < 8; i++)
        {
            var features = FeatureSet.DeserializeFromBytes([0x01, 0xA2, (byte)_random.Next(0, 255)]);
            var networksTlv = i % 2 == 0
                ? new NetworksTlv([new ChainHash(RandomBytes(32).AsSpan())])
                : null;

            var (decoded, _) = await RoundTripAsync(new InitMessage(new InitPayload(features), networksTlv));
            var message = Assert.IsType<InitMessage>(decoded);
            Assert.Equal(features.GetWireBytes(), message.Payload.FeatureSet.GetWireBytes());
            if (networksTlv is null)
                Assert.Null(message.NetworksTlv);
            else
                Assert.Equal(networksTlv.Value, message.NetworksTlv!.Value);
        }
    }

    [Fact]
    public async Task Given_RandomRevokeAndAck_When_RoundTripped_Then_ValuesEqualAndBytesStable()
    {
        for (var i = 0; i < 16; i++)
        {
            var payload = new RevokeAndAckPayload(RandomChannelId(), RandomPoint(), RandomBytes(32));
            var (decoded, _) = await RoundTripAsync(new RevokeAndAckMessage(payload));
            var back = Assert.IsType<RevokeAndAckMessage>(decoded).Payload;
            Assert.Equal(payload.ChannelId, back.ChannelId);
            Assert.Equal(payload.PerCommitmentSecret.ToArray(), back.PerCommitmentSecret.ToArray());
            Assert.Equal(payload.NextPerCommitmentPoint, back.NextPerCommitmentPoint);
        }
    }

    [Fact]
    public async Task Given_RandomFulfillFailMalformed_When_RoundTripped_Then_ValuesEqualAndBytesStable()
    {
        for (var i = 0; i < 16; i++)
        {
            var fulfillPayload = new UpdateFulfillHtlcPayload(RandomChannelId(),
                                                              (ulong)_random.NextInt64(0, 1 << 30), RandomBytes(32));
            var (fulfilled, _) = await RoundTripAsync(new UpdateFulfillHtlcMessage(fulfillPayload));
            var fulfilledBack = Assert.IsType<UpdateFulfillHtlcMessage>(fulfilled).Payload;
            Assert.Equal(fulfillPayload.ChannelId, fulfilledBack.ChannelId);
            Assert.Equal(fulfillPayload.Id, fulfilledBack.Id);
            Assert.Equal(fulfillPayload.PaymentPreimage.ToArray(), fulfilledBack.PaymentPreimage.ToArray());

            var reason = RandomBytes(_random.Next(0, 512));
            var failPayload = new UpdateFailHtlcPayload(RandomChannelId(), (ulong)_random.NextInt64(0, 1 << 30),
                                                        reason);
            var (failed, _) = await RoundTripAsync(new UpdateFailHtlcMessage(failPayload));
            Assert.Equal(reason, Assert.IsType<UpdateFailHtlcMessage>(failed).Payload.Reason.ToArray());

            var malformedPayload = new UpdateFailMalformedHtlcPayload(RandomChannelId(),
                                                                      (ushort)_random.Next(0, ushort.MaxValue),
                                                                      (ulong)_random.NextInt64(0, 1 << 30),
                                                                      RandomBytes(32));
            var (malformed, _) = await RoundTripAsync(new UpdateFailMalformedHtlcMessage(malformedPayload));
            Assert.Equal(malformedPayload.FailureCode, Assert.IsType<UpdateFailMalformedHtlcMessage>(malformed)
                                                       .Payload.FailureCode);
        }
    }

    // ---- strictness of the new codec's extension reader ----

    private static async Task<Exception> DeserializeAsync(string bodyHex)
    {
        try
        {
            await SerializerHelper.WireRegistry.Get<RevokeAndAckMessage>()!
               .DeserializeAsync(new MemoryStream(Convert.FromHexString(bodyHex)));
            return new InvalidOperationException("expected a failure");
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private const string RevokeAndAckBody =
        "0000000000000000000000000000000000000000000000000000000000000000" // channel_id
      + "C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75" // per_commitment_secret
      + "02C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75"; // next_per_commitment_point

    [Fact]
    public async Task Given_UnknownEvenExtensionTlv_When_Deserialized_Then_Fails()
    {
        // type 0xCA (even, unknown), length 1, value 0x2A
        var e = await DeserializeAsync(RevokeAndAckBody + "CA012A");
        Assert.True(e is MessageSerializationException, e.ToString());
    }

    [Fact]
    public async Task Given_UnknownOddExtensionTlv_When_Deserialized_Then_Ignored()
    {
        // type 0xC9 (odd, unknown) is ignored: the message still parses
        var message = await SerializerHelper.WireRegistry.Get<RevokeAndAckMessage>()!
           .DeserializeAsync(new MemoryStream(Convert.FromHexString(RevokeAndAckBody + "C9012A")));
        Assert.IsType<RevokeAndAckMessage>(message);
        Assert.NotNull(message);
        Assert.Null(Assert.IsType<RevokeAndAckMessage>(message).NextLocalNoncesTlv);
    }

    [Fact]
    public async Task Given_DecreasingExtensionTlvTypes_When_Deserialized_Then_Fails()
    {
        // type 5 before type 1
        var tlvHex = "0500" + "0100";
        var e = await DeserializeAsync(RevokeAndAckBody + tlvHex);
        Assert.IsType<MessageSerializationException>(e);
    }

    [Fact]
    public async Task Given_ExtensionLengthPastEnd_When_Deserialized_Then_Fails()
    {
        // type 1, length 99, but 0 bytes of value follow
        var e = await DeserializeAsync(RevokeAndAckBody + "0163");
        Assert.IsType<MessageSerializationException>(e);
    }

    [Fact]
    public async Task Given_NonCanonicalBigSizeType_When_Deserialized_Then_Fails()
    {
        // type 1 encoded non-canonically as FD 0001
        var e = await DeserializeAsync(RevokeAndAckBody + "FD000100");
        Assert.IsType<MessageSerializationException>(e);
    }

    [Fact]
    public async Task Given_TruncatedBody_When_Deserialized_Then_Fails()
    {
        var e = await DeserializeAsync(RevokeAndAckBody[..80]);
        Assert.IsType<PayloadSerializationException>(e);
    }
}
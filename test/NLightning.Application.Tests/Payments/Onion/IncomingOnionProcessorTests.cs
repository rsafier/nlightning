using System.Security.Cryptography;

namespace NLightning.Application.Tests.Payments.Onion;

using Application.Payments.Onion;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;

/// <summary>
/// ONION M4-T2 edge cases, with real Sphinx onions built from hand-made hop payloads.
/// </summary>
public class IncomingOnionProcessorTests : IDisposable
{
    private static readonly Hash s_paymentHash = Enumerable.Repeat((byte)0x77, 32).ToArray();
    private static readonly Secret s_paymentSecret = Enumerable.Repeat((byte)0x78, 32).ToArray();

    private readonly PaymentsTestNode _sender = new("sender", 0x31);
    private readonly PaymentsTestNode _bob = new("bob", 0x32);
    private readonly PaymentsTestNode _carol = new("carol", 0x33);

    public void Dispose()
    {
        _sender.Dispose();
        _bob.Dispose();
        _carol.Dispose();
    }

    private async Task<byte[]> SerializeAsync(HopPayload payload)
    {
        using var stream = new MemoryStream();
        await _sender.HopPayloadSerializer.SerializeAsync(payload, stream);
        return stream.ToArray();
    }

    /// <summary>A one-hop onion to Bob, or a two-hop onion Bob → Carol with <paramref name="carolPayload"/>.</summary>
    private byte[] BuildOnion(byte[] bobPayload, byte[]? carolPayload = null)
    {
        var hops = new List<OnionHop> { new(_bob.NodeId, bobPayload) };
        if (carolPayload is not null)
            hops.Add(new OnionHop(_carol.NodeId, carolPayload));

        var sessionKey = new PrivKey(Enumerable.Repeat((byte)0x05, 32).ToArray());
        return _sender.Sphinx.Construct(hops, sessionKey, s_paymentHash).ToBytes();
    }

    private static HopPayload FinalPayload() =>
        new(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1_000)), new OutgoingCltvValueTlv(500),
            new PaymentDataTlv(s_paymentSecret, LightningMoney.MilliSatoshis(1_000)));

    private static HopPayload ForwardPayload(bool withShortChannelId = true)
    {
        var tlvs = new List<BaseTlv>
        {
            new AmtToForwardTlv(LightningMoney.MilliSatoshis(1_000)),
            new OutgoingCltvValueTlv(500)
        };
        if (withShortChannelId)
            tlvs.Add(new OnionShortChannelIdTlv(new ShortChannelId(1, 2, 3)));

        return new HopPayload(tlvs.ToArray());
    }

    [Fact]
    public async Task Given_FinalPayload_When_Processed_Then_Final()
    {
        // Arrange
        var onion = BuildOnion(await SerializeAsync(FinalPayload()));

        // Act
        var result = await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash);

        // Assert
        var final = Assert.IsType<IncomingOnionFinal>(result);
        Assert.Equal(1_000UL, final.Payload.AmtToForward!.MilliSatoshi);
        Assert.Equal(final.SharedSecret, result.SharedSecretOrNull);
    }

    [Fact]
    public async Task Given_WrongPaymentHash_When_Processed_Then_MalformedInvalidOnionHmac()
    {
        // Arrange: the payment hash is the associated data of the HMAC
        var onion = BuildOnion(await SerializeAsync(FinalPayload()));

        // Act
        var result = await _bob.OnionProcessor.ProcessAsync(onion, Enumerable.Repeat((byte)0x76, 32).ToArray());

        // Assert
        var malformed = Assert.IsType<IncomingOnionMalformed>(result);
        Assert.Equal(FailureCode.InvalidOnionHmac, malformed.FailureCode);
        Assert.Equal(SHA256.HashData(onion), malformed.Sha256OfOnion.ToArray());
    }

    [Fact]
    public async Task Given_UnknownVersion_When_Processed_Then_MalformedInvalidOnionVersion()
    {
        // Arrange
        var onion = BuildOnion(await SerializeAsync(FinalPayload()));
        onion[0] = 1;

        // Act
        var result = await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash);

        // Assert
        var malformed = Assert.IsType<IncomingOnionMalformed>(result);
        Assert.Equal(FailureCode.InvalidOnionVersion, malformed.FailureCode);
        Assert.Equal(SHA256.HashData(onion), malformed.Sha256OfOnion.ToArray());
    }

    [Fact]
    public async Task Given_UpdateAddPathKey_When_Processed_Then_MalformedInvalidOnionBlinding()
    {
        // Arrange: route blinding (M5) is not supported; with a path_key every error is invalid_onion_blinding
        var onion = BuildOnion(await SerializeAsync(FinalPayload()));

        // Act
        var result = await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash, _carol.NodeId);

        // Assert
        var malformed = Assert.IsType<IncomingOnionMalformed>(result);
        Assert.Equal(FailureCode.InvalidOnionBlinding, malformed.FailureCode);
        Assert.Equal(SHA256.HashData(onion), malformed.Sha256OfOnion.ToArray());
    }

    [Fact]
    public async Task Given_IntermediatePayloadWithoutShortChannelId_When_Processed_Then_InvalidOnionPayloadType6()
    {
        // Arrange
        var onion = BuildOnion(await SerializeAsync(ForwardPayload(withShortChannelId: false)),
                               await SerializeAsync(FinalPayload()));

        // Act
        var result = await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash);

        // Assert
        var failed = Assert.IsType<IncomingOnionFailed>(result);
        Assert.Equal(FailureCode.InvalidOnionPayload, failed.Failure.Code);
        Assert.Equal(OnionPayloadTlvTypes.ShortChannelId, failed.Failure.InvalidPayloadType);
    }

    [Fact]
    public async Task Given_FinalPayloadWithoutPaymentData_When_Processed_Then_InvalidOnionPayloadType8()
    {
        // Arrange
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1_000)),
                                     new OutgoingCltvValueTlv(500));
        var onion = BuildOnion(await SerializeAsync(payload));

        // Act
        var result = await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash);

        // Assert
        var failed = Assert.IsType<IncomingOnionFailed>(result);
        Assert.Equal(FailureCode.InvalidOnionPayload, failed.Failure.Code);
        Assert.Equal(OnionPayloadTlvTypes.PaymentData, failed.Failure.InvalidPayloadType);
    }

    [Fact]
    public async Task Given_UnknownEvenTlvInPayload_When_Processed_Then_InvalidOnionPayloadWithTypeAndOffset()
    {
        // Arrange: amt_to_forward (2), outgoing_cltv_value (4), then unknown even type 100
        var payload = new HopPayload(new AmtToForwardTlv(LightningMoney.MilliSatoshis(1_000)),
                                     new OutgoingCltvValueTlv(500),
                                     new PaymentDataTlv(s_paymentSecret, LightningMoney.MilliSatoshis(1_000)),
                                     new BaseTlv(100, [1]));
        var onion = BuildOnion(await SerializeAsync(payload));

        // Act
        var result = await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash);

        // Assert
        var failed = Assert.IsType<IncomingOnionFailed>(result);
        Assert.Equal(FailureCode.InvalidOnionPayload, failed.Failure.Code);
        Assert.Equal(100UL, failed.Failure.InvalidPayloadType!.Value.Value);
        Assert.True(failed.Failure.InvalidPayloadOffset > 0);
    }

    [Fact]
    public async Task Given_PayloadWithCurrentPathKey_When_Processed_Then_UpdateFailHtlcWithInvalidOnionBlinding()
    {
        // Arrange: we would be the introduction point of a blinded route, which we do not support yet
        var payload = new HopPayload(new EncryptedRecipientDataTlv(new byte[] { 1, 2, 3 }),
                                     new CurrentPathKeyTlv(_carol.NodeId));
        var onion = BuildOnion(await SerializeAsync(payload), await SerializeAsync(FinalPayload()));

        // Act
        var result = await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash);

        // Assert
        var failed = Assert.IsType<IncomingOnionFailed>(result);
        Assert.Equal(FailureCode.InvalidOnionBlinding, failed.Failure.Code);
        Assert.Equal(SHA256.HashData(onion), failed.Failure.Sha256OfOnion!.Value.ToArray());
    }

    [Fact]
    public async Task Given_ForwardPayload_When_Processed_Then_ForwardCarriesNextPacketForCarol()
    {
        // Arrange
        var onion = BuildOnion(await SerializeAsync(ForwardPayload()), await SerializeAsync(FinalPayload()));

        // Act
        var atBob = await _bob.OnionProcessor.ProcessAsync(onion, s_paymentHash);
        var forward = Assert.IsType<IncomingOnionForward>(atBob);
        var atCarol = await _carol.OnionProcessor.ProcessAsync(forward.NextPacket, s_paymentHash);

        // Assert
        Assert.Equal(new ShortChannelId(1, 2, 3), forward.OutgoingShortChannelId);
        Assert.Equal(500U, forward.OutgoingCltvValue);
        Assert.IsType<IncomingOnionFinal>(atCarol);
    }

    [Fact]
    public async Task Given_ShortPacket_When_Processed_Then_ArgumentException()
    {
        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() => _bob.OnionProcessor.ProcessAsync(new byte[100],
                                                                                            s_paymentHash));
    }
}
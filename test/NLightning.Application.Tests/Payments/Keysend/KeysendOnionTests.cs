namespace NLightning.Application.Tests.Payments.Keysend;

using Application.Payments.Keysend;
using Application.Payments.Onion;
using Application.Payments.Routing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Keysend;
using Domain.Protocol.Onion.Models;

/// <summary>
/// The keysend final payload (lane lh1-l3): <c>keysend_preimage</c> and the custom records instead of
/// <c>payment_data</c>, peeled at the payee by the production onion processor and accepted by the validator.
/// </summary>
public class KeysendOnionTests : IDisposable
{
    private static readonly Secret s_preimage = Enumerable.Repeat((byte)0x5A, 32).ToArray();
    private static readonly Hash s_paymentHash = System.Security.Cryptography.SHA256.HashData((byte[])s_preimage);
    private static readonly ShortChannelId s_scid = new(200, 3, 1);
    private static readonly OnionReplayOwner s_replayOwner = new(ChannelId.Zero, 0, 1_000);

    private readonly PaymentsTestNode _alice = new("alice", 0x31);
    private readonly PaymentsTestNode _bob = new("bob", 0x32);
    private readonly PaymentsTestNode _carol = new("carol", 0x33);

    public void Dispose()
    {
        _alice.Dispose();
        _bob.Dispose();
        _carol.Dispose();
    }

    private PaymentRoute TwoHopRoute() =>
        new([
                new RouteHop(_bob.NodeId, LightningMoney.MilliSatoshis(10_000), 560, s_scid),
                new RouteHop(_carol.NodeId, LightningMoney.MilliSatoshis(10_000), 560, null)
            ], LightningMoney.MilliSatoshis(11_001), 600, s_paymentHash, new Secret(new byte[32]));

    [Fact]
    public async Task Given_KeysendRecords_When_OnionCreated_Then_PayeeReadsPreimageAndCustomRecordsWithoutPaymentData()
    {
        // Arrange
        CustomRecord[] records = [new(65536, [0x01, 0x02]), new(7629169, "hi"u8)];
        var keysend = new KeysendFinalRecords(s_preimage, records);

        // Act
        var onion = await _alice.OnionFactory.CreateAsync(TwoHopRoute(), keysend);
        var atBob = await _bob.OnionProcessor.ProcessAsync(onion.Packet, s_paymentHash, s_replayOwner);
        var forward = Assert.IsType<IncomingOnionForward>(atBob);
        var atCarol = await _carol.OnionProcessor.ProcessAsync(forward.NextPacket, s_paymentHash, s_replayOwner);

        // Assert: Bob's payload carries nothing of it, Carol's everything but payment_data
        var final = Assert.IsType<IncomingOnionFinal>(atCarol);
        Assert.Null(final.Payload.PaymentData);
        Assert.Equal((byte[])s_preimage, final.Payload.KeysendPreimage!.Value.ToArray());
        Assert.Equal(keysend.CustomRecords, final.Payload.CustomRecords);
        Assert.True(KeysendReceiver.IsKeysend(final.Payload));
    }

    [Fact]
    public void Given_KeysendRecords_When_IntermediatePayloadCreated_Then_NoKeysendRecords()
    {
        // Arrange
        var route = TwoHopRoute();
        var keysend = new KeysendFinalRecords(s_preimage, []);

        // Act
        var payload = PaymentOnionFactory.CreatePayload(route.Hops[0], route, keysend);

        // Assert
        Assert.Null(payload.KeysendPreimage);
        Assert.Empty(payload.CustomRecords);
        Assert.Equal(s_scid, payload.ShortChannelId);
    }
}
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

    [Fact]
    public async Task Given_RecordsThatFitTheLastLayerOnly_When_Measured_Then_TheRouteIsTooLongAndTheBuilderAgrees()
    {
        // Arrange: about 1,250 bytes of records fit the payee's layer alone, not with Bob's layer before it
        var keysend = new KeysendFinalRecords(s_preimage, [new CustomRecord(65537, new byte[1_200])]);
        var route = TwoHopRoute();

        // Act
        var finalOnly = await _alice.OnionFactory.GetKeysendFinalFramedLengthAsync(route.Amount, 560, keysend);
        var whole = await _alice.OnionFactory.GetFramedLengthAsync(route, keysend);

        // Assert: the measure matches the Sphinx framing, so the builder refuses exactly what it says does not fit
        Assert.True(finalOnly <= Domain.Protocol.Onion.Constants.OnionConstants.HopPayloadsLength);
        Assert.True(whole > Domain.Protocol.Onion.Constants.OnionConstants.HopPayloadsLength);
        await Assert.ThrowsAsync<ArgumentException>(() => _alice.OnionFactory.CreateAsync(route, keysend));
        var direct = new PaymentRoute([new RouteHop(_carol.NodeId, route.Amount, 560, null)], route.Amount, 560,
                                      s_paymentHash, new Secret(new byte[32]));
        Assert.Equal(finalOnly, await _alice.OnionFactory.GetFramedLengthAsync(direct, keysend));
        await _alice.OnionFactory.CreateAsync(direct, keysend);
    }

    [Fact]
    public async Task Given_ARouteWhoseLayersFillTheOnionExactly_When_Measured_Then_ItFitsAndOneByteMoreDoesNot()
    {
        // Arrange: size the record so the direct route takes exactly 1300 bytes
        var probe = new KeysendFinalRecords(s_preimage, [new CustomRecord(65537, new byte[300])]);
        var direct = new PaymentRoute([new RouteHop(_carol.NodeId, LightningMoney.MilliSatoshis(10_000), 560, null)],
                                      LightningMoney.MilliSatoshis(10_000), 560, s_paymentHash,
                                      new Secret(new byte[32]));
        var spare = Domain.Protocol.Onion.Constants.OnionConstants.HopPayloadsLength
                  - await _alice.OnionFactory.GetFramedLengthAsync(direct, probe);
        var exact = new KeysendFinalRecords(s_preimage, [new CustomRecord(65537, new byte[300 + spare])]);
        var over = new KeysendFinalRecords(s_preimage, [new CustomRecord(65537, new byte[301 + spare])]);

        // Act
        var exactLength = await _alice.OnionFactory.GetFramedLengthAsync(direct, exact);
        var overLength = await _alice.OnionFactory.GetFramedLengthAsync(direct, over);

        // Assert
        Assert.Equal(Domain.Protocol.Onion.Constants.OnionConstants.HopPayloadsLength, exactLength);
        await _alice.OnionFactory.CreateAsync(direct, exact);
        Assert.True(overLength > Domain.Protocol.Onion.Constants.OnionConstants.HopPayloadsLength);
        await Assert.ThrowsAsync<ArgumentException>(() => _alice.OnionFactory.CreateAsync(direct, over));
    }
}
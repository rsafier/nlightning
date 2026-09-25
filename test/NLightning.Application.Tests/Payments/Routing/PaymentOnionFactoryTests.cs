namespace NLightning.Application.Tests.Payments.Routing;

using Application.Payments.Onion;
using Application.Payments.Routing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Constants;

/// <summary>
/// ONION M4-T6: hop payloads per the BOLT 4 writer rules and a fresh CSPRNG session key per onion.
/// </summary>
public class PaymentOnionFactoryTests : IDisposable
{
    private static readonly Hash s_paymentHash = Enumerable.Repeat((byte)0x33, 32).ToArray();
    private static readonly Secret s_paymentSecret = Enumerable.Repeat((byte)0x44, 32).ToArray();
    private static readonly ShortChannelId s_scid = new(200, 3, 1);

    private readonly PaymentsTestNode _alice = new("alice", 0x21);
    private readonly PaymentsTestNode _bob = new("bob", 0x22);
    private readonly PaymentsTestNode _carol = new("carol", 0x23);

    public void Dispose()
    {
        _alice.Dispose();
        _bob.Dispose();
        _carol.Dispose();
    }

    private PaymentRoute TwoHopRoute(ReadOnlyMemory<byte>? metadata = null) =>
        new([
                new RouteHop(_bob.NodeId, LightningMoney.MilliSatoshis(10_000), 560, s_scid),
                new RouteHop(_carol.NodeId, LightningMoney.MilliSatoshis(10_000), 560, null)
            ], LightningMoney.MilliSatoshis(11_001), 600, s_paymentHash, s_paymentSecret, metadata);

    [Fact]
    public void Given_IntermediateHop_When_PayloadCreated_Then_ShortChannelIdAndNoPaymentData()
    {
        // Arrange
        var route = TwoHopRoute();

        // Act
        var payload = PaymentOnionFactory.CreatePayload(route.Hops[0], route);

        // Assert
        Assert.Equal(s_scid, payload.ShortChannelId);
        Assert.Equal(10_000UL, payload.AmtToForward!.MilliSatoshi);
        Assert.Equal(560U, payload.OutgoingCltvValue);
        Assert.Null(payload.PaymentData);
        Assert.Null(payload.PaymentMetadata);
    }

    [Fact]
    public void Given_FinalHop_When_PayloadCreated_Then_PaymentDataWithSecretAndTotalEqualToAmount()
    {
        // Arrange
        var route = TwoHopRoute(new byte[] { 1, 2, 3 });

        // Act
        var payload = PaymentOnionFactory.CreatePayload(route.Hops[1], route);

        // Assert
        Assert.Null(payload.ShortChannelId);
        Assert.NotNull(payload.PaymentData);
        Assert.Equal(s_paymentSecret, payload.PaymentData.PaymentSecret);
        Assert.Equal(payload.AmtToForward, payload.PaymentData.TotalMsat);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload.PaymentMetadata!.Value.ToArray());
    }

    [Fact]
    public async Task Given_Route_When_OnionCreated_Then_EachHopPeelsItsOwnPayload()
    {
        // Arrange
        var route = TwoHopRoute(new byte[] { 9 });

        // Act
        var onion = await _alice.OnionFactory.CreateAsync(route);
        var atBob = await _bob.OnionProcessor.ProcessAsync(onion.Packet, s_paymentHash);
        var bobForward = Assert.IsType<IncomingOnionForward>(atBob);
        var atCarol = await _carol.OnionProcessor.ProcessAsync(bobForward.NextPacket, s_paymentHash);

        // Assert
        Assert.Equal(OnionConstants.PacketLength, onion.Packet.Length);
        Assert.Equal(2, onion.SharedSecrets.Count);
        Assert.Equal(s_scid, bobForward.OutgoingShortChannelId);
        var carolFinal = Assert.IsType<IncomingOnionFinal>(atCarol);
        Assert.Equal(new byte[] { 9 }, carolFinal.Payload.PaymentMetadata!.Value.ToArray());
    }

    [Fact]
    public async Task Given_SameRouteTwice_When_OnionsCreated_Then_SessionKeysDiffer()
    {
        // Arrange
        var route = TwoHopRoute();

        // Act
        var first = await _alice.OnionFactory.CreateAsync(route);
        var second = await _alice.OnionFactory.CreateAsync(route);

        // Assert: a different ephemeral key means a different session key
        Assert.NotEqual(first.Packet.PublicKey.ToArray(), second.Packet.PublicKey.ToArray());
        Assert.NotEqual(first.SharedSecrets[0], second.SharedSecrets[0]);
    }

    [Fact]
    public void Given_Scalars_When_Checked_Then_OnlyOneToOrderMinusOneAreValid()
    {
        // Arrange
        var order = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141");
        var orderMinusOne = Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364140");
        var one = new byte[32];
        one[^1] = 1;

        // Act / Assert
        Assert.False(PaymentOnionFactory.IsValidScalar(new byte[32]));
        Assert.True(PaymentOnionFactory.IsValidScalar(one));
        Assert.True(PaymentOnionFactory.IsValidScalar(orderMinusOne));
        Assert.False(PaymentOnionFactory.IsValidScalar(order));
        Assert.False(PaymentOnionFactory.IsValidScalar(Enumerable.Repeat((byte)0xFF, 32).ToArray()));
        Assert.True(PaymentOnionFactory.IsValidScalar(PaymentOnionFactory.CreateSessionKey()));
    }
}
namespace NLightning.Application.Tests.Payments.Trampoline;

using Application.Payments.Routing;
using Application.Payments.Trampoline;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Models;

/// <summary>
/// NL-875 TR4-T1: the outer final-hop extension of a route to a trampoline node and the payer's trampoline onion,
/// checked by peeling with the real Sphinx and the trampoline's and payee's keys.
/// </summary>
public class TrampolineOnionFactoryTests : IDisposable
{
    private static readonly Hash s_paymentHash = Enumerable.Repeat((byte)0x33, 32).ToArray();
    private static readonly Secret s_outerSecret = Enumerable.Repeat((byte)0x2B, 32).ToArray();
    private static readonly Secret s_invoiceSecret = Enumerable.Repeat((byte)0x2A, 32).ToArray();
    private static readonly ShortChannelId s_scid = new(200, 3, 1);

    private readonly PaymentsTestNode _bob = new("bob", 0x22);
    private readonly PaymentsTestNode _carol = new("carol", 0x23);
    private readonly PaymentsTestNode _eve = new("eve", 0x25);

    public void Dispose()
    {
        _bob.Dispose();
        _carol.Dispose();
        _eve.Dispose();
    }

    private TrampolineOnionFactory Factory() => new(_bob.TrampolineOnion, _bob.HopPayloadSerializer);

    private PaymentRoute RouteToCarol() =>
        new([
                new RouteHop(_bob.NodeId, LightningMoney.MilliSatoshis(100_005_000), 800_250, s_scid),
                new RouteHop(_carol.NodeId, LightningMoney.MilliSatoshis(100_005_000), 800_250, null)
            ], LightningMoney.MilliSatoshis(100_006_000), 800_290, s_paymentHash, s_outerSecret);

    [Fact]
    public void Given_ARouteToATrampolineNode_When_TheFinalPayloadIsCreated_Then_ItCarriesPaymentDataAndTlv20()
    {
        // Arrange
        var route = RouteToCarol();
        var packet = Enumerable.Range(0, OnionConstants.PacketOverheadLength + 100).Select(i => (byte)i).ToArray();
        var pathKey = _eve.NodeId;

        // Act
        var payload = PaymentOnionFactory.CreatePayload(route.Hops[1], route, null,
                                                        new TrampolineFinalHop(packet, pathKey));
        var intermediate = PaymentOnionFactory.CreatePayload(route.Hops[0], route, null,
                                                             new TrampolineFinalHop(packet));

        // Assert
        Assert.Equal(100_005_000UL, payload.AmtToForward!.MilliSatoshi);
        Assert.Equal(800_250U, payload.OutgoingCltvValue);
        Assert.Equal(s_outerSecret, payload.PaymentData!.PaymentSecret);
        Assert.Equal(100_005_000UL, payload.PaymentData.TotalMsat.MilliSatoshi);
        Assert.Equal(packet, payload.TrampolineOnionPacket!.Value.ToBytes());
        Assert.Equal(pathKey, payload.CurrentPathKey);
        Assert.Null(intermediate.TrampolineOnionPacket);
        Assert.Equal(s_scid, intermediate.ShortChannelId);
    }

    [Fact]
    public async Task Given_ABolt11TrampolineRoute_When_BothOnionsArePeeled_Then_EachLayerCarriesTheSpecPayloads()
    {
        // Arrange: inner [Carol (trampoline), Eve (payee)], outer Bob -> Carol
        var factory = Factory();
        var amount = LightningMoney.MilliSatoshis(100_000_000);
        var hops = new[]
        {
            new TrampolineHop(_carol.NodeId,
                              TrampolineOnionFactory.CreateIntermediatePayload(amount, 800_000, _eve.NodeId),
                              amount, 800_000),
            new TrampolineHop(_eve.NodeId,
                              TrampolineOnionFactory.CreateFinalPayload(amount, 800_000, s_invoiceSecret, amount),
                              amount, 800_000)
        };
        var route = RouteToCarol();
        var max = factory.GetMaxHopPayloadsLength(await _bob.OnionFactory.GetIntermediateFramedLengthAsync(route),
                                                  await _bob.OnionFactory.GetTrampolineFinalOtherTlvsLengthAsync(
                                                      route, null));

        // Act
        var trampoline = await factory.CreateAsync(hops, s_paymentHash, max);
        var outer = await _bob.OnionFactory.CreateAsync(route, null, new TrampolineFinalHop(trampoline.ToTlvValue()));

        // Assert: Bob forwards; Carol's outer payload holds the trampoline onion (padded to 650 bytes)
        var atBob = _bob.Sphinx.Peel(outer.Packet, s_paymentHash, _bob.KeyManager.GetNodeKeyPair().PrivKey);
        var atCarol = _carol.Sphinx.Peel(atBob.NextPacket!.Value, s_paymentHash,
                                         _carol.KeyManager.GetNodeKeyPair().PrivKey);
        Assert.True(atCarol.IsFinal);
        var carolOuter = await _carol.HopPayloadSerializer.DeserializeAsync(atCarol.Payload);
        Assert.Equal(s_outerSecret, carolOuter.PaymentData!.PaymentSecret);
        var innerPacket = carolOuter.TrampolineOnionPacket!.Value;
        Assert.Equal(TrampolineOnionConstants.RecommendedHopPayloadsLength, innerPacket.HopPayloadsLength);

        var carolInner = _carol.TrampolineOnion.PeelWithNodeKey(innerPacket.ToBytes(), s_paymentHash,
                                                                _carol.KeyManager.GetNodeKeyPair().PrivKey);
        var carolPayload = await _carol.HopPayloadSerializer.DeserializeAsync(carolInner.Payload);
        Assert.Equal(_eve.NodeId, carolPayload.OutgoingNodeId);
        Assert.Equal(100_000_000UL, carolPayload.AmtToForward!.MilliSatoshi);
        Assert.Equal(800_000U, carolPayload.OutgoingCltvValue);
        Assert.Null(carolPayload.PaymentData);
        Assert.Equal(trampoline.SharedSecrets[0], carolInner.SharedSecret);

        var eveInner = _eve.TrampolineOnion.PeelWithNodeKey(carolInner.NextPacket!.Value.ToBytes(), s_paymentHash,
                                                            _eve.KeyManager.GetNodeKeyPair().PrivKey);
        Assert.True(eveInner.IsFinal);
        var evePayload = await _eve.HopPayloadSerializer.DeserializeAsync(eveInner.Payload);
        Assert.Equal(s_invoiceSecret, evePayload.PaymentData!.PaymentSecret);
        Assert.Equal(100_000_000UL, evePayload.PaymentData.TotalMsat.MilliSatoshi);
        Assert.Null(evePayload.OutgoingNodeId);
        Assert.Equal(trampoline.SharedSecrets[1], eveInner.SharedSecret);
    }

    [Fact]
    public async Task Given_ABolt12RecipientWithoutTrampoline_When_TheOnionIsBuilt_Then_TheTrampolineGetsTlv22And21()
    {
        // Arrange
        var factory = Factory();
        var amount = LightningMoney.MilliSatoshis(150_000_000);
        var path = WireBlindedPaymentPath.FromBlindedPaymentPath(
            new BlindedPaymentPath(new BlindedPath(_eve.NodeId, _bob.NodeId,
                                                   [new BlindedPathHop(_eve.NodeId, new byte[] { 1, 2, 3 })]),
                                   new BlindedPayInfo(500, 1_000, 36, 1, 500_000_000)));
        var features = new FeatureSet();
        features.SetFeature(Feature.BasicMpp, false);
        var hop = new TrampolineHop(_carol.NodeId,
                                    TrampolineOnionFactory.CreateRecipientBlindedPathsPayload(
                                        amount, 800_000, [path], features), amount, 800_000);

        // Act
        var trampoline = await factory.CreateAsync([hop], s_paymentHash, 1_000);

        // Assert
        var peeled = _carol.TrampolineOnion.PeelWithNodeKey(trampoline.ToTlvValue(), s_paymentHash,
                                                            _carol.KeyManager.GetNodeKeyPair().PrivKey);
        var payload = await _carol.HopPayloadSerializer.DeserializeAsync(peeled.Payload);
        Assert.Null(payload.OutgoingNodeId);
        Assert.Single(payload.RecipientBlindedPaths!);
        Assert.True(payload.RecipientFeatures!.GetFeatureSet().IsFeatureSet(Feature.BasicMpp, false));
        Assert.Equal(150_000_000UL, payload.AmtToForward!.MilliSatoshi);
    }

    [Fact]
    public async Task Given_ABlindedPathAsTrampolineHops_When_TheOnionIsPeeled_Then_OnlyTheFinalHopCarriesAmounts()
    {
        // Arrange: Carol (trampoline) -> blinded Bob (introduction) -> blinded Eve (final)
        var factory = Factory();
        var amount = LightningMoney.MilliSatoshis(10_000);
        var path = new BlindedPath(_bob.NodeId, _eve.NodeId,
                                   [
                                       new BlindedPathHop(_bob.NodeId, new byte[] { 9, 9 }),
                                       new BlindedPathHop(_eve.NodeId, new byte[] { 7, 7, 7 })
                                   ]);
        var hops = new List<TrampolineHop>
        {
            new(_carol.NodeId, TrampolineOnionFactory.CreateIntermediatePayload(amount, 900, _bob.NodeId), amount,
                900)
        };
        hops.AddRange(TrampolineOnionFactory.CreateBlindedHops(path, amount, 800, amount));

        // Act
        var trampoline = await factory.CreateAsync(hops, s_paymentHash, 1_000);

        // Assert
        var atCarol = _carol.TrampolineOnion.PeelWithNodeKey(trampoline.ToTlvValue(), s_paymentHash,
                                                             _carol.KeyManager.GetNodeKeyPair().PrivKey);
        Assert.Equal(_bob.NodeId,
                     (await _carol.HopPayloadSerializer.DeserializeAsync(atCarol.Payload)).OutgoingNodeId);
        var atBob = _bob.TrampolineOnion.PeelWithNodeKey(atCarol.NextPacket!.Value.ToBytes(), s_paymentHash,
                                                         _bob.KeyManager.GetNodeKeyPair().PrivKey);
        var bobPayload = await _bob.HopPayloadSerializer.DeserializeAsync(atBob.Payload);
        Assert.Equal(_eve.NodeId, bobPayload.CurrentPathKey);
        Assert.Equal(new byte[] { 9, 9 }, bobPayload.EncryptedRecipientData!.Value.ToArray());
        Assert.Null(bobPayload.AmtToForward);
        Assert.Equal(3, hops.Count);
        var finalPayload = hops[2].Payload;
        Assert.Equal(10_000UL, finalPayload.AmtToForward!.MilliSatoshi);
        Assert.Equal(800U, finalPayload.OutgoingCltvValue);
        Assert.Equal(10_000UL, finalPayload.TotalAmountMsat!.MilliSatoshi);
        Assert.Null(finalPayload.CurrentPathKey);
    }

    [Fact]
    public async Task Given_TwoOnions_When_Built_Then_TheyUseDifferentSessionKeys()
    {
        // Arrange
        var factory = Factory();
        var amount = LightningMoney.MilliSatoshis(10_000);
        TrampolineHop[] hops =
        [
            new(_carol.NodeId, TrampolineOnionFactory.CreateFinalPayload(amount, 800, s_invoiceSecret, amount),
                amount, 800)
        ];

        // Act
        var first = await factory.CreateAsync(hops, s_paymentHash, 1_000);
        var second = await factory.CreateAsync(hops, s_paymentHash, 1_000);
        var outer = await _bob.OnionFactory.CreateAsync(RouteToCarol(), null,
                                                        new TrampolineFinalHop(first.ToTlvValue()));

        // Assert: the ephemeral keys (bytes 1..33) differ, also from the outer onion's
        Assert.NotEqual(first.Packet.PublicKey.ToArray(), second.Packet.PublicKey.ToArray());
        Assert.NotEqual(first.Packet.PublicKey.ToArray(), outer.Packet.PublicKey.ToArray());
    }
}
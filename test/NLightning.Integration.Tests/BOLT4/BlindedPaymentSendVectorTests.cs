using System.Text.Json;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT4;

using Application.Payments.Routing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Protocol.Factories;
using Infrastructure.Serialization.Factories;
using Infrastructure.Serialization.Onion;
using Infrastructure.Serialization.Tlv;

/// <summary>
/// BOLT 4 route blinding, sender side (ONION M5 step 2): blinded-payment-onion-test.json's <c>generate</c> section
/// rebuilt by our send path (<see cref="BlindedPayInfo"/>, <see cref="BlindedRouteComposer"/>,
/// <see cref="PaymentOnionFactory"/>): the aggregated pay info, every hop payload and the onion byte for byte.
/// </summary>
public class BlindedPaymentSendVectorTests
{
    private readonly PaymentOnionFactory _onionFactory;
    private readonly HopPayloadSerializer _hopPayloadSerializer;

    public BlindedPaymentSendVectorTests()
    {
        var valueObjectSerializerFactory = new ValueObjectSerializerFactory();
        var tlvConverterFactory = new TlvConverterFactory();
        var tlvSerializer = new TlvSerializer(valueObjectSerializerFactory);
        var tlvStreamSerializer = new TlvStreamSerializer(tlvConverterFactory, tlvSerializer);
        _hopPayloadSerializer = new HopPayloadSerializer(tlvSerializer, tlvStreamSerializer, tlvConverterFactory,
                                                         valueObjectSerializerFactory);
        _onionFactory = new PaymentOnionFactory(new SphinxService(new Secp256K1Math()), _hopPayloadSerializer);
    }

    [Fact]
    public void Given_BlindedPaymentVectorRelays_When_Aggregating_Then_PayInfoMatches()
    {
        // Arrange: the payment_relay of Bob, Carol and Dave (Eve is the recipient)
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.BlindedPaymentOnionTestPath);
        var generate = Bolt4Vectors.GetRequired(document.RootElement, "generate");
        var hops = generate.GetProperty("full_route").GetProperty("hops").EnumerateArray().ToList();
        var relays = hops.Skip(1).Take(3).Select(h => ReadRelay(h.GetProperty("tlvs")
                                                                 .GetProperty("encrypted_recipient_data")))
                         .ToList();
        var expected = generate.GetProperty("blinded_payinfo");

        // Act
        var (feeBase, feeProportional, cltvDelta) = BlindedPayInfo.Aggregate(relays, 0);

        // Assert
        Assert.Equal(expected.GetProperty("fee_base_msat").GetUInt32(), feeBase);
        Assert.Equal(expected.GetProperty("fee_proportional_millionths").GetUInt32(), feeProportional);
        Assert.Equal(expected.GetProperty("cltv_expiry_delta").GetUInt16(), cltvDelta);
    }

    [Fact]
    public async Task Given_BlindedPaymentVector_When_BuildingTheSendersOnion_Then_PayloadsAndOnionMatch()
    {
        // Arrange: the sender's route to Bob (the introduction node) through Alice, then the blinded route
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.BlindedPaymentOnionTestPath);
        var generate = Bolt4Vectors.GetRequired(document.RootElement, "generate");
        var sessionKey = Bolt4Vectors.GetHex(generate, "session_key");
        var paymentHash = new Hash(Bolt4Vectors.GetHex(generate, "associated_data"));
        var finalAmount = LightningMoney.MilliSatoshis(generate.GetProperty("final_amount_msat").GetUInt64());
        var finalCltv = generate.GetProperty("final_cltv").GetUInt32();
        var payInfoJson = generate.GetProperty("blinded_payinfo");
        var payInfo = new BlindedPayInfo(payInfoJson.GetProperty("fee_base_msat").GetUInt32(),
                                         payInfoJson.GetProperty("fee_proportional_millionths").GetUInt32(),
                                         payInfoJson.GetProperty("cltv_expiry_delta").GetUInt16(), 0, 0);
        var path = ReadBlindedPath(generate.GetProperty("blinded_route"), payInfo);
        var expectedHops = generate.GetProperty("full_route").GetProperty("hops").EnumerateArray().ToList();
        var alice = new CompactPubKey(Bolt4Vectors.GetHex(expectedHops[0], "pubkey"));
        var aliceTlvs = expectedHops[0].GetProperty("tlvs");
        var introAmount = BlindedRouteComposer.GetIntroductionAmount(payInfo, finalAmount);
        var introCltv = finalCltv + payInfo.CltvExpiryDelta;
        var toIntroduction = new PaymentRoute(
            [
                new RouteHop(alice, introAmount, introCltv,
                             ShortChannelId.Parse(aliceTlvs.GetProperty("outgoing_channel_id").GetString()!)),
                new RouteHop(path.Path.FirstNodeId, introAmount, introCltv, null)
            ], introAmount, introCltv, paymentHash, new Secret(new byte[32]));
        var totalAmount = LightningMoney.MilliSatoshis(expectedHops[^1].GetProperty("tlvs")
                                                                       .GetProperty("total_amount_msat").GetUInt64());

        // Act
        var route = BlindedRouteComposer.Compose(toIntroduction, path, finalAmount, totalAmount, 0);
        var onion = await _onionFactory.CreateAsync(route, new PrivKey(sessionKey));

        // Assert: the fee and the CLTV the vector's Alice forwards, each payload, then the onion
        Assert.Equal(aliceTlvs.GetProperty("amt_to_forward").GetUInt64(), introAmount.MilliSatoshi);
        Assert.Equal(aliceTlvs.GetProperty("outgoing_cltv_value").GetUInt32(), introCltv);
        Assert.Equal(1, route.BlindedStartIndex);
        Assert.Equal(expectedHops.Count, route.Hops.Count);
        for (var i = 0; i < route.Hops.Count; i++)
        {
            Assert.Equal(Bolt4Vectors.GetHex(expectedHops[i], "pubkey"), (byte[])route.Hops[i].NodeId);
            using var stream = new MemoryStream();
            await _hopPayloadSerializer.SerializeAsync(PaymentOnionFactory.CreatePayload(route.Hops[i], route), stream);
            // The vector's payload starts with its BigSize length (one byte below 0xfd); ours is written without it
            var payload = stream.ToArray();
            Assert.True(payload.Length < 0xfd);
            Assert.Equal(Convert.ToHexStringLower(Bolt4Vectors.GetHex(expectedHops[i], "payload")),
                         Convert.ToHexStringLower([(byte)payload.Length, .. payload]));
        }

        Assert.Equal(Convert.ToHexStringLower(Bolt4Vectors.GetHex(generate, "onion")),
                     Convert.ToHexStringLower(onion.Packet.ToBytes()));
        Assert.Equal(finalAmount, route.Amount);
        Assert.Equal(introAmount - finalAmount, route.Fee);
    }

    private static BlindedPaymentPath ReadBlindedPath(JsonElement blindedRoute, BlindedPayInfo payInfo)
    {
        var hops = blindedRoute.GetProperty("hops").EnumerateArray()
                               .Select(h => new BlindedPathHop(new CompactPubKey(Bolt4Vectors.GetHex(h, "blinded_node_id")),
                                                               Bolt4Vectors.GetHex(h, "encrypted_data")))
                               .ToList();
        var path = new BlindedPath(new CompactPubKey(Bolt4Vectors.GetHex(blindedRoute, "first_node_id")),
                                   new CompactPubKey(Bolt4Vectors.GetHex(blindedRoute, "first_path_key")), hops);
        return new BlindedPaymentPath(path, payInfo);
    }

    private static BlindedPaymentRelay ReadRelay(JsonElement recipientData)
    {
        var relay = recipientData.GetProperty("payment_relay");
        return new BlindedPaymentRelay(relay.GetProperty("cltv_expiry_delta").GetUInt16(),
                                       relay.GetProperty("fee_proportional_millionths").GetUInt32(),
                                       relay.TryGetProperty("fee_base_msat", out var feeBase)
                                           ? feeBase.GetUInt32()
                                           : 0);
    }
}
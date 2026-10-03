using System.Text.Json;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT4;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Validators;
using Domain.Protocol.Onion.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Onion.RouteBlinding;
using Infrastructure.Protocol.Factories;
using Infrastructure.Serialization.Factories;
using Infrastructure.Serialization.Onion;
using Infrastructure.Serialization.Tlv;

/// <summary>
/// BOLT 4 route blinding (ONION M5) against the official vectors: route-blinding-test.json (path creation, the
/// <c>encrypted_data_tlv</c> encoding and every hop's unblinding) and blinded-payment-onion-test.json (every blinded
/// hop decrypts its <c>encrypted_recipient_data</c> from the onion it peels, and the <c>payment_relay</c> chain yields
/// the final hop's amount and expiry).
/// </summary>
public class RouteBlindingVectorTests
{
    private readonly RouteBlindingService _service = new(new Secp256K1Math());
    private readonly SphinxService _sphinxService = new(new Secp256K1Math());
    private readonly HopPayloadSerializer _hopPayloadSerializer;

    public RouteBlindingVectorTests()
    {
        var valueObjectSerializerFactory = new ValueObjectSerializerFactory();
        var tlvConverterFactory = new TlvConverterFactory();
        var tlvSerializer = new TlvSerializer(valueObjectSerializerFactory);
        var tlvStreamSerializer = new TlvStreamSerializer(tlvConverterFactory, tlvSerializer);
        _hopPayloadSerializer = new HopPayloadSerializer(tlvSerializer, tlvStreamSerializer, tlvConverterFactory,
                                                         valueObjectSerializerFactory);
    }

    [Fact]
    public void Given_RouteBlindingVector_When_CreatingBothPathSegments_Then_EveryHopValueMatches()
    {
        // Arrange: Bob creates Bob -> Carol (session 0x02..), Eve creates Dave -> Eve (session 0x01..); Carol's data
        // carries the next_path_key_override that joins them
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.RouteBlindingTestPath);
        var hops = GetGenerateHops(document.RootElement);
        var segments = new[] { hops[..2], hops[2..] };

        foreach (var segment in segments)
        {
            var sessionKey = Bolt4Vectors.GetHex(segment[0], "session_key");
            var nodeIds = segment.Select(h => new CompactPubKey(Bolt4Vectors.GetHex(h, "node_id"))).ToList();
            var plaintexts = segment.Select(h => Bolt4Vectors.GetHex(h, "encoded_tlvs")).ToList();

            // Act
            var trace = _service.CreateBlindedPathTrace(nodeIds, plaintexts, sessionKey);
            var path = _service.CreateBlindedPath(nodeIds, plaintexts, sessionKey);

            // Assert
            Assert.Equal(nodeIds[0], path.FirstNodeId);
            Assert.Equal(Hex(segment[0], "path_key"), path.FirstPathKey.ToString());
            for (var i = 0; i < segment.Length; i++)
            {
                Assert.Equal(Hex(segment[i], "path_key"), trace[i].PathKey.ToString());
                Assert.Equal(Hex(segment[i], "shared_secret"), Convert.ToHexStringLower(trace[i].SharedSecret));
                Assert.Equal(Hex(segment[i], "rho"), Convert.ToHexStringLower(trace[i].Rho));
                Assert.Equal(Hex(segment[i], "blinded_node_id"), trace[i].BlindedNodeId.ToString());
                Assert.Equal(Hex(segment[i], "encrypted_data"), Convert.ToHexStringLower(trace[i].EncryptedData));
                Assert.Equal(trace[i].BlindedNodeId, path.Hops[i].BlindedNodeId);
                Assert.Equal(trace[i].EncryptedData, path.Hops[i].EncryptedRecipientData.ToArray());
            }
        }
    }

    [Fact]
    public void Given_RouteBlindingVector_When_EncodingEachHopsTlvs_Then_BytesMatchAndDecodeRoundTrips()
    {
        // Arrange
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.RouteBlindingTestPath);

        foreach (var hop in GetGenerateHops(document.RootElement))
        {
            var expected = Bolt4Vectors.GetHex(hop, "encoded_tlvs");
            var data = FromJson(hop.GetProperty("tlvs"));

            // Act
            var encoded = _service.EncodeRecipientData(data);
            var reEncoded = _service.EncodeRecipientData(_service.DecodeRecipientData(expected));

            // Assert
            Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(encoded));
            Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(reEncoded));
        }
    }

    [Fact]
    public void Given_RouteBlindingVector_When_EachHopUnblinds_Then_DataKeysAndNextPathKeyMatch()
    {
        // Arrange
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.RouteBlindingTestPath);
        var root = document.RootElement;
        var routeHops = Bolt4Vectors.GetRequired(root, "route").GetProperty("hops").EnumerateArray().ToList();
        var unblindHops = Bolt4Vectors.GetRequired(root, "unblind").GetProperty("hops").EnumerateArray().ToList();
        Assert.Equal(4, unblindHops.Count);

        for (var i = 0; i < unblindHops.Count; i++)
        {
            var hop = unblindHops[i];
            PrivKey nodeKey = Bolt4Vectors.GetHex(hop, "node_privkey");
            var pathKey = new CompactPubKey(Bolt4Vectors.GetHex(hop, "path_key"));
            var encrypted = Bolt4Vectors.GetHex(routeHops[i], "encrypted_data");

            // Act
            var unblinded = _service.Unblind(nodeKey, pathKey, encrypted);
            var blindedPrivKey = _service.ComputeBlindedPrivKey(nodeKey, pathKey);
            var sharedSecret = new byte[32];
            using (var ecNodeKey = SphinxKeyGenerator.CreatePrivateKey(nodeKey.Value, "nodeKey"))
                SphinxKeyGenerator.ComputeEcdhSharedSecret(ecNodeKey, pathKey, sharedSecret);
            var derivedNextPathKey = _service.DeriveNextPathKey(pathKey, new Secret(sharedSecret));

            // Assert
            Assert.Equal(Hex(hop, "decrypted_data"), Convert.ToHexStringLower(unblinded.DecryptedData.Span));
            Assert.Equal(Hex(hop, "blinded_privkey"), Convert.ToHexStringLower(blindedPrivKey.Value));
            Assert.Equal(Hex(hop, "next_path_key"), derivedNextPathKey.ToString());
            var expectedNext = Bolt4Vectors.GetOptionalHex(hop, "next_path_key_override")
                            ?? Bolt4Vectors.GetHex(hop, "next_path_key");
            Assert.Equal(Convert.ToHexStringLower(expectedNext), unblinded.NextPathKey.ToString());
        }
    }

    [Fact]
    public void Given_RouteBlindingVectorWithFlippedCiphertextBit_When_Unblinding_Then_ThrowsInvalidOnionBlinding()
    {
        // Arrange
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.RouteBlindingTestPath);
        var root = document.RootElement;
        var bob = Bolt4Vectors.GetRequired(root, "unblind").GetProperty("hops")[0];
        var encrypted = Bolt4Vectors.GetHex(Bolt4Vectors.GetRequired(root, "route").GetProperty("hops")[0],
                                            "encrypted_data");
        encrypted[5] ^= 0x01;

        // Act
        var exception = Assert.Throws<OnionException>(() => _service.Unblind(
                                                          Bolt4Vectors.GetHex(bob, "node_privkey"),
                                                          new CompactPubKey(Bolt4Vectors.GetHex(bob, "path_key")),
                                                          encrypted));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionBlinding, exception.FailureCode);
    }

    [Fact]
    public void Given_RouteBlindingVectorEve_When_Validating_Then_UnknownAllowedFeatureIsRefused()
    {
        // Arrange: Eve's data allows feature bit 113, which no blinded payment defines: a reader MUST fail it
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.RouteBlindingTestPath);
        var eve = GetGenerateHops(document.RootElement)[3];
        var data = _service.DecodeRecipientData(Bolt4Vectors.GetHex(eve, "encoded_tlvs"));

        // Act
        var valid = BlindedRecipientDataValidator.TryValidate(data, true, 1500, 747777, out var reason);

        // Assert
        Assert.False(valid);
        Assert.Contains("feature", reason);
        Assert.Equal(new byte[] { 0xde, 0xad, 0xbe, 0xef }, data.PathId!.Value.ToArray());
    }

    [Fact]
    public async Task Given_BlindedPaymentVector_When_EachBlindedHopPeelsAndUnblinds_Then_RelayChainReachesTheFinalHop()
    {
        // Arrange: Alice (normal hop) forwards to Bob, the introduction node (current_path_key in his payload); Carol,
        // Dave and Eve get the path_key in update_add_htlc
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.BlindedPaymentOnionTestPath);
        var root = document.RootElement;
        var associatedData = Bolt4Vectors.GetHex(Bolt4Vectors.GetRequired(root, "generate"), "associated_data");
        var expectedHops = Bolt4Vectors.GetRequired(root, "generate").GetProperty("full_route").GetProperty("hops")
                                       .EnumerateArray().ToList();
        var decryptHops = Bolt4Vectors.GetRequired(root, "decrypt").GetProperty("hops").EnumerateArray().ToList();

        var alice = _sphinxService.Peel(new OnionPacket(Bolt4Vectors.GetHex(decryptHops[0], "onion")), associatedData,
                                        Bolt4Vectors.GetHex(decryptHops[0], "node_privkey"));
        var alicePayload = await _hopPayloadSerializer.DeserializeAsync(alice.Payload);
        var amount = alicePayload.AmtToForward!.MilliSatoshi;
        var cltv = alicePayload.OutgoingCltvValue!.Value;
        CompactPubKey? updateAddPathKey = null;

        for (var i = 1; i < decryptHops.Count; i++)
        {
            // Act
            var peeled = _sphinxService.Peel(new OnionPacket(Bolt4Vectors.GetHex(decryptHops[i], "onion")),
                                             associatedData, Bolt4Vectors.GetHex(decryptHops[i], "node_privkey"),
                                             updateAddPathKey);
            var payload = await _hopPayloadSerializer.DeserializeAsync(peeled.Payload);
            Assert.True(HopPayloadValidator.TryValidate(payload, peeled.IsFinal, updateAddPathKey.HasValue, out _));
            var pathKey = updateAddPathKey ?? payload.CurrentPathKey!.Value;
            var unblinded = _service.Unblind(Bolt4Vectors.GetHex(decryptHops[i], "node_privkey"), pathKey,
                                             payload.EncryptedRecipientData!.Value);
            // Past the introduction node the peel already computed ECDH(path_key, node_key): no second ECDH
            var withSharedSecret = peeled.PathKeySharedSecret is { } pathKeySharedSecret
                                       ? _service.UnblindAsLocalNode(pathKey, payload.EncryptedRecipientData!.Value,
                                                                     pathKeySharedSecret)
                                       : unblinded;

            // Assert
            var expected = expectedHops[i].GetProperty("tlvs").GetProperty("encrypted_recipient_data");
            var data = unblinded.RecipientData;
            Assert.Equal(withSharedSecret.DecryptedData.ToArray(), unblinded.DecryptedData.ToArray());
            Assert.Equal(Hex(decryptHops[i], "next_path_key"), unblinded.NextPathKey.ToString());
            Assert.True(BlindedRecipientDataValidator.TryValidate(data, peeled.IsFinal, amount, cltv, out var reason),
                        reason);
            AssertConstraints(expected, data);
            if (!peeled.IsFinal)
            {
                Assert.Equal(ShortChannelId.Parse(expected.GetProperty("short_channel_id").GetString()!),
                             data.ShortChannelId);
                Assert.True(data.PaymentRelay!.TryComputeAmountToForward(amount, out amount));
                Assert.True(data.PaymentRelay.TryComputeOutgoingCltvValue(cltv, out cltv));
            }
            else
            {
                Assert.Equal(Hex(expected, "path_id"), Convert.ToHexStringLower(data.PathId!.Value.Span));
                Assert.Equal(payload.AmtToForward!.MilliSatoshi, amount);
                Assert.Equal(payload.OutgoingCltvValue!.Value, cltv);
            }

            updateAddPathKey = unblinded.NextPathKey;
        }

        Assert.Equal(100_000UL, amount);
        Assert.Equal(749_000U, cltv);
    }

    private static void AssertConstraints(JsonElement expected, BlindedRecipientData data)
    {
        var constraints = expected.GetProperty("payment_constraints");
        Assert.Equal(constraints.GetProperty("max_cltv_expiry").GetUInt32(), data.PaymentConstraints!.MaxCltvExpiry);
        Assert.Equal(constraints.GetProperty("htlc_minimum_msat").GetUInt64(), data.PaymentConstraints.HtlcMinimumMsat);
        if (!expected.TryGetProperty("payment_relay", out var relay))
            return;

        Assert.Equal(relay.GetProperty("cltv_expiry_delta").GetUInt16(), data.PaymentRelay!.CltvExpiryDelta);
        Assert.Equal(relay.GetProperty("fee_proportional_millionths").GetUInt32(),
                     data.PaymentRelay.FeeProportionalMillionths);
        Assert.Equal(relay.TryGetProperty("fee_base_msat", out var feeBase) ? feeBase.GetUInt32() : 0U,
                     data.PaymentRelay.FeeBaseMsat);
    }

    private static JsonElement[] GetGenerateHops(JsonElement root) =>
        Bolt4Vectors.GetRequired(root, "generate").GetProperty("hops").EnumerateArray().ToArray();

    private static string Hex(JsonElement element, string property) =>
        Convert.ToHexStringLower(Bolt4Vectors.GetHex(element, property));

    /// <summary>
    /// The vector's JSON view of an <c>encrypted_data_tlv</c> (feature bits as a list, unknown records as
    /// <c>unknown_tag_N</c>).
    /// </summary>
    private static BlindedRecipientData FromJson(JsonElement tlvs)
    {
        var unknown = new List<KeyValuePair<ulong, ReadOnlyMemory<byte>>>();
        foreach (var property in tlvs.EnumerateObject().Where(p => p.Name.StartsWith("unknown_tag_")))
            unknown.Add(new KeyValuePair<ulong, ReadOnlyMemory<byte>>(ulong.Parse(property.Name["unknown_tag_".Length..]),
                                                                        Convert.FromHexString(property.Value.GetString()!)));

        BlindedPaymentRelay? relay = null;
        if (tlvs.TryGetProperty("payment_relay", out var r))
            relay = new BlindedPaymentRelay(r.GetProperty("cltv_expiry_delta").GetUInt16(),
                                            r.GetProperty("fee_proportional_millionths").GetUInt32(),
                                            r.TryGetProperty("fee_base_msat", out var b) ? b.GetUInt32() : 0);

        BlindedPaymentConstraints? constraints = null;
        if (tlvs.TryGetProperty("payment_constraints", out var c))
            constraints = new BlindedPaymentConstraints(c.GetProperty("max_cltv_expiry").GetUInt32(),
                                                        c.GetProperty("htlc_minimum_msat").GetUInt64());

        byte[]? allowedFeatures = null;
        if (tlvs.TryGetProperty("allowed_features", out var f))
        {
            var bits = f.GetProperty("features").EnumerateArray().Select(e => e.GetInt32()).ToList();
            allowedFeatures = new byte[bits.Count == 0 ? 0 : bits.Max() / 8 + 1];
            foreach (var bit in bits)
                allowedFeatures[allowedFeatures.Length - 1 - bit / 8] |= (byte)(1 << (bit % 8));
        }

        return new BlindedRecipientData
        {
            Padding = tlvs.TryGetProperty("padding", out var p) ? (ReadOnlyMemory<byte>?)Convert.FromHexString(p.GetString()!) : null,
            ShortChannelId = tlvs.TryGetProperty("short_channel_id", out var s)
                                 ? (ShortChannelId?)ShortChannelId.Parse(s.GetString()!)
                                 : null,
            PathId = tlvs.TryGetProperty("path_id", out var id) ? (ReadOnlyMemory<byte>?)Convert.FromHexString(id.GetString()!) : null,
            NextPathKeyOverride = tlvs.TryGetProperty("next_path_key_override", out var o)
                                      ? (CompactPubKey?)new CompactPubKey(Convert.FromHexString(o.GetString()!))
                                      : null,
            PaymentRelay = relay,
            PaymentConstraints = constraints,
            AllowedFeatures = allowedFeatures is null ? null : (ReadOnlyMemory<byte>?)allowedFeatures,
            UnknownOddRecords = unknown
        };
    }
}
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT4;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Onion.OnionMessages;
using Infrastructure.Bitcoin.Onion.RouteBlinding;

/// <summary>
/// BOLT 4 onion messages (wave M6, OM1) against the official <c>blinded-onion-message-onion-test.json</c>: Dave's
/// message path Bob -> Carol -> Dave (with equal-length padding), the sender's prefix hop to Alice that joins it with
/// <c>next_path_key_override</c>, the resulting route, the onion packet byte for byte, and every hop's decryption.
/// </summary>
/// <remarks>
/// Session keys, confirmed against the vector: the top-level <c>generate.session_key</c> (0x03 x 32) is the
/// <b>Sphinx</b> session key (the packet's public key is its point); the blinded paths use the hops'
/// <c>path_key_secret</c>: 0x01 x 32 for Dave's path (Bob's) and 0x63 x 32 for the sender's prefix (Alice's).
/// </remarks>
public class OnionMessageVectorTests
{
    private const string UnknownTagPrefix = "unknown_tag_";

    private readonly Secp256K1Math _secp256K1Math = new();
    private readonly RouteBlindingService _routeBlindingService;
    private readonly BlindedMessagePathBuilder _pathBuilder;
    private readonly OnionMessagePacketBuilder _packetBuilder;
    private readonly OnionMessageUnwrapper _unwrapper;

    public OnionMessageVectorTests()
    {
        var sphinxService = new SphinxService(_secp256K1Math);
        _routeBlindingService = new RouteBlindingService(_secp256K1Math);
        _pathBuilder = new BlindedMessagePathBuilder(_routeBlindingService);
        _packetBuilder = new OnionMessagePacketBuilder(sphinxService, _pathBuilder);
        _unwrapper = new OnionMessageUnwrapper(sphinxService, _routeBlindingService);
    }

    [Fact]
    public void Given_MessageVector_When_CreatingDavesPathWithPadding_Then_EveryHopValueMatches()
    {
        // Arrange: Dave's path Bob -> Carol -> Dave from the unpadded tlvs, with Bob's path_key_secret
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.BlindedOnionMessageOnionTestPath);
        var vector = new MessageVector(document.RootElement);
        var hops = vector.GenerateHops[1..];
        var nodeIds = vector.NodeIds[1..];
        var data = hops.Select(ParseTlvs).ToList();
        var sessionKey = Bolt4Vectors.GetHex(hops[0], "path_key_secret");

        // Act
        var encoded = _pathBuilder.EncodePaddedHops(data);
        var trace = _routeBlindingService.CreateBlindedPathTrace(nodeIds, encoded.ToList(), sessionKey);
        var path = _pathBuilder.CreatePath(nodeIds, data, sessionKey);

        // Assert
        AssertHopsMatch(hops, encoded, trace, sessionKey);
        Assert.Equal(nodeIds[0], path.FirstNodeId);
        Assert.Equal(Hex(hops[0], "E"), path.FirstPathKey.ToString());
        for (var i = 0; i < hops.Length; i++)
        {
            Assert.Equal(Hex(hops[i], "blinded_node_id"), path.Hops[i].BlindedNodeId.ToString());
            Assert.Equal(Hex(hops[i], "encrypted_recipient_data"),
                         Convert.ToHexStringLower(path.Hops[i].EncryptedRecipientData.Span));
        }
    }

    [Fact]
    public void Given_MessageVector_When_CreatingTheSendersPrefixHop_Then_AlicesValuesMatch()
    {
        // Arrange: the sender's own one-hop path to Alice, whose data names Bob and overrides the path_key with
        // Dave's first_path_key (BOLT 4 writer, OM-S-05)
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.BlindedOnionMessageOnionTestPath);
        var vector = new MessageVector(document.RootElement);
        var alice = vector.GenerateHops[0];
        var sessionKey = Bolt4Vectors.GetHex(alice, "path_key_secret");
        var data = new BlindedRecipientData
        {
            NextNodeId = vector.NodeIds[1],
            NextPathKeyOverride = new CompactPubKey(Bolt4Vectors.GetHex(vector.GenerateHops[1], "E"))
        };

        // Act
        var encoded = _pathBuilder.EncodePaddedHops([data]);
        var trace = _routeBlindingService.CreateBlindedPathTrace([vector.NodeIds[0]], encoded.ToList(), sessionKey);

        // Assert
        AssertHopsMatch([alice], encoded, trace, sessionKey);
        Assert.Equal(Hex(alice, "encrypted_data_tlv"), Convert.ToHexStringLower(_routeBlindingService
                                                                  .EncodeRecipientData(ParseTlvs(alice))));
    }

    [Fact]
    public void Given_MessageVector_When_BuildingThePacket_Then_RoutePacketAndFirstMessageMatch()
    {
        // Arrange
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.BlindedOnionMessageOnionTestPath);
        var vector = new MessageVector(document.RootElement);
        var davesPath = CreateDavesPath(vector);
        var route = Bolt4Vectors.GetRequired(vector.Root, "route");
        var onionMessage = Bolt4Vectors.GetRequired(vector.Root, "onionmessage");
        var contents = OnionMessageContents.Single(1, Bolt4Vectors.GetHex(onionMessage, "unknown_tag_1"));
        var sessionKey = Bolt4Vectors.GetHex(vector.Generate, "session_key");
        var prefixSessionKey = Bolt4Vectors.GetHex(vector.GenerateHops[0], "path_key_secret");

        // Act
        var message = _packetBuilder.Build([vector.NodeIds[0]], davesPath, contents, null, sessionKey,
                                           prefixSessionKey);

        // Assert: the route (Alice's prefix + Dave's path) as the vector lists it
        Assert.Equal(Hex(route, "first_node_id"), vector.NodeIds[0].ToString());
        Assert.Equal(Hex(route, "first_path_key"), message.Payload.PathKey.ToString());
        var routeHops = Bolt4Vectors.GetRequired(route, "hops").EnumerateArray().ToList();
        Assert.Equal(4, routeHops.Count);
        for (var i = 1; i < routeHops.Count; i++)
        {
            Assert.Equal(Hex(routeHops[i], "blinded_node_id"), davesPath.Hops[i - 1].BlindedNodeId.ToString());
            Assert.Equal(Hex(routeHops[i], "encrypted_recipient_data"),
                         Convert.ToHexStringLower(davesPath.Hops[i - 1].EncryptedRecipientData.Span));
        }

        // Assert: the Sphinx session key is generate.session_key (the packet's public key is its point)
        var packet = message.Payload.OnionMessagePacket.ToArray();
        Assert.Equal(1366, packet.Length);
        Assert.Equal(Convert.ToHexStringLower(new Ecdh().GenerateKeyPair(sessionKey).CompactPubKey),
                     Convert.ToHexStringLower(packet.AsSpan(1, 33)));
        Assert.Equal(Hex(onionMessage, "onion_message_packet"), Convert.ToHexStringLower(packet));
        Assert.Equal(Hex(vector.DecryptHops[0], "onion_message"), Convert.ToHexStringLower(Serialize(message)));
    }

    [Fact]
    public void Given_MessageVector_When_EachHopUnwraps_Then_NextMessagesNextNodesAndDaveMatch()
    {
        // Arrange
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.BlindedOnionMessageOnionTestPath);
        var vector = new MessageVector(document.RootElement);
        var hops = vector.DecryptHops;

        for (var i = 0; i < hops.Length; i++)
        {
            var message = Parse(Bolt4Vectors.GetHex(hops[i], "onion_message"));

            // Act
            var result = _unwrapper.Unwrap(message, Bolt4Vectors.GetHex(hops[i], "privkey"));

            // Assert
            if (i < hops.Length - 1)
            {
                Assert.Equal(OnionMessageUnwrapStatus.Forward, result.Status);
                Assert.Equal(Hex(hops[i], "next_node_id"), result.NextNodeId?.ToString());
                Assert.Null(result.NextShortChannelId);
                Assert.Equal(Hex(hops[i + 1], "onion_message"), Convert.ToHexStringLower(Serialize(result.NextMessage!)));
                continue;
            }

            // Dave: unknown_tag_1 = "hello" in the onionmsg_tlv, path_id and unknown_tag_65535 in his data
            var tlvs = Bolt4Vectors.GetRequired(hops[i], "tlvs");
            var davesData = ParseTlvs(vector.GenerateHops[^1]);
            Assert.Equal(OnionMessageUnwrapStatus.Deliver, result.Status);
            Assert.NotNull(result.Payload);
            Assert.Null(result.Payload.ReplyPath);
            Assert.Equal(Hex(tlvs, "encrypted_recipient_data"),
                         Convert.ToHexStringLower(result.Payload.EncryptedRecipientData!.Value.Span));
            var record = Assert.Single(result.Payload.OtherRecords);
            Assert.Equal(1UL, record.Type);
            Assert.Equal(Hex(tlvs, "unknown_tag_1"), Convert.ToHexStringLower(record.Value.Span));
            Assert.Equal(davesData.PathId!.Value.ToArray(), result.PathId!.Value.ToArray());
            var unknown = Assert.Single(result.RecipientData!.UnknownOddRecords);
            Assert.Equal(65535UL, unknown.Key);
            Assert.Equal("06c1", Convert.ToHexStringLower(unknown.Value.Span));
        }
    }

    [Fact]
    public void Given_MessageVector_When_AnyBitOfTheFirstMessageFlips_Then_AliceIgnoresItWithoutThrowing()
    {
        // Arrange: every byte of the packet header, a sample of the payloads, the HMAC, and the path_key
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.BlindedOnionMessageOnionTestPath);
        var vector = new MessageVector(document.RootElement);
        var original = Bolt4Vectors.GetHex(vector.DecryptHops[0], "onion_message");
        var aliceKey = Bolt4Vectors.GetHex(vector.DecryptHops[0], "privkey");
        var positions = Enumerable.Range(2, 33 + 2 + 1 + 33)
                                  .Concat(Enumerable.Range(0, 40).Select(i => 71 + (i * 32)))
                                  .Concat(Enumerable.Range(original.Length - 32, 32))
                                  .Where(p => p is not (35 or 36)) // the u16 len only frames the packet
                                  .Distinct()
                                  .ToList();

        foreach (var position in positions)
        {
            var tampered = (byte[])original.Clone();
            tampered[position] ^= 0x01;
            OnionMessageMessage message;
            try
            {
                message = Parse(tampered);
            }
            catch (ArgumentException)
            {
                continue; // not a valid wire message at all (the serializer's job, OM0-T1)
            }

            // Act
            var result = _unwrapper.Unwrap(message, aliceKey);

            // Assert
            Assert.Equal(OnionMessageUnwrapStatus.Ignored, result.Status);
            Assert.NotNull(result.IgnoreReason);
        }
    }

    [Fact]
    public void Given_A32KibPayload_When_BuildingAndUnwrappingOverFourHops_Then_DaveGetsItInALargePacket()
    {
        // Arrange: the vector's nodes, fresh session keys, a 30000-byte odd record (type 65) for Dave
        using var document = Bolt4Vectors.LoadDocument(Bolt4Vectors.BlindedOnionMessageOnionTestPath);
        var vector = new MessageVector(document.RootElement);
        var nodeKeys = vector.DecryptHops.Select(h => new PrivKey(Bolt4Vectors.GetHex(h, "privkey"))).ToArray();
        var pathId = RandomNumberGenerator.GetBytes(32);
        var davesPath = _pathBuilder.CreateMessagePath(vector.NodeIds[1..], pathId);
        var large = RandomNumberGenerator.GetBytes(30_000);
        var contents = OnionMessageContents.Single(65, large);

        // Act
        var message = _packetBuilder.Build([vector.NodeIds[0]], davesPath, contents, null);
        var results = new List<OnionMessageUnwrapResult>();
        for (var i = 0; i < nodeKeys.Length; i++)
        {
            results.Add(_unwrapper.Unwrap(message, nodeKeys[i]));
            if (results[^1].NextMessage is { } next)
                message = next;
        }

        // Assert
        Assert.All(results[..3], r => Assert.Equal(OnionMessageUnwrapStatus.Forward, r.Status));
        Assert.Equal(vector.NodeIds[1..], results[..3].Select(r => r.NextNodeId!.Value));
        Assert.Equal(OnionMessageConstants.LargePayloadsLength + OnionConstants.PacketOverheadLength,
                     message.Payload.OnionMessagePacket.Length);
        var delivered = results[3];
        Assert.Equal(OnionMessageUnwrapStatus.Deliver, delivered.Status);
        Assert.Equal(pathId, delivered.PathId!.Value.ToArray());
        var record = Assert.Single(delivered.Payload!.OtherRecords);
        Assert.Equal(65UL, record.Type);
        Assert.Equal(large, record.Value.ToArray());
    }

    private BlindedPath CreateDavesPath(MessageVector vector)
    {
        var hops = vector.GenerateHops[1..];
        return _pathBuilder.CreatePath(vector.NodeIds[1..], hops.Select(ParseTlvs).ToList(),
                                       Bolt4Vectors.GetHex(hops[0], "path_key_secret"));
    }

    private void AssertHopsMatch(JsonElement[] hops, IReadOnlyList<byte[]> encoded,
                                 IReadOnlyList<BlindedPathHopTrace> trace, byte[] sessionKey)
    {
        using var keyGenerator = new SphinxKeyGenerator();
        var e = new PrivKey(sessionKey);
        for (var i = 0; i < hops.Length; i++)
        {
            var hop = hops[i];
            Assert.Equal(Hex(hop, "path_key_secret"), Convert.ToHexStringLower(e.Value));
            Assert.Equal(Hex(hop, "encrypted_data_tlv"), Convert.ToHexStringLower(encoded[i]));
            Assert.Equal(Hex(hop, "E"), trace[i].PathKey.ToString());
            Assert.Equal(Hex(hop, "ss"), Convert.ToHexStringLower(trace[i].SharedSecret));
            Assert.Equal(Hex(hop, "HMAC256('blinded_node_id', ss)"),
                         Convert.ToHexStringLower(keyGenerator.DeriveKey(OnionConstants.BlindedNodeId,
                                                                         trace[i].SharedSecret)));
            Assert.Equal(Hex(hop, "blinded_node_id"), trace[i].BlindedNodeId.ToString());
            Assert.Equal(Hex(hop, "rho"), Convert.ToHexStringLower(trace[i].Rho));
            Assert.Equal(Hex(hop, "encrypted_recipient_data"), Convert.ToHexStringLower(trace[i].EncryptedData));

            // H(E || ss) and next_e = e * H(E || ss); the vector prints next_e with a trailing 0x01 (a compressed
            // private key's flag byte), which is not part of the scalar
            var blindingFactor = SHA256.HashData([.. (byte[])trace[i].PathKey, .. (byte[])trace[i].SharedSecret]);
            Assert.Equal(Hex(hop, "H(E || ss)"), Convert.ToHexStringLower(blindingFactor));
            e = _secp256K1Math.MultiplyPrivKey(e, blindingFactor);
            Assert.Equal(Hex(hop, "next_e"), Convert.ToHexStringLower(e.Value) + "01");
        }
    }

    /// <summary>
    /// The vector's <c>tlvs</c> object as the unpadded data of one hop (the builder adds the padding).
    /// </summary>
    private static BlindedRecipientData ParseTlvs(JsonElement hop)
    {
        var tlvs = Bolt4Vectors.GetRequired(hop, "tlvs");
        var unknown = new List<KeyValuePair<ulong, ReadOnlyMemory<byte>>>();
        CompactPubKey? nextNodeId = null;
        CompactPubKey? nextPathKeyOverride = null;
        ReadOnlyMemory<byte>? pathId = null;
        foreach (var property in tlvs.EnumerateObject())
        {
            switch (property.Name)
            {
                case "next_node_id":
                    nextNodeId = new CompactPubKey(Convert.FromHexString(property.Value.GetString()!));
                    break;
                case "next_path_key_override":
                    nextPathKeyOverride = new CompactPubKey(Convert.FromHexString(property.Value.GetString()!));
                    break;
                case "path_id":
                    pathId = Convert.FromHexString(property.Value.GetString()!);
                    break;
                case "padding" or "path_key_override_secret":
                    break;
                default:
                    Assert.StartsWith(UnknownTagPrefix, property.Name);
                    unknown.Add(new KeyValuePair<ulong, ReadOnlyMemory<byte>>(
                                    ulong.Parse(property.Name[UnknownTagPrefix.Length..]),
                                    Convert.FromHexString(property.Value.GetString()!)));
                    break;
            }
        }

        return new BlindedRecipientData
        {
            NextNodeId = nextNodeId,
            NextPathKeyOverride = nextPathKeyOverride,
            PathId = pathId,
            UnknownOddRecords = unknown
        };
    }

    /// <summary>
    /// type 513 (u16) || point path_key || u16 len || onion_message_packet (the serializer is lane M6-A's).
    /// </summary>
    private static byte[] Serialize(OnionMessageMessage message)
    {
        var packet = message.Payload.OnionMessagePacket.Span;
        var output = new byte[2 + 33 + 2 + packet.Length];
        BinaryPrimitives.WriteUInt16BigEndian(output, 513);
        ((byte[])message.Payload.PathKey).CopyTo(output, 2);
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(35), (ushort)packet.Length);
        packet.CopyTo(output.AsSpan(37));
        return output;
    }

    private static OnionMessageMessage Parse(byte[] wire)
    {
        Assert.Equal(513, BinaryPrimitives.ReadUInt16BigEndian(wire));
        Assert.Equal(wire.Length - 37, BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(35)));
        return new OnionMessageMessage(new OnionMessagePayload(new CompactPubKey(wire[2..35]), wire[37..]));
    }

    private static string Hex(JsonElement element, string propertyName) =>
        Convert.ToHexStringLower(Bolt4Vectors.GetHex(element, propertyName));

    private sealed class MessageVector
    {
        public JsonElement Root { get; }
        public JsonElement Generate { get; }
        public JsonElement[] GenerateHops { get; }
        public JsonElement[] DecryptHops { get; }

        /// <summary>
        /// Alice, Bob, Carol and Dave's real node ids (Alice is route.first_node_id, the others each previous hop's
        /// next_node_id).
        /// </summary>
        public CompactPubKey[] NodeIds { get; }

        public MessageVector(JsonElement root)
        {
            Root = root;
            Generate = Bolt4Vectors.GetRequired(root, "generate");
            GenerateHops = Bolt4Vectors.GetRequired(Generate, "hops").EnumerateArray().ToArray();
            DecryptHops = Bolt4Vectors.GetRequired(Bolt4Vectors.GetRequired(root, "decrypt"), "hops")
                                      .EnumerateArray()
                                      .ToArray();
            var alice = new CompactPubKey(Bolt4Vectors.GetHex(Bolt4Vectors.GetRequired(root, "route"),
                                                              "first_node_id"));
            NodeIds = [alice, .. GenerateHops[..3].Select(h => new CompactPubKey(
                                                                Bolt4Vectors.GetHex(
                                                                    Bolt4Vectors.GetRequired(h, "tlvs"),
                                                                    "next_node_id")))];
        }
    }
}
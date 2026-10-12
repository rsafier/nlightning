using System.Buffers.Binary;
using System.Text.Json;

namespace NLightning.Application.Tests.OnionMessages;

using Application.OnionMessages;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Harness;

/// <summary>
/// The receive side of <see cref="OnionMessageService"/> against the BOLT 4 vector
/// <c>blinded-onion-message-onion-test.json</c>: Alice, Bob and Carol (keys 0x41.., 0x42.., 0x43..) each peel the
/// vector's <c>onion_message</c> with the real Sphinx, read their <c>onionmsg_tlv</c> and recipient data (Alice's
/// carries <c>next_path_key_override</c>, Bob's an unknown odd type 561, Carol's padding) and forward to the next node
/// byte for byte what the vector's next hop receives; Dave (0x44..) delivers the final hop (only the odd type 1,
/// "hello": no payload field, so nothing for a handler).
/// </summary>
/// <remarks>
/// This proves the service's own payload decoding and forwarding against the spec, independently of the harness packet
/// builder (which shares the service's codec).
/// </remarks>
public sealed class OnionMessageVectorTests
{
    private const int MessageTypeLength = 2;
    private const int PathKeyLength = 33;
    private const int LengthPrefixLength = 2;

    [Fact]
    public async Task Given_TheBolt4OnionMessageVector_When_AliceBobCarolAndDaveReadIt_Then_EachForwardIsByteExact()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var vector = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "OnionMessages", "Vectors",
                                                     "blinded-onion-message-onion-test.json"), ct));
        var hops = vector.RootElement.GetProperty("decrypt").GetProperty("hops").EnumerateArray().ToList();
        var wireMessages = hops.Select(h => Convert.FromHexString(h.GetProperty("onion_message").GetString()!))
                               .ToList();
        // Alice, Bob and Carol name the next node; Dave is the final hop
        var nextNodeIds = hops.Take(3).Select(h => h.GetProperty("next_node_id").GetString()!).ToList();

        using var sender = new OnionMessageTestNode("sender", 0x01);
        using var alice = new OnionMessageTestNode("alice", 0x41);
        using var bob = new OnionMessageTestNode("bob", 0x42);
        using var carol = new OnionMessageTestNode("carol", 0x43);
        using var dave = new OnionMessageTestNode("dave", 0x44);
        OnionMessageTestNode.Connect(sender, alice);
        OnionMessageTestNode.Connect(alice, bob);
        OnionMessageTestNode.Connect(bob, carol);
        OnionMessageTestNode.Connect(carol, dave);
        // The vector's next_node_ids are our keys
        Assert.Equal(nextNodeIds[0], Convert.ToHexStringLower(bob.NodeId));
        Assert.Equal(nextNodeIds[1], Convert.ToHexStringLower(carol.NodeId));
        Assert.Equal(nextNodeIds[2], Convert.ToHexStringLower(dave.NodeId));

        // Act: the sender's connection at Alice hands her the vector's first onion_message
        alice.Service.HandleIncoming(alice.LinkTo(sender), ParseWire(wireMessages[0]));
        await OnionMessageTestWaits.UntilAsync(() => dave.Metrics.GetDelivered("empty") == 1, ct);

        // Assert: each forward is the vector's next onion_message (path key and packet)
        Assert.Equal(wireMessages[1], ToWire(Assert.Single(alice.LinkTo(bob).Sent)));
        Assert.Equal(wireMessages[2], ToWire(Assert.Single(bob.LinkTo(carol).Sent)));
        Assert.Equal(wireMessages[3], ToWire(Assert.Single(carol.LinkTo(dave).Sent)));
        Assert.Equal(1, alice.Metrics.Forwarded);
        Assert.Equal(1, bob.Metrics.Forwarded);
        Assert.Equal(1, carol.Metrics.Forwarded);
    }

    /// <summary>
    /// type (513) || path_key || u16 len || onion_message_packet.
    /// </summary>
    private static OnionMessageMessage ParseWire(byte[] wire)
    {
        var body = wire.AsSpan(MessageTypeLength);
        var pathKey = new CompactPubKey(body[..PathKeyLength].ToArray());
        var length = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(PathKeyLength, LengthPrefixLength));
        var packet = body.Slice(PathKeyLength + LengthPrefixLength, length).ToArray();
        return new OnionMessageMessage(new OnionMessagePayload(pathKey, packet));
    }

    private static byte[] ToWire(OnionMessageMessage message)
    {
        var packet = message.Payload.OnionMessagePacket;
        var wire = new byte[MessageTypeLength + PathKeyLength + LengthPrefixLength + packet.Length];
        BinaryPrimitives.WriteUInt16BigEndian(wire, 513);
        ((byte[])message.Payload.PathKey).CopyTo(wire, MessageTypeLength);
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(MessageTypeLength + PathKeyLength), (ushort)packet.Length);
        packet.Span.CopyTo(wire.AsSpan(MessageTypeLength + PathKeyLength + LengthPrefixLength));
        return wire;
    }
}
namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.OnionMessages;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Onion.OnionMessages;
using Infrastructure.Bitcoin.Onion.RouteBlinding;

/// <summary>
/// Real crypto for the onion-message unit tests: four nodes, the builders and the unwrapper, and a raw builder for
/// messages the production writer refuses to make.
/// </summary>
internal sealed class OnionMessageTestKit
{
    public Secp256K1Math Secp256K1Math { get; } = new();
    public SphinxService Sphinx { get; }
    public RouteBlindingService RouteBlinding { get; }
    public BlindedMessagePathBuilder PathBuilder { get; }
    public OnionMessagePacketBuilder PacketBuilder { get; }
    public OnionMessageUnwrapper Unwrapper { get; }

    public PrivKey[] NodeKeys { get; } =
        Enumerable.Range(0x51, 4).Select(b => new PrivKey(Enumerable.Repeat((byte)b, 32).ToArray())).ToArray();

    public CompactPubKey[] NodeIds { get; }

    public OnionMessageTestKit()
    {
        Sphinx = new SphinxService(Secp256K1Math);
        RouteBlinding = new RouteBlindingService(Secp256K1Math);
        PathBuilder = new BlindedMessagePathBuilder(RouteBlinding);
        PacketBuilder = new OnionMessagePacketBuilder(Sphinx, PathBuilder);
        Unwrapper = new OnionMessageUnwrapper(Sphinx, RouteBlinding);
        var ecdh = new Ecdh();
        NodeIds = NodeKeys.Select(k => ecdh.GenerateKeyPair(k).CompactPubKey).ToArray();
    }

    /// <summary>
    /// Unwraps along the kit's nodes from <paramref name="firstNode"/> until a result is not a forward.
    /// </summary>
    public List<OnionMessageUnwrapResult> UnwrapChain(OnionMessageMessage message, int firstNode = 0)
    {
        var results = new List<OnionMessageUnwrapResult>();
        for (var i = firstNode; i < NodeKeys.Length; i++)
        {
            var result = Unwrapper.Unwrap(message, NodeKeys[i]);
            results.Add(result);
            if (result.Status != OnionMessageUnwrapStatus.Forward)
                break;
            message = result.NextMessage!;
        }

        return results;
    }

    /// <summary>
    /// A message over the first <paramref name="encryptedDataTlvs"/>.Length nodes with raw plaintext data per hop and a
    /// raw <c>onionmsg_tlv</c> per hop (given the hop's <c>encrypted_recipient_data</c>), bypassing the writer's rules.
    /// </summary>
    public OnionMessageMessage BuildRaw(byte[][] encryptedDataTlvs, Func<int, byte[], byte[]> payloadFor)
    {
        var nodeIds = NodeIds[..encryptedDataTlvs.Length];
        var path = RouteBlinding.CreateBlindedPath(nodeIds, encryptedDataTlvs, NodeKeys[3]);
        var hops = path.Hops.Select((h, i) => new OnionHop(h.BlindedNodeId,
                                                           payloadFor(i, h.EncryptedRecipientData.ToArray())))
                       .ToList();
        var packet = Sphinx.Construct(hops, NodeKeys[2], ReadOnlySpan<byte>.Empty, 1300, OnionPacketKind.OnionMessage);
        return new OnionMessageMessage(new OnionMessagePayload(path.FirstPathKey, packet.ToBytes()));
    }

    /// <summary>
    /// One TLV record with a one-byte type and length (types and lengths below 253), or a 3-byte length.
    /// </summary>
    public static byte[] Tlv(ulong type, byte[] value)
    {
        byte[] length = value.Length < 253
                            ? [(byte)value.Length]
                            : [0xFD, (byte)(value.Length >> 8), (byte)value.Length];
        byte[] typeBytes = type < 253 ? [(byte)type] : [0xFD, (byte)(type >> 8), (byte)type];
        return [.. typeBytes, .. length, .. value];
    }

    /// <summary>The plaintext <c>encrypted_data_tlv</c> naming <paramref name="nextNode"/>.</summary>
    public byte[] NextNodeData(int nextNode) =>
        RouteBlinding.EncodeRecipientData(new BlindedRecipientData { NextNodeId = NodeIds[nextNode] });
}
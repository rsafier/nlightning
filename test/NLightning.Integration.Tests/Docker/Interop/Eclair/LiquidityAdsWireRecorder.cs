using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;
using Infrastructure.Serialization.Messages;

/// <summary>
/// Records the raw wire bytes (type prefix included) of the messages that carry liquidity ads TLV 1339 (BOLT PR #1153,
/// NL-850), both ways: <c>init</c>, <c>open_channel2</c>/<c>accept_channel2</c>, <c>tx_init_rbf</c>/<c>tx_ack_rbf</c>,
/// <c>splice_init</c>/<c>splice_ack</c> and <c>node_announcement</c>.
/// </summary>
/// <remarks>
/// Install it through <c>NLightningTestNode.ConfigureServices</c>: it replaces the node's
/// <see cref="IMessageSerializer"/> with a decorator of the production <see cref="MessageSerializer"/> (the
/// <c>RawGossipRecorder</c> pattern) that also copies what the node writes. Parsing and writing are unchanged.
/// </remarks>
public sealed class LiquidityAdsWireRecorder
{
    /// <summary>TLV 1339 as written on the wire (<c>fd053b</c>).</summary>
    public const ulong TlvType = 1339;

    private static readonly HashSet<ushort> s_types =
    [
        (ushort)MessageTypes.Init, (ushort)MessageTypes.OpenChannel2, (ushort)MessageTypes.AcceptChannel2,
        (ushort)MessageTypes.TxInitRbf, (ushort)MessageTypes.TxAckRbf, (ushort)MessageTypes.SpliceInit,
        (ushort)MessageTypes.SpliceAck, (ushort)MessageTypes.NodeAnnouncement
    ];

    private readonly ConcurrentQueue<RecordedMessage> _messages = new();

    /// <summary>Every recorded message, in order.</summary>
    public IReadOnlyList<RecordedMessage> Messages => _messages.ToArray();

    /// <summary>The recorded messages of <paramref name="type"/> in one direction.</summary>
    public IReadOnlyList<RecordedMessage> Of(MessageTypes type, bool inbound) =>
        Messages.Where(m => m.Type == (ushort)type && m.Inbound == inbound).ToList();

    /// <summary>Replaces the node's <see cref="IMessageSerializer"/> with the recording decorator.</summary>
    public void Install(IServiceCollection services)
    {
        services.AddSingleton<IMessageSerializer>(sp =>
                                                      new RecordingSerializer(
                                                          new MessageSerializer(
                                                              sp.GetRequiredService<ILogger<MessageSerializer>>(),
                                                              sp.GetRequiredService<IMessageTypeSerializerFactory>()),
                                                          this));
    }

    /// <summary>
    /// The value of TLV 1339 in a recorded message: the last <c>fd053b</c> record whose length fits the message and
    /// that <paramref name="decodes"/> accepts (the TLV stream is the message's tail), or null.
    /// </summary>
    public static byte[]? FindTlv(byte[] wire, Func<byte[], bool> decodes)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(decodes);
        for (var i = wire.Length - 3; i >= 2; i--)
        {
            if (wire[i] != 0xfd || wire[i + 1] != 0x05 || wire[i + 2] != 0x3b
             || !TryReadBigSize(wire.AsSpan(i + 3), out var length, out var lengthBytes))
                continue;

            var start = i + 3 + lengthBytes;
            if (length > (ulong)(wire.Length - start))
                continue;

            var value = wire.AsSpan(start, (int)length).ToArray();
            if (decodes(value))
                return value;
        }

        return null;
    }

    private static bool TryReadBigSize(ReadOnlySpan<byte> data, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        if (data.IsEmpty)
            return false;

        switch (data[0])
        {
            case < 0xfd:
                value = data[0];
                length = 1;
                return true;
            case 0xfd when data.Length >= 3:
                value = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
                length = 3;
                return value >= 0xfd;
            case 0xfe when data.Length >= 5:
                value = BinaryPrimitives.ReadUInt32BigEndian(data[1..]);
                length = 5;
                return value > 0xffff;
            default:
                return false;
        }
    }

    private void Record(byte[] wire, bool inbound)
    {
        if (wire.Length < 2)
            return;

        var type = BinaryPrimitives.ReadUInt16BigEndian(wire);
        if (s_types.Contains(type))
            _messages.Enqueue(new RecordedMessage(type, inbound, wire));
    }

    /// <summary>One recorded message.</summary>
    /// <param name="Type">The message type.</param>
    /// <param name="Inbound">Received (true) or sent (false).</param>
    /// <param name="Wire">The whole message, u16 type prefix included.</param>
    public sealed record RecordedMessage(ushort Type, bool Inbound, byte[] Wire);

    private sealed class RecordingSerializer(IMessageSerializer inner, LiquidityAdsWireRecorder recorder)
        : IMessageSerializer
    {
        public async Task SerializeAsync(IMessage message, Stream stream)
        {
            using var buffer = new MemoryStream();
            await inner.SerializeAsync(message, buffer);
            var wire = buffer.ToArray();
            recorder.Record(wire, false);
            await stream.WriteAsync(wire);
        }

        public Task<TMessage?> DeserializeMessageAsync<TMessage>(Stream stream) where TMessage : class, IMessage =>
            inner.DeserializeMessageAsync<TMessage>(stream);

        public async Task<IMessage?> DeserializeMessageAsync(Stream stream)
        {
            var wire = new byte[stream.Length - stream.Position];
            await stream.ReadExactlyAsync(wire);
            recorder.Record(wire, true);

            using var copy = new MemoryStream(wire, false);
            return await inner.DeserializeMessageAsync(copy);
        }
    }
}
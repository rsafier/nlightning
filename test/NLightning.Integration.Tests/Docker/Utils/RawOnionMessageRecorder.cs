using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Integration.Tests.Docker.Utils;

using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;
using Infrastructure.Serialization.Messages;

/// <summary>
/// Records the raw wire bytes (type prefix included) of every BOLT 4 <c>onion_message</c> (513) a node receives and
/// sends, plus every BOLT 1 <c>warning</c> (1) and <c>error</c> (17), for the onion-message proofs (plan Proof M6):
/// what CLN forwarded to us, what we forwarded on, and that a dropped or junk message never made us warn the sender.
/// The recorded 513s are also the raw material for captured vectors (plan B0-T4).
/// </summary>
/// <remarks>
/// Install it with <see cref="Install"/> through <c>NLightningTestNode.ConfigureServices</c>: it replaces the node's
/// <see cref="IMessageSerializer"/> with a decorator of the production <see cref="MessageSerializer"/>, which every
/// connection's transport uses for both directions. The bytes are recorded before they are parsed, so a message the
/// node cannot parse is still recorded, then fails as usual. It does not know the peer, so a proof that needs
/// per-peer traffic gives the recorded node a single peer. The recorder outlives node restarts.
/// </remarks>
public sealed class RawOnionMessageRecorder
{
    public const ushort WarningType = 1;
    public const ushort ErrorType = 17;
    public const ushort OnionMessageType = 513;

    // onion_message: u16 type, point path_key (33), u16 len, onion_message_packet
    private const int PathKeyOffset = 2;
    private const int PathKeyLength = 33;
    private const int PacketLengthOffset = PathKeyOffset + PathKeyLength;
    private const int PacketOffset = PacketLengthOffset + 2;

    private readonly ConcurrentQueue<RecordedMessage> _traffic = new();

    /// <summary>
    /// Every recorded message, in order.
    /// </summary>
    public IReadOnlyList<RecordedMessage> Traffic => _traffic.ToArray();

    /// <summary>
    /// The recorded <c>onion_message</c>s the node received.
    /// </summary>
    public IReadOnlyList<RecordedMessage> ReceivedOnionMessages =>
        Traffic.Where(t => !t.Outbound && t.Type == OnionMessageType).ToList();

    /// <summary>
    /// The recorded <c>onion_message</c>s the node sent.
    /// </summary>
    public IReadOnlyList<RecordedMessage> SentOnionMessages =>
        Traffic.Where(t => t.Outbound && t.Type == OnionMessageType).ToList();

    /// <summary>
    /// The <c>warning</c>s and <c>error</c>s the node sent.
    /// </summary>
    public IReadOnlyList<RecordedMessage> SentWarningsAndErrors =>
        Traffic.Where(t => t.Outbound && t.Type is WarningType or ErrorType).ToList();

    /// <summary>
    /// One line counting the recorded messages per direction and type.
    /// </summary>
    public string Describe() =>
        string.Join(", ", Traffic.GroupBy(t => (t.Outbound, t.Type))
                                 .OrderBy(g => g.Key.Outbound)
                                 .ThenBy(g => g.Key.Type)
                                 .Select(g => $"{(g.Key.Outbound ? "sent" : "received")} {g.Key.Type}: {g.Count()}"));

    public void Install(IServiceCollection services)
    {
        services.AddSingleton<IMessageSerializer>(sp =>
                                                      new RecordingMessageSerializer(
                                                          new MessageSerializer(
                                                              sp.GetRequiredService<ILogger<MessageSerializer>>(),
                                                              sp.GetRequiredService<IMessageTypeSerializerFactory>()),
                                                          this));
    }

    /// <summary>
    /// Records one message if it is of a recorded type (tests of the recorder call it directly).
    /// </summary>
    internal void Record(byte[] wire, bool outbound)
    {
        if (wire.Length < 2)
            return;

        var type = BinaryPrimitives.ReadUInt16BigEndian(wire);
        if (type is OnionMessageType or WarningType or ErrorType)
            _traffic.Enqueue(new RecordedMessage(type, wire, outbound, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// One recorded message.
    /// </summary>
    /// <param name="Type">The message type.</param>
    /// <param name="Wire">The whole message (u16 type prefix included).</param>
    /// <param name="Outbound">Sent by the node (true) or received (false).</param>
    /// <param name="At">When it was serialized or read.</param>
    public sealed record RecordedMessage(ushort Type, byte[] Wire, bool Outbound, DateTimeOffset At)
    {
        public string Hex => Convert.ToHexStringLower(Wire);

        /// <summary>
        /// The <c>path_key</c> of an <c>onion_message</c>, or null for another type or a truncated message.
        /// </summary>
        public byte[]? PathKey =>
            Type == OnionMessageType && Wire.Length >= PacketOffset
                ? Wire[PathKeyOffset..PacketLengthOffset]
                : null;

        /// <summary>
        /// The <c>onion_message_packet</c> of an <c>onion_message</c> (its declared <c>len</c> bytes), or null for
        /// another type or a truncated message.
        /// </summary>
        public byte[]? OnionMessagePacket
        {
            get
            {
                if (Type != OnionMessageType || Wire.Length < PacketOffset)
                    return null;

                var length = BinaryPrimitives.ReadUInt16BigEndian(Wire.AsSpan(PacketLengthOffset));
                return Wire.Length >= PacketOffset + length ? Wire[PacketOffset..(PacketOffset + length)] : null;
            }
        }
    }

    private sealed class RecordingMessageSerializer(IMessageSerializer inner, RawOnionMessageRecorder recorder)
        : IMessageSerializer
    {
        public async Task SerializeAsync(IMessage message, Stream stream)
        {
            using var buffer = new MemoryStream();
            await inner.SerializeAsync(message, buffer);
            var wire = buffer.ToArray();
            recorder.Record(wire, outbound: true);
            await stream.WriteAsync(wire);
        }

        public Task<TMessage?> DeserializeMessageAsync<TMessage>(Stream stream) where TMessage : class, IMessage =>
            inner.DeserializeMessageAsync<TMessage>(stream);

        public async Task<IMessage?> DeserializeMessageAsync(Stream stream)
        {
            var wire = new byte[stream.Length - stream.Position];
            await stream.ReadExactlyAsync(wire);
            recorder.Record(wire, outbound: false);

            using var copy = new MemoryStream(wire, false);
            return await inner.DeserializeMessageAsync(copy);
        }
    }
}
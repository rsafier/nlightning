using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Integration.Tests.Docker.Gossip.Capture;

using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;
using Infrastructure.Serialization.Messages;

/// <summary>
/// Records the raw wire bytes (type prefix included) of every BOLT 7 gossip message (256-259) a node receives, before
/// they are parsed, so a capture test can turn what LND or CLN really sent into vectors
/// (<c>Tests.Utils/Vectors/Bolt7Vectors.cs</c>, plan G0-T5).
/// </summary>
/// <remarks>
/// Install it with <see cref="Install"/> through <c>NLightningTestNode.ConfigureServices</c>: it replaces the node's
/// <see cref="IMessageSerializer"/> with a decorator of the production <see cref="MessageSerializer"/>. The recorder
/// outlives node restarts. Parsing is unchanged: a message the node cannot parse is still recorded, then fails as usual.
/// </remarks>
public sealed class RawGossipRecorder
{
    private readonly ConcurrentQueue<RecordedGossip> _received = new();

    /// <summary>
    /// Every recorded message, in arrival order.
    /// </summary>
    public IReadOnlyList<RecordedGossip> Received => _received.ToArray();

    /// <summary>
    /// The recorded messages of <paramref name="type"/>.
    /// </summary>
    public IReadOnlyList<RecordedGossip> OfType(ushort type) => Received.Where(r => r.Type == type).ToList();

    /// <summary>
    /// Replaces the node's <see cref="IMessageSerializer"/> with a recording decorator of the production serializer.
    /// </summary>
    public void Install(IServiceCollection services)
    {
        services.AddSingleton<IMessageSerializer>(sp =>
                                                      new RecordingMessageSerializer(
                                                          new MessageSerializer(
                                                              sp.GetRequiredService<ILogger<MessageSerializer>>(),
                                                              sp.GetRequiredService<IMessageTypeSerializerFactory>()),
                                                          this));
    }

    private void Record(byte[] wire)
    {
        if (wire.Length < 2)
            return;

        var type = BinaryPrimitives.ReadUInt16BigEndian(wire);
        if (type is >= 256 and <= 259)
            _received.Enqueue(new RecordedGossip(type, wire, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// One received gossip message.
    /// </summary>
    /// <param name="Type">The message type.</param>
    /// <param name="Wire">The whole message (u16 type prefix included).</param>
    /// <param name="ReceivedAt">When it was read.</param>
    public sealed record RecordedGossip(ushort Type, byte[] Wire, DateTimeOffset ReceivedAt)
    {
        /// <summary>
        /// The payload hex (without the type prefix), the form <c>Bolt7Vectors</c> keeps.
        /// </summary>
        public string PayloadHex => Convert.ToHexStringLower(Wire.AsSpan(2));
    }

    private sealed class RecordingMessageSerializer(IMessageSerializer inner, RawGossipRecorder recorder)
        : IMessageSerializer
    {
        public Task SerializeAsync(IMessage message, Stream stream) => inner.SerializeAsync(message, stream);

        public Task<TMessage?> DeserializeMessageAsync<TMessage>(Stream stream) where TMessage : class, IMessage =>
            inner.DeserializeMessageAsync<TMessage>(stream);

        public async Task<IMessage?> DeserializeMessageAsync(Stream stream)
        {
            var wire = new byte[stream.Length - stream.Position];
            await stream.ReadExactlyAsync(wire);
            recorder.Record(wire);

            using var copy = new MemoryStream(wire, false);
            return await inner.DeserializeMessageAsync(copy);
        }
    }
}
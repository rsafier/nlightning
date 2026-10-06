using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLightning.Infrastructure.Serialization.Wire;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;
using Infrastructure.Serialization.Messages;

/// <summary>
/// Records the wire bytes (type prefix included) of every BOLT 7 message (256-265: announcements, updates, queries,
/// replies and timestamp filters; the taproot gossip messages 267, 269 and 271) a node receives <b>and sends</b>, for
/// the G3 proofs: which queries our node sent, which replies it got, and whether a message it relayed came back (plan
/// Proof G3 (a), (c)). Other types are recorded only when named at construction (e.g. <c>channel_reestablish</c>).
/// </summary>
/// <remarks>
/// Install it with <see cref="Install"/> through <c>NLightningTestNode.ConfigureServices</c>: it replaces the node's
/// <see cref="IMessageSerializer"/> with a decorator of the production <see cref="MessageSerializer"/>, which every
/// connection's transport uses for both directions. It does not know the peer, so a proof that needs per-peer traffic
/// gives the recorded node a single peer. The recorder outlives node restarts.
/// </remarks>
public sealed class GossipTrafficRecorder
{
    public const ushort QueryShortChannelIdsType = 261;
    public const ushort ReplyShortChannelIdsEndType = 262;
    public const ushort QueryChannelRangeType = 263;
    public const ushort ReplyChannelRangeType = 264;
    public const ushort GossipTimestampFilterType = 265;
    public const ushort ChannelAnnouncement2Type = 267;
    public const ushort NodeAnnouncement2Type = 269;
    public const ushort ChannelUpdate2Type = 271;

    private readonly HashSet<ushort> _extraTypes;
    private readonly ConcurrentQueue<GossipTraffic> _traffic = new();

    /// <param name="extraTypes">Message types recorded besides the gossip ones.</param>
    public GossipTrafficRecorder(params IEnumerable<ushort> extraTypes)
    {
        _extraTypes = [.. extraTypes];
    }

    /// <summary>
    /// Every recorded message, in order.
    /// </summary>
    public IReadOnlyList<GossipTraffic> Traffic => _traffic.ToArray();

    public IReadOnlyList<GossipTraffic> Received => Traffic.Where(t => !t.Outbound).ToList();

    public IReadOnlyList<GossipTraffic> Sent => Traffic.Where(t => t.Outbound).ToList();

    public int CountSent(ushort type) => Sent.Count(t => t.Type == type);

    public int CountReceived(ushort type) => Received.Count(t => t.Type == type);

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
                                                              sp.GetRequiredService<WireRegistry>()),
                                                          this));
    }

    private void Record(byte[] wire, bool outbound)
    {
        if (wire.Length < 2)
            return;

        var type = BinaryPrimitives.ReadUInt16BigEndian(wire);
        if (type is >= 256 and <= GossipTimestampFilterType or ChannelAnnouncement2Type or NodeAnnouncement2Type
                                                          or ChannelUpdate2Type
         || _extraTypes.Contains(type))
            _traffic.Enqueue(new GossipTraffic(type, wire, outbound, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// One recorded message.
    /// </summary>
    /// <param name="Type">The message type.</param>
    /// <param name="Wire">The whole message (u16 type prefix included).</param>
    /// <param name="Outbound">Sent by the node (true) or received (false).</param>
    /// <param name="At">When it was serialized or read.</param>
    public sealed record GossipTraffic(ushort Type, byte[] Wire, bool Outbound, DateTimeOffset At)
    {
        public string Hex => Convert.ToHexStringLower(Wire);
    }

    private sealed class RecordingMessageSerializer(IMessageSerializer inner, GossipTrafficRecorder recorder)
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

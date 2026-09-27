using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace NLightning.Application.OnionMessages;

/// <summary>
/// The onion-message counters (plan OM3-T3): one <see cref="Meter"/> named <see cref="MeterName"/> per instance (a
/// singleton in the node). The same counts are kept in memory for tests and logs.
/// </summary>
/// <remarks>
/// <para>Instruments (tags in brackets):</para>
/// <list type="bullet">
/// <item><c>nlightning.onion_messages.received</c>: admitted from peers (after the rate limit).</item>
/// <item><c>nlightning.onion_messages.forwarded</c>: handed to the next peer.</item>
/// <item><c>nlightning.onion_messages.delivered</c> [kind]: final hop reached us; kind <c>handler</c>,
/// <c>reply</c> or <c>empty</c>.</item>
/// <item><c>nlightning.onion_messages.dropped</c> [reason]: ignored (BOLT 4: nothing is sent back).</item>
/// <item><c>nlightning.onion_messages.sent</c>: our own messages queued to a first hop.</item>
/// </list>
/// </remarks>
public sealed class OnionMessageMetrics : IDisposable
{
    /// <summary>The meter name.</summary>
    public const string MeterName = "NLightning.OnionMessages";

    private readonly Counter<long> _received;
    private readonly Counter<long> _forwarded;
    private readonly Counter<long> _delivered;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _sent;
    private readonly ConcurrentDictionary<string, long> _counts = new();

    /// <summary>The meter.</summary>
    public Meter Meter { get; }

    public OnionMessageMetrics()
    {
        Meter = new Meter(MeterName);
        _received = Meter.CreateCounter<long>("nlightning.onion_messages.received");
        _forwarded = Meter.CreateCounter<long>("nlightning.onion_messages.forwarded");
        _delivered = Meter.CreateCounter<long>("nlightning.onion_messages.delivered");
        _dropped = Meter.CreateCounter<long>("nlightning.onion_messages.dropped");
        _sent = Meter.CreateCounter<long>("nlightning.onion_messages.sent");
    }

    /// <summary>Messages admitted from peers.</summary>
    public long Received => Get("received");

    /// <summary>Messages forwarded.</summary>
    public long Forwarded => Get("forwarded");

    /// <summary>Our own messages sent.</summary>
    public long Sent => Get("sent");

    /// <summary>Messages delivered to us of <paramref name="kind"/> (<c>handler</c>, <c>reply</c>, <c>empty</c>).
    /// </summary>
    public long GetDelivered(string kind) => Get("delivered:" + kind);

    /// <summary>Messages dropped for <paramref name="reason"/> (see <see cref="OnionMessageDropReasons"/>).</summary>
    public long GetDropped(string reason) => Get("dropped:" + reason);

    /// <summary>Every dropped message, whatever the reason.</summary>
    public long DroppedTotal => Get("dropped");

    internal void RecordReceived()
    {
        _received.Add(1);
        Increment("received");
    }

    internal void RecordForwarded()
    {
        _forwarded.Add(1);
        Increment("forwarded");
    }

    internal void RecordDelivered(string kind)
    {
        _delivered.Add(1, new KeyValuePair<string, object?>("kind", kind));
        Increment("delivered:" + kind);
    }

    internal void RecordDropped(string reason)
    {
        _dropped.Add(1, new KeyValuePair<string, object?>("reason", reason));
        Increment("dropped");
        Increment("dropped:" + reason);
    }

    internal void RecordSent()
    {
        _sent.Add(1);
        Increment("sent");
    }

    public void Dispose() => Meter.Dispose();

    private long Get(string key) => _counts.TryGetValue(key, out var value) ? value : 0;

    private void Increment(string key) => _counts.AddOrUpdate(key, 1, (_, value) => value + 1);
}
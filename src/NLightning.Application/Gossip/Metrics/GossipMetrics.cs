using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;

namespace NLightning.Application.Gossip.Metrics;

using Domain.Protocol.Constants;

/// <summary>
/// The gossip counters (BOLT 7 plan G5-T4, the metrics half): one <see cref="Meter"/> named
/// <see cref="MeterName"/> per instance (a singleton in the node), recorded by the graph ingress, the relay and the
/// sync. Any <c>System.Diagnostics.Metrics</c> listener (OpenTelemetry, <c>dotnet-counters</c>, a test's
/// <c>MeterListener</c>) reads it.
/// </summary>
/// <remarks>
/// <para>Instruments (tags in brackets):</para>
/// <list type="bullet">
/// <item><c>nlightning.gossip.messages.received</c> [type]: graph gossip handed over by peers.</item>
/// <item><c>nlightning.gossip.messages.accepted</c> [type]: applied to the graph.</item>
/// <item><c>nlightning.gossip.messages.rejected</c> [type, reason]: validated and refused (BOLT 7 "ignore", a warning,
/// a rate limit, a graph limit, a banned peer).</item>
/// <item><c>nlightning.gossip.messages.orphaned</c> [type]: kept until their channel or node is known.</item>
/// <item><c>nlightning.gossip.messages.dropped</c> [reason]: never validated (a full queue, retries given up) or
/// taken out of a relay backlog.</item>
/// <item><c>nlightning.gossip.messages.relayed</c> [type, path]: sent (or queued on a peer's outbox); path
/// <c>own</c> or <c>others</c>.</item>
/// <item><c>nlightning.gossip.chain.lookups</c> [status]: funding output lookups of received announcements.</item>
/// <item><c>nlightning.gossip.peers.banned</c>: peers banned for misbehaviour.</item>
/// <item><c>nlightning.gossip.sync.duration</c> [outcome] (seconds): range syncs with a peer.</item>
/// <item><c>nlightning.gossip.queue.depth</c> [queue]: the registered queue depths (observable).</item>
/// </list>
/// <para>Thread-safe; recording never throws and costs nothing while no listener is enabled.</para>
/// </remarks>
public sealed class GossipMetrics : IDisposable
{
    /// <summary>The meter's name.</summary>
    public const string MeterName = "NLightning.Gossip";

    /// <summary>The <c>type</c> tag.</summary>
    public const string TypeTag = "type";

    /// <summary>The <c>reason</c> tag.</summary>
    public const string ReasonTag = "reason";

    /// <summary>The <c>queue</c> tag of the queue depth gauge.</summary>
    public const string QueueTag = "queue";

    /// <summary>The <c>status</c> tag of the chain lookups.</summary>
    public const string StatusTag = "status";

    /// <summary>The <c>outcome</c> tag of the sync durations.</summary>
    public const string OutcomeTag = "outcome";

    /// <summary>The <c>path</c> tag of the relayed messages.</summary>
    public const string PathTag = "path";

    private static readonly ConcurrentDictionary<Enum, string> s_tagValues = new();

    private readonly Counter<long> _received;
    private readonly Counter<long> _accepted;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _orphaned;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _relayed;
    private readonly Counter<long> _chainLookups;
    private readonly Counter<long> _banned;
    private readonly Histogram<double> _syncDuration;
    private readonly Lock _queuesLock = new();
    private readonly Dictionary<string, Func<long>> _queues = new(StringComparer.Ordinal);

    public GossipMetrics()
    {
        Meter = new Meter(MeterName);
        _received = Meter.CreateCounter<long>("nlightning.gossip.messages.received", "{message}",
                                              "Graph gossip messages handed over by peers");
        _accepted = Meter.CreateCounter<long>("nlightning.gossip.messages.accepted", "{message}",
                                              "Graph gossip messages applied to the graph");
        _rejected = Meter.CreateCounter<long>("nlightning.gossip.messages.rejected", "{message}",
                                              "Graph gossip messages refused, by reason");
        _orphaned = Meter.CreateCounter<long>("nlightning.gossip.messages.orphaned", "{message}",
                                              "Graph gossip messages kept until their channel or node is known");
        _dropped = Meter.CreateCounter<long>("nlightning.gossip.messages.dropped", "{message}",
                                             "Gossip messages dropped before validation or from a relay backlog");
        _relayed = Meter.CreateCounter<long>("nlightning.gossip.messages.relayed", "{message}",
                                             "Gossip messages sent or queued for peers");
        _chainLookups = Meter.CreateCounter<long>("nlightning.gossip.chain.lookups", "{lookup}",
                                                  "Funding output lookups of received channel announcements");
        _banned = Meter.CreateCounter<long>("nlightning.gossip.peers.banned", "{peer}",
                                            "Peers banned for gossip misbehaviour");
        _syncDuration = Meter.CreateHistogram<double>("nlightning.gossip.sync.duration", "s",
                                                      "Duration of a range sync with a peer");
        Meter.CreateObservableGauge("nlightning.gossip.queue.depth", ObserveQueues, "{message}",
                                    "Messages waiting in the gossip queues");
    }

    /// <summary>The meter (a listener may filter on it).</summary>
    public Meter Meter { get; }

    /// <summary>A graph gossip message was handed over by a peer.</summary>
    public void RecordReceived(MessageTypes type) => _received.Add(1, TypeTagOf(type));

    /// <summary>A message was applied to the graph.</summary>
    public void RecordAccepted(MessageTypes type) => _accepted.Add(1, TypeTagOf(type));

    /// <summary>A message was validated and refused for <paramref name="reason"/>.</summary>
    public void RecordRejected(MessageTypes type, string reason) =>
        _rejected.Add(1, TypeTagOf(type), new KeyValuePair<string, object?>(ReasonTag, reason));

    /// <summary>A message waits for its channel or node.</summary>
    public void RecordOrphaned(MessageTypes type) => _orphaned.Add(1, TypeTagOf(type));

    /// <summary><paramref name="count"/> messages were dropped for <paramref name="reason"/>.</summary>
    public void RecordDropped(string reason, long count = 1)
    {
        if (count > 0)
            _dropped.Add(count, new KeyValuePair<string, object?>(ReasonTag, reason));
    }

    /// <summary>A message went to a peer (sent, or queued on its outbox).</summary>
    public void RecordRelayed(MessageTypes type, string path) =>
        _relayed.Add(1, TypeTagOf(type), new KeyValuePair<string, object?>(PathTag, path));

    /// <summary>A funding output lookup answered <paramref name="status"/>.</summary>
    public void RecordChainLookup(string status) =>
        _chainLookups.Add(1, new KeyValuePair<string, object?>(StatusTag, status));

    /// <summary>A peer was banned for misbehaviour.</summary>
    public void RecordPeerBanned() => _banned.Add(1);

    /// <summary>A range sync with a peer ended after <paramref name="duration"/>.</summary>
    public void RecordSyncDuration(TimeSpan duration, bool completed) =>
        _syncDuration.Record(Math.Max(0, duration.TotalSeconds),
                             new KeyValuePair<string, object?>(OutcomeTag, completed ? "completed" : "failed"));

    /// <summary>
    /// Reports <paramref name="read"/> as the depth of <paramref name="queue"/> (replacing an earlier source of that
    /// name).
    /// </summary>
    public void RegisterQueue(string queue, Func<long> read)
    {
        ArgumentException.ThrowIfNullOrEmpty(queue);
        ArgumentNullException.ThrowIfNull(read);
        lock (_queuesLock)
            _queues[queue] = read;
    }

    /// <inheritdoc />
    public void Dispose() => Meter.Dispose();

    /// <summary>The <c>type</c> tag value of a gossip message type.</summary>
    public static string TypeName(MessageTypes type) => type switch
    {
        MessageTypes.ChannelAnnouncement => "channel_announcement",
        MessageTypes.NodeAnnouncement => "node_announcement",
        MessageTypes.ChannelUpdate => "channel_update",
        _ => TagValue(type)
    };

    /// <summary>
    /// The tag value of an enum member: its name in snake_case (<c>DuplicateUpdate</c> → <c>duplicate_update</c>), so
    /// validator reasons and lookup statuses read like the <see cref="GossipMetricReasons"/> values. Cached.
    /// </summary>
    public static string TagValue(Enum value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return s_tagValues.GetOrAdd(value, v => ToSnakeCase(v.ToString()));
    }

    /// <summary><c>PascalCase</c> (or <c>camelCase</c>) to <c>snake_case</c>; an acronym stays one word.</summary>
    internal static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var previousIsLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                var endsAcronym = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length
                               && char.IsLower(name[i + 1]);
                if (previousIsLowerOrDigit || endsAcronym)
                    builder.Append('_');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static KeyValuePair<string, object?> TypeTagOf(MessageTypes type) => new(TypeTag, TypeName(type));

    private IEnumerable<Measurement<long>> ObserveQueues()
    {
        List<KeyValuePair<string, Func<long>>> queues;
        lock (_queuesLock)
            queues = _queues.ToList();

        var measurements = new List<Measurement<long>>(queues.Count);
        foreach (var (name, read) in queues)
        {
            long depth;
            try
            {
                depth = read();
            }
            catch
            {
                continue;
            }

            measurements.Add(new Measurement<long>(depth, new KeyValuePair<string, object?>(QueueTag, name)));
        }

        return measurements;
    }
}
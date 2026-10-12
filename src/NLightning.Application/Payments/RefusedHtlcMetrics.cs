using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace NLightning.Application.Payments;

/// <summary>
/// Why an incoming HTLC was failed back before a forward circuit was written for it (NL-598); the reasons of
/// <see cref="IRefusedHtlcCounter"/> and the <c>reason</c> tag of <see cref="RefusedHtlcMetrics"/>.
/// </summary>
public enum RefusedHtlcReason
{
    /// <summary>Our forwarding policy refused the relay (fee, CLTV delta, amount bounds, disabled channel).</summary>
    ForwardPolicy = 0,

    /// <summary>The onion's next channel (or node's channel) is unknown to us.</summary>
    UnknownNextChannel = 1,

    /// <summary>The node is draining for its shutdown (NL-591) and accepts no new payment.</summary>
    ShutdownDrain = 2,

    /// <summary>Chain processing is halted (NL-216) or no block was processed yet: nothing new is accepted.</summary>
    ChainHalt = 3,

    /// <summary>We are the final node and no invoice (or keysend record) carries the payment hash.</summary>
    UnknownPaymentHash = 4,

    /// <summary>We are the final node and the HTLC is wrong otherwise (amount, CLTV, expired).</summary>
    FinalHop = 5,

    /// <summary>The onion could not be peeled (BOLT 4 BADONION).</summary>
    MalformedOnion = 6,

    /// <summary>The peer added the HTLC after we sent <c>shutdown</c> (BOLT 2 B2-SHUT-S08).</summary>
    AddedAfterShutdown = 7
}

/// <summary>
/// Counts the incoming HTLCs that were failed back before a forward circuit exists for them (NL-598), by reason, from
/// process start. In memory only; the snapshot rides the <c>listforwards</c> summary (NL-597).
/// </summary>
/// <remarks>Counted once per refused HTLC: the switch counts only an HTLC whose onion it processes for the first time
/// (no stored secret — a restart's replay carries one and is not counted again).</remarks>
public interface IRefusedHtlcCounter
{
    /// <summary>Counts one refused HTLC of <paramref name="reason"/>.</summary>
    void Count(RefusedHtlcReason reason);

    /// <summary>The counts so far, by reason; reasons with a zero count are left out.</summary>
    IReadOnlyDictionary<RefusedHtlcReason, long> Snapshot();

    /// <summary>The total of <see cref="Snapshot"/>.</summary>
    long Total();
}

/// <summary>
/// The default <see cref="IRefusedHtlcCounter"/>: in-memory counts plus a <see cref="Meter"/> named
/// <see cref="MeterName"/> in the style of the gossip metrics, so any
/// <c>System.Diagnostics.Metrics</c> listener reads the same numbers.
/// </summary>
public sealed class RefusedHtlcMetrics : IRefusedHtlcCounter, IDisposable
{
    /// <summary>The meter's name.</summary>
    public const string MeterName = "NLightning.Payments";

    /// <summary>The <c>reason</c> tag of the counter.</summary>
    public const string ReasonTag = "reason";

    private static readonly ConcurrentDictionary<RefusedHtlcReason, string> s_tagValues = new();

    private readonly ConcurrentDictionary<RefusedHtlcReason, long> _counts = new();
    private readonly Counter<long> _refused;

    /// <summary>The meter, for tests that assert the instruments.</summary>
    internal Meter Meter { get; }

    public RefusedHtlcMetrics()
    {
        Meter = new Meter(MeterName);
        _refused = Meter.CreateCounter<long>("nlightning.payments.htlcs.refused", "{htlc}",
                                             "Incoming HTLCs failed back before a forward circuit, by reason");
    }

    /// <inheritdoc />
    public void Count(RefusedHtlcReason reason)
    {
        _counts.AddOrUpdate(reason, 1, (_, n) => n + 1);
        _refused.Add(1, new KeyValuePair<string, object?>(ReasonTag, TagValueOf(reason)));
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<RefusedHtlcReason, long> Snapshot() =>
        new Dictionary<RefusedHtlcReason, long>(_counts);

    /// <inheritdoc />
    public long Total() => _counts.Values.Sum();

    /// <summary>The tag value of a reason, interned so recording allocates nothing steady-state.</summary>
    internal static string TagValueOf(RefusedHtlcReason reason) =>
        s_tagValues.GetOrAdd(reason, static r => r.ToString());

    public void Dispose() => Meter.Dispose();
}
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace NLightning.GossipProbe;

using Application.Gossip.Metrics;

/// <summary>
/// Listens to the node's <c>NLightning.Gossip</c> meter: counters summed per instrument and tag set, histograms as
/// count/sum/max, and the queue depth gauges read on demand (<see cref="ReadGauges"/>).
/// </summary>
public sealed class GossipMeterCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HistogramState> _histograms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _gauges = new(StringComparer.Ordinal);

    public GossipMeterCollector()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == GossipMetrics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var key = Key(instrument.Name, tags);
            if (instrument is ObservableGauge<long>)
                _gauges[key] = value;
            else
                _counters.AddOrUpdate(key, value, (_, current) => current + value);
        });
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var key = Key(instrument.Name, tags);
            _histograms.AddOrUpdate(key, _ => new HistogramState(1, value, value),
                                    (_, s) => new HistogramState(s.Count + 1, s.Sum + value, Math.Max(s.Max, value)));
        });
        _listener.Start();
    }

    /// <summary>The counters by "instrument{tag=value,...}".</summary>
    public IReadOnlyDictionary<string, long> Counters => new SortedDictionary<string, long>(_counters);

    /// <summary>The histograms by "instrument{tag=value,...}".</summary>
    public IReadOnlyDictionary<string, HistogramState> Histograms =>
        new SortedDictionary<string, HistogramState>(_histograms);

    /// <summary>Reads the observable gauges now (the queue depths, keyed by their <c>queue</c> tag).</summary>
    public IReadOnlyDictionary<string, long> ReadGauges()
    {
        _listener.RecordObservableInstruments();
        return _gauges.ToDictionary(g => g.Key.Replace("nlightning.gossip.queue.depth{queue=", "", StringComparison.Ordinal)
                                          .TrimEnd('}'),
                                    g => g.Value);
    }

    /// <summary>The sum of every counter of <paramref name="instrument"/> (all tag sets).</summary>
    public long Sum(string instrument) =>
        _counters.Where(c => c.Key == instrument || c.Key.StartsWith(instrument + "{", StringComparison.Ordinal))
                 .Sum(c => c.Value);

    public void Dispose() => _listener.Dispose();

    private static string Key(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
            return name;

        var parts = new List<string>(tags.Length);
        foreach (var tag in tags)
            parts.Add($"{tag.Key}={tag.Value}");
        parts.Sort(StringComparer.Ordinal);
        return $"{name}{{{string.Join(',', parts)}}}";
    }

    public sealed record HistogramState(long Count, double Sum, double Max);
}
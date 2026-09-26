using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace NLightning.Application.Tests.Gossip.Metrics;

using Application.Gossip.Metrics;

/// <summary>
/// Listens to one <see cref="GossipMetrics"/> instance's meter only (other tests' meters of the same name run in
/// parallel) and sums what was recorded, per instrument and tags.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class GossipMetricsRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Measurement> _measurements = new();

    public GossipMetricsRecorder(GossipMetrics metrics)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>The sum of <paramref name="instrument"/>'s measurements whose tags include every given pair.</summary>
    public double Sum(string instrument, params (string Key, string Value)[] tags) =>
        _measurements.Where(m => m.Instrument == instrument
                              && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && Equals(v, t.Value)))
                     .Sum(m => m.Value);

    /// <summary>How many measurements <paramref name="instrument"/> recorded with every given tag pair.</summary>
    public int Count(string instrument, params (string Key, string Value)[] tags) =>
        _measurements.Count(m => m.Instrument == instrument
                              && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && Equals(v, t.Value)));

    /// <summary>The latest observed value of the queue gauge for <paramref name="queue"/>.</summary>
    public double ObserveQueue(string queue)
    {
        _listener.RecordObservableInstruments();
        return _measurements.Where(m => m.Instrument == "nlightning.gossip.queue.depth"
                                     && m.Tags.TryGetValue(GossipMetrics.QueueTag, out var q) && Equals(q, queue))
                            .Select(m => m.Value).LastOrDefault(double.NaN);
    }

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
            copy[tag.Key] = tag.Value;
        _measurements.Enqueue(new Measurement(instrument.Name, value, copy));
    }

    private sealed record Measurement(string Instrument, double Value, Dictionary<string, object?> Tags);
}
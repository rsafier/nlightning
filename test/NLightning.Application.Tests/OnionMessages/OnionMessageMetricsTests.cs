using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.OnionMessages;

using Application.OnionMessages;
using Application.Tests.OnionMessages.Harness;

/// <summary>
/// NL-446: the onion-message queue depth gauge (plan §3.4) — the registered queues are observed, a replaced source
/// wins, and a failing one is skipped.
/// </summary>
public class OnionMessageMetricsTests
{
    [Fact]
    public void Given_RegisteredQueues_When_Observed_Then_EachDepthIsReported()
    {
        // Arrange
        using var metrics = new OnionMessageMetrics();
        using var recorder = new Recorder(metrics);
        metrics.RegisterQueue("incoming", () => 3);
        metrics.RegisterQueue("handler", () => 1);
        metrics.RegisterQueue("outbox", () => 9);

        // Act
        var (incoming, handler, outbox) = (recorder.Observe("incoming"), recorder.Observe("handler"),
                                           recorder.Observe("outbox"));

        // Assert
        Assert.Equal(3, incoming);
        Assert.Equal(1, handler);
        Assert.Equal(9, outbox);
    }

    [Fact]
    public void Given_AReplacedOrFailingQueueSource_When_Observed_Then_TheLatestValueWinsAndAFailingOneIsSkipped()
    {
        // Arrange
        using var metrics = new OnionMessageMetrics();
        using var recorder = new Recorder(metrics);
        var depth = 5L;
        metrics.RegisterQueue("incoming", () => depth);
        metrics.RegisterQueue("broken", () => throw new InvalidOperationException("gone"));

        // Act
        var first = recorder.Observe("incoming");
        depth = 7;
        metrics.RegisterQueue("incoming", () => depth * 2);
        var replaced = recorder.Observe("incoming");

        // Assert
        Assert.Equal(5, first);
        Assert.Equal(14, replaced);
        Assert.True(double.IsNaN(recorder.Observe("broken")));
    }

    [Fact]
    public void Given_AServiceNode_When_ItIsBuilt_Then_ItsTwoQueuesAreRegistered()
    {
        // Arrange (the outbox queue is the peer manager's, not the service's)
        using var node = new OnionMessageTestNode("n", 1);
        using var recorder = new Recorder(node.Metrics);

        // Act
        var (incoming, handler, outbox) = (recorder.Observe("incoming"), recorder.Observe("handler"),
                                           recorder.Observe("outbox"));

        // Assert
        Assert.Equal(0, incoming);
        Assert.Equal(0, handler);
        Assert.True(double.IsNaN(outbox));
    }

    [Fact]
    public void Given_AddOnionMessageServices_When_TheDropCounterIsResolved_Then_ItIsTheMetrics()
    {
        // Arrange: NL-464, the message reader's malformed 513s reach the metrics through this port
        var services = new ServiceCollection();
        services.AddOnionMessageServices();
        using var provider = services.BuildServiceProvider();

        // Act
        var counter = provider.GetRequiredService<Domain.Protocol.OnionMessages.Interfaces.IOnionMessageDropCounter>();
        counter.RecordDropped(OnionMessageDropReasons.Malformed);
        counter.RecordDropped(OnionMessageDropReasons.Malformed);
        var metrics = provider.GetRequiredService<OnionMessageMetrics>();

        // Assert: the shared counter set (the exported one and the in-memory one)
        Assert.Same(metrics, counter);
        Assert.Equal(2, metrics.GetDropped(OnionMessageDropReasons.Malformed));
        Assert.Equal(2, metrics.DroppedTotal);
    }

    /// <summary>
    /// Listens to one <see cref="OnionMessageMetrics"/> instance's meter only (other tests' meters of the same name
    /// run in parallel) and keeps the queue gauge's latest observation per queue.
    /// </summary>
    [ExcludeFromCodeCoverage]
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, double> _observations = new();

        public Recorder(OnionMessageMetrics metrics)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == OnionMessageMetrics.QueueTag && tag.Value is string queue)
                        _observations[queue] = value;
                }
            });
            _listener.Start();
        }

        /// <summary>The latest observed value of the queue gauge for <paramref name="queue"/>.</summary>
        public double Observe(string queue)
        {
            _listener.RecordObservableInstruments();
            return _observations.TryGetValue(queue, out var value) ? value : double.NaN;
        }

        public void Dispose() => _listener.Dispose();
    }
}
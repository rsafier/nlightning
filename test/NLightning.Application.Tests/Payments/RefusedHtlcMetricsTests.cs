using System.Diagnostics.Metrics;

namespace NLightning.Application.Tests.Payments;

using Application.Payments;

/// <summary>
/// The counter of incoming HTLCs refused before a forward circuit (NL-598): one count per reason, in memory from
/// process start, mirrored onto the <c>NLightning.Payments</c> meter with a <c>reason</c> tag.
/// </summary>
public sealed class RefusedHtlcMetricsTests : IDisposable
{
    private readonly RefusedHtlcMetrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly Dictionary<(string Meter, string Instrument, string? Reason), long> _readings = [];

    public RefusedHtlcMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RefusedHtlcMetrics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            string? reason = null;
            for (var i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == RefusedHtlcMetrics.ReasonTag)
                    reason = tags[i].Value as string;
            }
            var key = (instrument.Meter.Name, instrument.Name, reason);
            _readings[key] = _readings.GetValueOrDefault(key) + measurement;
        });
        _listener.Start();
    }

    [Fact]
    public void Given_NoRefusals_When_Snapshotted_Then_EmptyAndZero()
    {
        // Act
        var snapshot = _metrics.Snapshot();

        // Assert
        Assert.Empty(snapshot);
        Assert.Equal(0, _metrics.Total());
    }

    [Fact]
    public void Given_RefusalsOfSeveralReasons_When_Counted_Then_EachReasonCountsWithItsTag()
    {
        // Arrange - NL-598: count once per refused HTLC, by reason
        _metrics.Count(RefusedHtlcReason.ShutdownDrain);
        _metrics.Count(RefusedHtlcReason.ShutdownDrain);
        _metrics.Count(RefusedHtlcReason.UnknownPaymentHash);

        // Act
        var snapshot = _metrics.Snapshot();

        // Assert
        Assert.Equal(2, snapshot[RefusedHtlcReason.ShutdownDrain]);
        Assert.Equal(1, snapshot[RefusedHtlcReason.UnknownPaymentHash]);
        Assert.Equal(3, _metrics.Total());
        Assert.Equal(2, _readings[(RefusedHtlcMetrics.MeterName, "nlightning.payments.htlcs.refused",
                                   nameof(RefusedHtlcReason.ShutdownDrain))]);
        Assert.Equal(1, _readings[(RefusedHtlcMetrics.MeterName, "nlightning.payments.htlcs.refused",
                                   nameof(RefusedHtlcReason.UnknownPaymentHash))]);
    }

    public void Dispose()
    {
        _metrics.Dispose();
        _listener.Dispose();
    }
}
using System.Diagnostics.Metrics;

namespace NLightning.Application.Tests.Payments;

using Application.Payments;

/// <summary>
/// The counter of incoming HTLCs refused before a forward circuit (NL-598): one count per reason, in memory from
/// process start, mirrored onto the <c>NLightning.Payments</c> meter with a <c>reason</c> tag. The listener keys its
/// readings by instrument instance — parallel test classes run their own <c>RefusedHtlcMetrics</c> under the same
/// meter name, and their measurements must not bleed into these readings.
/// </summary>
public sealed class RefusedHtlcMetricsTests : IDisposable
{
    private readonly RefusedHtlcMetrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _readings = [];

    public RefusedHtlcMetricsTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, _metrics.Meter))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (!ReferenceEquals(instrument.Meter, _metrics.Meter))
                return;

            string? reason = null;
            for (var i = 0; i < tags.Length; i++)
            {
                if (tags[i].Key == RefusedHtlcMetrics.ReasonTag)
                    reason = tags[i].Value as string;
            }

            _readings[reason ?? "?"] = _readings.GetValueOrDefault(reason ?? "?") + measurement;
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
        Assert.Equal(2, _readings.GetValueOrDefault(nameof(RefusedHtlcReason.ShutdownDrain)));
        Assert.Equal(1, _readings.GetValueOrDefault(nameof(RefusedHtlcReason.UnknownPaymentHash)));
    }

    public void Dispose()
    {
        _metrics.Dispose();
        _listener.Dispose();
    }
}
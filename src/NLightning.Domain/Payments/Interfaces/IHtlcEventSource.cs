namespace NLightning.Domain.Payments.Interfaces;

using Events;

/// <summary>Live-only passive HTLC observations, with independent bounded subscriptions.</summary>
public interface IHtlcEventSource
{
    IHtlcEventSubscription Subscribe(int capacity = 1024);
}

public interface IHtlcEventSubscription : IDisposable
{
    bool Overflowed { get; }
    CancellationToken OverflowCancellationToken { get; }
    IAsyncEnumerable<HtlcActivityEvent> ReadAllAsync(CancellationToken cancellationToken = default);
}

public interface IHtlcEventPublisher
{
    bool HasSubscribers { get; }
    void Publish(HtlcActivityEvent activity);

    /// <summary>Captures the recipients at observation time so asynchronous enrichment cannot replay to new readers.</summary>
    Action<HtlcActivityEvent> CapturePublisher();

    /// <summary>Ends current subscriptions when an observation could not be constructed.</summary>
    void InvalidateSubscriptions();
}
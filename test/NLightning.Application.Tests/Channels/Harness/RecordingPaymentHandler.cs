using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Tests.Channels.Harness;

using Application.Payments.Switch;
using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;

/// <summary>Records the resolutions of our own payments (<c>HtlcOrigin.Local</c>).</summary>
[ExcludeFromCodeCoverage]
internal sealed class RecordingPaymentHandler : ILocalPaymentHtlcHandler
{
    public List<OutgoingHtlcFulfilled> Fulfilled { get; } = [];
    public List<OutgoingHtlcFailed> Failed { get; } = [];

    public Task HandleFulfilledAsync(OutgoingHtlcFulfilled fulfilled, Hash paymentHash,
                                     CancellationToken cancellationToken)
    {
        lock (Fulfilled)
            Fulfilled.Add(fulfilled);
        return Task.CompletedTask;
    }

    public Task HandleFailedAsync(OutgoingHtlcFailed failed, Hash paymentHash, CancellationToken cancellationToken)
    {
        lock (Failed)
            Failed.Add(failed);
        return Task.CompletedTask;
    }
}
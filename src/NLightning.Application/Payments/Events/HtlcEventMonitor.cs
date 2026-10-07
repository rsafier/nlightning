using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Events;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// A bounded, independent observer of fresh committed transitions. Metadata enrichment owns a fresh scope, never
/// the funds-path unit of work. Startup/link-up domain-event replay is deliberately outside this observation feed.
/// </summary>
public sealed class HtlcEventMonitor : IDisposable
{
    private readonly IHtlcEventPublisher _publisher;
    private readonly IChannelMemoryRepository _channels;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<HtlcEventMonitor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(ChannelId, ulong), HtlcActivityRole> _roles = new();
    private readonly Dictionary<(ChannelId, ulong), Attribution> _outgoing = new();
    private readonly AsyncLocal<ushort?> _localFailure = new();
    private readonly AsyncLocal<HtlcOrigin?> _offeredOrigin = new();
    private readonly ConcurrentDictionary<(ChannelId, ulong), ForwardInfo> _forwards = new();
    private readonly Channel<Observation> _pending = Channel.CreateBounded<Observation>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;

    public HtlcEventMonitor(IHtlcEventPublisher publisher, IChannelMemoryRepository channels,
                            IServiceScopeFactory scopes, ILogger<HtlcEventMonitor> logger,
                            TimeProvider? timeProvider = null)
    {
        _publisher = publisher;
        _channels = channels;
        _scopes = scopes;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _worker = Task.Run(ProcessAsync);
    }

    public void ClassifyIncoming(ChannelId channelId, ulong htlcId, HtlcActivityRole role)
    {
        // Only live readers need transient classification; bound stale force-closed entries as well.
        var key = (channelId, htlcId);
        if (_roles.Count >= 8192 && !_roles.ContainsKey(key))
        {
            InvalidateSubscribers();
            return;
        }
        _roles[key] = role;
    }

    public void ForgetIncoming(ChannelId channelId, ulong htlcId)
    {
        _roles.TryRemove((channelId, htlcId), out _);
        _forwards.TryRemove((channelId, htlcId), out _);
    }

    public void CaptureForward(ForwardCircuitModel circuit)
    {
        if (_forwards.Count >= 8192)
        {
            InvalidateSubscribers();
            _forwards.Clear();
        }
        _forwards[(circuit.IncomingChannelId, circuit.IncomingHtlcId)] =
            new ForwardInfo(circuit.IncomingAmount.MilliSatoshi, circuit.IncomingCltvExpiry,
                            circuit.OutgoingAmount.MilliSatoshi, circuit.OutgoingCltvExpiry,
                            Scid(circuit.IncomingChannelId));
    }

    public void ForgetOutgoing(ChannelId channelId, ulong htlcId)
    {
        if (!_pending.Writer.TryWrite(new Observation(channelId, 0, null, default, [], null, null, ForgetId: htlcId)))
            InvalidateSubscribers();
    }

    public IDisposable WithOrigin(HtlcOrigin origin)
    {
        var previous = _offeredOrigin.Value;
        _offeredOrigin.Value = origin;
        return new RestoreFailure(() => _offeredOrigin.Value = previous);
    }

    public IDisposable WithLocalFailure(ushort code)
    {
        var previous = _localFailure.Value;
        _localFailure.Value = code;
        return new RestoreFailure(() => _localFailure.Value = previous);
    }

    private sealed class RestoreFailure(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>Captures immutable facts synchronously after the save; neither producer nor peer waits for metadata.</summary>
    public void ObserveCommitted(ChannelModel channel, CommitmentsResult result)
    {
        try
        {
            ObserveCommittedCore(channel, result);
        }
        catch (Exception exception)
        {
            InvalidateSubscribers();
            _logger.LogWarning(exception, "Could not enqueue HTLC observation for channel {ChannelId}", channel.ChannelId);
        }
    }

    private void ObserveCommittedCore(ChannelModel channel, CommitmentsResult result)
    {
        var incomingIds = result.Outbound.Select(outbound => outbound switch
        {
            OutboundFulfillHtlc fulfill => (ulong?)fulfill.Id,
            OutboundFailHtlc fail => fail.Id,
            OutboundFailMalformedHtlc malformed => malformed.Id,
            _ => null
        }).Concat(result.Events.OfType<IncomingHtlcSettled>().Select(final => (ulong?)final.HtlcId));
        var roles = incomingIds.Where(id => id.HasValue).Distinct()
                               .ToDictionary(id => id!.Value,
                                   id => _roles.GetValueOrDefault((channel.ChannelId, id!.Value), HtlcActivityRole.Unknown));
        foreach (var final in result.Events.OfType<IncomingHtlcSettled>())
            ForgetIncoming(channel.ChannelId, final.HtlcId);
        var emit = _publisher.HasSubscribers;
        if (!emit && !result.Outbound.OfType<OutboundAddHtlc>().Any()
                  && !result.Events.OfType<OutgoingHtlcSettled>().Any())
            return;

        // Secret is a value object wrapping an array: keep private copies across the asynchronous boundary.
        var snapshot = result with
        {
            Outbound = result.Outbound.Select(outbound => outbound is OutboundFulfillHtlc fulfill
                ? fulfill with { PaymentPreimage = new Secret(((byte[])fulfill.PaymentPreimage).ToArray()) }
                : outbound).ToArray(),
            Events = result.Events.Select(domainEvent => domainEvent is OutgoingHtlcFulfilled fulfilled
                ? fulfilled with { PaymentPreimage = new Secret(((byte[])fulfilled.PaymentPreimage).ToArray()) }
                : domainEvent).ToArray()
        };
        var observation = new Observation(channel.ChannelId, Scid(channel.ChannelId), snapshot,
            _timeProvider.GetUtcNow(), roles, _localFailure.Value, _publisher.CapturePublisher(), Emit: emit,
            OfferedOrigin: _offeredOrigin.Value,
            Forward: _offeredOrigin.Value is { IncomingChannelId: { } input, IncomingHtlcId: { } id }
                         ? _forwards.GetValueOrDefault((input, id)) : null);
        if (!_pending.Writer.TryWrite(observation))
            InvalidateSubscribers();
    }

    /// <summary>A deterministic barrier for verification and orderly drains; never used by the channel funds path.</summary>
    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _pending.Writer.WriteAsync(new Observation(default, 0, null, default, [], null, null, barrier),
                                         cancellationToken);
        await barrier.Task.WaitAsync(cancellationToken);
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var observation in _pending.Reader.ReadAllAsync(_stop.Token))
            {
                if (observation.ForgetId is { } forgetId)
                {
                    _outgoing.Remove((observation.ChannelId, forgetId));
                    continue;
                }
                if (observation.Barrier is { } barrier)
                {
                    barrier.TrySetResult();
                    continue;
                }
                try
                {
                    foreach (var add in observation.Result!.Outbound.OfType<OutboundAddHtlc>())
                    {
                        if (observation.OfferedOrigin is { } origin
                         && (origin.Kind != HtlcOriginKind.Forwarded || observation.Forward is not null))
                            _outgoing[(observation.ChannelId, add.Htlc.Id)] = new Attribution(origin, observation.Forward);
                    }
                    if (observation.Emit)
                    {
                        using var scope = _scopes.CreateScope();
                        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                        await ObserveAsync(observation, unitOfWork);
                    }
                }
                catch (Exception exception)
                {
                    InvalidateSubscribers();
                    _logger.LogWarning(exception, "Could not publish HTLC observations for channel {ChannelId}",
                                       observation.ChannelId);
                }
                finally
                {
                    foreach (var final in observation.Result!.Events.OfType<OutgoingHtlcSettled>())
                        _outgoing.Remove((observation.ChannelId, final.HtlcId));
                    if (_outgoing.Count > 8192)
                    {
                        InvalidateSubscribers();
                        _outgoing.Clear();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            while (_pending.Reader.TryRead(out var pending))
                pending.Barrier?.TrySetCanceled();
            _outgoing.Clear();
        }
    }

    private async Task ObserveAsync(Observation observation, IUnitOfWork unitOfWork)
    {
        var result = observation.Result!;
        foreach (var outbound in result.Outbound)
        {
            switch (outbound)
            {
                case OutboundAddHtlc add:
                    await ObserveOutgoingAsync(observation, add.Htlc.Id, HtlcActivityKind.Forward, unitOfWork,
                                               add.Htlc);
                    break;
                case OutboundFulfillHtlc fulfill:
                    await ObserveIncomingAsync(observation, fulfill.Id, HtlcActivityKind.Settle, unitOfWork,
                                               preimage: (byte[])fulfill.PaymentPreimage);
                    break;
                case OutboundFailHtlc fail:
                    await ObserveIncomingAsync(observation, fail.Id, HtlcActivityKind.LinkFail, unitOfWork,
                                               wireFailure: observation.WireFailure);
                    break;
                case OutboundFailMalformedHtlc malformed:
                    await ObserveIncomingAsync(observation, malformed.Id, HtlcActivityKind.LinkFail, unitOfWork,
                                               wireFailure: malformed.FailureCode);
                    break;
            }
        }
        foreach (var domainEvent in result.Events)
        {
            switch (domainEvent)
            {
                case OutgoingHtlcFulfilled fulfilled:
                    await ObserveOutgoingAsync(observation, fulfilled.HtlcId, HtlcActivityKind.Settle, unitOfWork,
                                               preimage: (byte[])fulfilled.PaymentPreimage);
                    break;
                case OutgoingHtlcFailed failed:
                    await ObserveOutgoingAsync(observation, failed.HtlcId, HtlcActivityKind.ForwardFail, unitOfWork);
                    break;
                case IncomingHtlcSettled final:
                    await ObserveIncomingAsync(observation, final.HtlcId, HtlcActivityKind.Final, unitOfWork,
                                               settled: final.Kind == HtlcRemovalKind.Fulfill);
                    break;
            }
        }
    }

    private async Task ObserveOutgoingAsync(Observation observation, ulong htlcId, HtlcActivityKind kind,
                                            IUnitOfWork unitOfWork, HtlcRecord? record = null,
                                            ReadOnlyMemory<byte> preimage = default)
    {
        var key = (observation.ChannelId, htlcId);
        if (!_outgoing.TryGetValue(key, out var attribution))
        {
            var origin = await unitOfWork.ChannelStateDbRepository.GetHtlcOriginAsync(
                observation.ChannelId, new HtlcKey(HtlcDirection.Outgoing, htlcId)) ?? throw new InvalidOperationException("HTLC origin was pruned before observation; reconcile current state");
            ForwardCircuitModel? circuit = null;
            if (origin is { Kind: HtlcOriginKind.Forwarded, IncomingChannelId: { } incoming, IncomingHtlcId: { } id })
            {
                circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(incoming, id) ?? throw new InvalidOperationException("HTLC forward circuit unavailable before observation");
            }
            var forward = circuit is not null
                ? new ForwardInfo(circuit.IncomingAmount.MilliSatoshi, circuit.IncomingCltvExpiry,
                                  circuit.OutgoingAmount.MilliSatoshi, circuit.OutgoingCltvExpiry,
                                  Scid(circuit.IncomingChannelId)) : null;
            attribution = new Attribution(origin, forward);
            _outgoing[key] = attribution;
        }
        var role = attribution.Origin.Kind switch
        {
            HtlcOriginKind.Local => HtlcActivityRole.Send,
            HtlcOriginKind.Forwarded => HtlcActivityRole.Forward,
            _ => HtlcActivityRole.Unknown // Trampoline fan-in has no single incoming circuit key.
        };
        record ??= observation.Result!.Next.GetHtlc(HtlcDirection.Outgoing, htlcId)
                ?? observation.Result.Transition.SettledHtlcs.FirstOrDefault(h => h.Direction == HtlcDirection.Outgoing
                                                                             && h.Id == htlcId);
        var circuitInfo = attribution.Forward;
        observation.Publish!(new HtlcActivityEvent(kind, role,
            attribution.Forward?.IncomingScid ?? 0, attribution.Origin.IncomingHtlcId ?? 0,
            observation.Scid, htlcId, observation.OccurredAt,
            circuitInfo?.IncomingAmountMsat ?? 0, circuitInfo?.IncomingTimelock ?? 0,
            record?.AmountMsat ?? circuitInfo?.OutgoingAmountMsat ?? 0,
            record?.CltvExpiry ?? circuitInfo?.OutgoingTimelock ?? 0, preimage));
    }

    private async Task ObserveIncomingAsync(Observation observation, ulong htlcId, HtlcActivityKind kind,
                                            IUnitOfWork unitOfWork, ReadOnlyMemory<byte> preimage = default,
                                            ushort? wireFailure = null, bool settled = false)
    {
        var circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(observation.ChannelId, htlcId);
        if (kind != HtlcActivityKind.Final && circuit?.OutgoingHtlcId is not null)
            return; // The paired outgoing outcome already describes this forward's wrap/propagation upstream.
        var result = observation.Result!;
        var record = result.Next.GetHtlc(HtlcDirection.Incoming, htlcId)
                  ?? result.Transition.SettledHtlcs.FirstOrDefault(h => h.Direction == HtlcDirection.Incoming
                                                                   && h.Id == htlcId);
        var role = circuit is not null ? HtlcActivityRole.Forward
                 : observation.Roles.GetValueOrDefault(htlcId, HtlcActivityRole.Unknown);
        observation.Publish!(new HtlcActivityEvent(kind, role, observation.Scid, htlcId,
            circuit is not null ? ToUlong(circuit.OutgoingShortChannelId) : 0, circuit?.OutgoingHtlcId ?? 0,
            observation.OccurredAt, record?.AmountMsat ?? 0, record?.CltvExpiry ?? 0,
            circuit?.OutgoingAmount.MilliSatoshi ?? 0, circuit?.OutgoingCltvExpiry ?? 0,
            preimage, wireFailure, wireFailure is { } code ? ((Domain.Protocol.Onion.Enums.FailureCode)code).ToString()
                                                         : "opaque failure", settled));
    }

    private ulong Scid(ChannelId channelId)
    {
        if (!_channels.TryGetChannel(channelId, out var channel))
            return 0;
        var scid = ToUlong(channel.ShortChannelId);
        return scid != 0 ? scid : ToUlong(channel.LocalAliases?.FirstOrDefault() ?? default);
    }

    private static ulong ToUlong(ShortChannelId scid) =>
        ((byte[])scid) is { Length: 8 } bytes ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : 0;

    private void InvalidateSubscribers()
    {
        try
        {
            _publisher.InvalidateSubscriptions();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "HTLC subscription invalidation callback failed");
        }
    }

    public Task Completion => _worker;

    public void Dispose()
    {
        _pending.Writer.TryComplete();
        _stop.Cancel();
        _roles.Clear();
        _forwards.Clear();
        // The worker owns its UOW; an in-flight provider read finishes/disposes there, never on a channel thread.
    }

    private sealed record Attribution(HtlcOrigin Origin, ForwardInfo? Forward);
    private sealed record ForwardInfo(ulong IncomingAmountMsat, uint IncomingTimelock,
                                      ulong OutgoingAmountMsat, uint OutgoingTimelock, ulong IncomingScid);
    private sealed record Observation(ChannelId ChannelId, ulong Scid, CommitmentsResult? Result,
                                       DateTimeOffset OccurredAt, Dictionary<ulong, HtlcActivityRole> Roles,
                                       ushort? WireFailure, Action<HtlcActivityEvent>? Publish,
                                       TaskCompletionSource? Barrier = null, bool Emit = false,
                                       HtlcOrigin? OfferedOrigin = null, ForwardInfo? Forward = null, ulong? ForgetId = null);
}
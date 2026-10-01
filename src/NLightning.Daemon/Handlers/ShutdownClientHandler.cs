using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Application.Node.Services;
using Domain.Channels.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Interfaces;
using Interfaces;
using Services;
using Services.Ipc;

/// <summary>
/// Stops the node gracefully (ClientCommand 39, NL-591/NL-592).
/// </summary>
/// <remarks>
/// <para>The drain starts first (<see cref="INodeDrainState.TryBeginDrain"/>: from then on no new HTLC, channel,
/// splice or operator activity starts). Then, by mode:</para>
/// <list type="bullet">
///   <item><b>Plain</b> (the first pass, NL-591; an older client's request): the HTLCs in flight are counted and any
///   count refuses with <see cref="ErrorCodes.InvalidOperation"/> (the drain ends, the node goes on).</item>
///   <item><b><c>--wait</c></b>: <see cref="ShutdownDrainWaiter"/> waits until nothing is in flight (HTLCs or
///   negotiations) for the settle period, then the host stops once the answer is written. On timeout the drain ends
///   and the response is <see cref="ShutdownOutcome.TimedOut"/> with what is still busy; the client's Ctrl-C (its
///   connection dropping) ends the wait like a cancellation. <c>--force</c> with <c>--wait</c> stops on the timeout
///   instead.</item>
///   <item><b><c>--force</c></b> (plain): stops although something is in flight. Never closes or broadcasts anything
///   - the HTLC expiry monitor and the BOLT 5 resolvers run at the next start - but logs a warning and reports the
///   nearest <c>cltv_expiry</c> and the blocks until our deadline for it.</item>
/// </list>
/// <para>A second <c>shutdown</c> while one runs is refused.</para>
/// </remarks>
internal sealed class ShutdownClientHandler : IClientCommandHandler<ShutdownClientRequest, ShutdownClientResponse>
{
    private readonly IpcClientConnectionAccessor? _clientConnection;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<ShutdownClientHandler> _logger;
    private readonly INodeBusyStateMonitor _busyStateMonitor;
    private readonly INodeDrainState _nodeDrainState;
    private readonly ShutdownDrainWaiter _waiter;
    private readonly NodeShutdownTrigger _shutdownTrigger;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.Shutdown;

    public ShutdownClientHandler(IChannelMemoryRepository channelMemoryRepository,
                                 ILogger<ShutdownClientHandler> logger, INodeBusyStateMonitor busyStateMonitor,
                                 INodeDrainState nodeDrainState, ShutdownDrainWaiter waiter,
                                 NodeShutdownTrigger shutdownTrigger,
                                 IpcClientConnectionAccessor? clientConnection = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _busyStateMonitor = busyStateMonitor;
        _nodeDrainState = nodeDrainState;
        _waiter = waiter;
        _shutdownTrigger = shutdownTrigger;
        _clientConnection = clientConnection;
    }

    /// <inheritdoc/>
    public async Task<ShutdownClientResponse> HandleAsync(ShutdownClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_nodeDrainState.TryBeginDrain())
            throw new ClientException(ErrorCodes.InvalidOperation, "The node is already shutting down.");

        try
        {
            return request.Wait ? await WaitAsync(request, ct) : StopNow(request);
        }
        catch
        {
            // Every exit but an accepted shutdown opens the gate again: the node goes on as before
            _nodeDrainState.EndDrain();
            throw;
        }
    }

    /// <summary>The plain first-pass shutdown, plus plain <c>--force</c>.</summary>
    private ShutdownClientResponse StopNow(ShutdownClientRequest request)
    {
        var summary = PeerChannelSummary.ForAll(_channelMemoryRepository);
        if (summary.HtlcsInFlight > 0 && !request.Force)
        {
            // NL-595: name the channels that carry the HTLCs (the node's total channel count is noise) and list them
            var busyList = string.Join(", ", _channelMemoryRepository
                                                 .FindChannels(c => PeerChannelSummary.CountHtlcsInFlight(c) > 0)
                                                 .Select(c => $"{c.ChannelId} "
                                                            + $"({PeerChannelSummary.CountHtlcsInFlight(c)})"));
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"{summary.HtlcsInFlight} HTLC(s) are in flight on {summary.ChannelsWithHtlcs} "
                                    + $"channel(s): {busyList}; not shutting down. Use --wait to drain, or --force to "
                                    + "stop anyway.");
        }

        if (!request.Force)
        {
            _logger.LogInformation("Shutdown requested over IPC: no HTLC in flight on {Channels} channel(s); refusing "
                                 + "new activity and stopping the node", summary.ChannelCount);
            _shutdownTrigger.RequestStop();
            return new ShutdownClientResponse(summary.ChannelCount);
        }

        var busy = _busyStateMonitor.Snapshot();
        LogForced(busy);
        _shutdownTrigger.RequestStop();
        return Forced(summary.ChannelCount, busy);
    }

    /// <summary>The <c>--wait</c> mode: drain to idle, force on the timeout when asked.</summary>
    private async Task<ShutdownClientResponse> WaitAsync(ShutdownClientRequest request, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(request.TimeoutSeconds is 0
                                               ? ShutdownDefaults.DefaultWaitTimeoutSeconds
                                               : request.TimeoutSeconds);
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var connection = _clientConnection?.Current;
        var disconnected = connection?.Disconnected ?? default;
        var waitToken = disconnected.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(waitCts.Token, disconnected).Token
            : waitCts.Token;

        var result = await _waiter.WaitUntilIdleAsync(timeout, waitToken);
        if (!result.Drained)
        {
            // The gate opens again: the node goes on (unless --force stops it below)
            _nodeDrainState.EndDrain();
        }

        if (result.Drained)
        {
            _logger.LogInformation("Shutdown accepted over IPC: the node is drained; stopping");
            _shutdownTrigger.RequestStop();
            return new ShutdownClientResponse(result.Snapshot.ChannelCount) { Outcome = ShutdownOutcome.Stopped };
        }

        if (request.Force)
        {
            LogForced(result.Snapshot);
            _shutdownTrigger.RequestStop();
            return Forced(result.Snapshot.ChannelCount, result.Snapshot);
        }

        return new ShutdownClientResponse(result.Snapshot.ChannelCount)
        {
            Outcome = ShutdownOutcome.TimedOut,
            HtlcsInFlight = result.Snapshot.HtlcsInFlight,
            NegotiationCount = result.Snapshot.NegotiationCount,
            BusyChannels = result.Snapshot.Channels.Select(ToBusyChannel).ToList(),
            NearestCltvExpiry = result.Snapshot.NearestCltvExpiry,
            BlocksUntilDeadline = result.Snapshot.BlocksUntilDeadline
        };
    }

    private static ShutdownClientResponse Forced(int channelCount, NodeBusyState busy) =>
        new(channelCount)
        {
            Outcome = ShutdownOutcome.Forced,
            HtlcsInFlight = busy.HtlcsInFlight,
            NegotiationCount = busy.NegotiationCount,
            BusyChannels = busy.Channels.Select(ToBusyChannel).ToList(),
            NearestCltvExpiry = busy.NearestCltvExpiry,
            BlocksUntilDeadline = busy.BlocksUntilDeadline
        };

    private void LogForced(NodeBusyState busy)
    {
        _logger.LogWarning("Shutdown FORCED over IPC with {Htlcs} HTLC(s) in flight and {Negotiations} negotiation(s) "
                         + "mid-flight: nothing is broadcast or force-closed, but the node MUST be back before the "
                         + "HTLC deadlines. Nearest cltv_expiry {Expiry}, our deadline in {Blocks} block(s)",
                           busy.HtlcsInFlight, busy.NegotiationCount, busy.NearestCltvExpiry,
                           busy.BlocksUntilDeadline);
    }

    private static ShutdownBusyChannel ToBusyChannel(NodeBusyChannel channel) =>
        new(channel.ChannelId, channel.HtlcsInFlight, channel.Negotiating);
}
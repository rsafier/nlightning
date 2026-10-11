using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Channels.KeyIndexes;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Interfaces;

/// <summary>
/// Shows, and with <c>--release</c> lifts, the hold on channel opens (ClientCommand 59, NL-1392): the start hold of
/// <c>Node:Backup:HoldOpensUntilRestore</c> (a node set up on a fresh database refuses opens until its channels are
/// restored) and a <c>restorechanbackup</c> in progress, during which no new channel key index is handed out.
/// </summary>
/// <remarks>Releasing lifts only the start hold: a running restore keeps the gate held until it ends.</remarks>
public sealed class OpenHoldClientHandler : IClientCommandHandler<OpenHoldClientRequest, OpenHoldClientResponse>
{
    private readonly IChannelKeyIndexGate _gate;
    private readonly ILogger<OpenHoldClientHandler> _logger;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.OpenHold;

    public OpenHoldClientHandler(IChannelKeyIndexGate gate, ILogger<OpenHoldClientHandler> logger)
    {
        _gate = gate;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<OpenHoldClientResponse> HandleAsync(OpenHoldClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var released = request.Release && _gate.ReleaseRestoreHold();
        if (released)
            _logger.LogWarning("The operator lifted the hold on channel opens (Node:Backup:HoldOpensUntilRestore) "
                             + "without a channel restore");

        var reason = _gate.HoldReason;
        return Task.FromResult(new OpenHoldClientResponse(reason is not null, reason, _gate.IsAwaitingRestore,
                                                          _gate.IsRestoreInProgress, released));
    }
}
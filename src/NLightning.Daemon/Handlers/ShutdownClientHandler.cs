using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Channels.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Interfaces;
using Interfaces;
using Services;

/// <summary>
/// Stops the node gracefully (ClientCommand 39, NL-591).
/// </summary>
/// <remarks>
/// The drain starts first (<see cref="INodeDrainState.TryBeginDrain"/>: from then on no new HTLC, channel, splice or
/// operator activity starts), then the HTLCs in flight on every channel that is not Closed or Stale are counted: an
/// HTLC added just before the drain began is counted too. With any in flight the drain ends again and the request is
/// refused with <see cref="ErrorCodes.InvalidOperation"/> (the node goes on as before); with none the host stops once
/// the answer is written (<see cref="NodeShutdownTrigger"/>), through the daemon's normal stop path. A second
/// <c>shutdown</c> while one runs is refused.
/// </remarks>
internal sealed class ShutdownClientHandler : IClientCommandHandler<ShutdownClientRequest, ShutdownClientResponse>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<ShutdownClientHandler> _logger;
    private readonly INodeDrainState _nodeDrainState;
    private readonly NodeShutdownTrigger _shutdownTrigger;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.Shutdown;

    public ShutdownClientHandler(IChannelMemoryRepository channelMemoryRepository,
                                 ILogger<ShutdownClientHandler> logger, INodeDrainState nodeDrainState,
                                 NodeShutdownTrigger shutdownTrigger)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _nodeDrainState = nodeDrainState;
        _shutdownTrigger = shutdownTrigger;
    }

    /// <inheritdoc/>
    public Task<ShutdownClientResponse> HandleAsync(ShutdownClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_nodeDrainState.TryBeginDrain())
            throw new ClientException(ErrorCodes.InvalidOperation, "The node is already shutting down.");

        // Counted after the drain began, so no new HTLC can be offered or accepted once this count is zero
        var summary = PeerChannelSummary.ForAll(_channelMemoryRepository);
        if (summary.HtlcsInFlight > 0)
        {
            _nodeDrainState.EndDrain();
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"{summary.HtlcsInFlight} HTLC(s) are in flight on {summary.ChannelCount} "
                                    + "channel(s); not shutting down. Try again once they are resolved "
                                    + "(listchannels shows them).");
        }

        _logger.LogInformation("Shutdown requested over IPC: no HTLC in flight on {Channels} channel(s); refusing new "
                             + "activity and stopping the node", summary.ChannelCount);
        _shutdownTrigger.RequestStop();

        return Task.FromResult(new ShutdownClientResponse(summary.ChannelCount));
    }
}
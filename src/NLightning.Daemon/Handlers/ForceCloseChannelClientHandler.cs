namespace NLightning.Daemon.Handlers;

using Application.Channels.Safety.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Interfaces;

/// <summary>
/// Force-closes a channel (ClientCommand 14, BOLT 5 plan O2-T5, B5-FAIL-05): fails it through
/// <see cref="IChannelFailureService"/>, which persists Failed with the broadcast of our fully signed latest commitment,
/// publishes it and sends the peer an <c>error</c>. The on-chain watcher then moves the channel to
/// <c>OnchainResolving</c> once a commitment spends the funding output; <c>pendingsweeps</c> shows the resolution.
/// </summary>
/// <remarks>
/// An unknown channel is <see cref="ErrorCodes.InvalidChannel"/>; a channel that has nothing to broadcast (not funded
/// yet, already closed or resolving on chain) is <see cref="ErrorCodes.InvalidOperation"/>. Data loss is not an error:
/// the channel is failed without a broadcast (our commitment is revoked from the peer's point of view) and the status
/// says so (<c>RefusedDataLoss</c>).
/// </remarks>
public sealed class ForceCloseChannelClientHandler
    : IClientCommandHandler<ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>
{
    /// <summary>The <c>error</c> text the peer gets.</summary>
    public const string PeerMessage = "channel force closed by the operator";

    private readonly IChannelFailureService _channelFailureService;
    private readonly IChannelMemoryRepository _channelMemoryRepository;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ForceCloseChannel;

    public ForceCloseChannelClientHandler(IChannelFailureService channelFailureService,
                                          IChannelMemoryRepository channelMemoryRepository)
    {
        _channelFailureService = channelFailureService;
        _channelMemoryRepository = channelMemoryRepository;
    }

    /// <inheritdoc/>
    public async Task<ForceCloseChannelClientResponse> HandleAsync(ForceCloseChannelClientRequest request,
                                                                   CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ChannelFailureOutcome outcome;
        try
        {
            outcome = await _channelFailureService.FailChannelAsync(
                          request.ChannelId,
                          new ChannelFailureRequest("force close requested over IPC", PeerMessage, true,
                                                    "B5-FAIL-05"), ct);
        }
        catch (KeyNotFoundException e)
        {
            throw new ClientException(ErrorCodes.InvalidChannel, $"Unknown channel {request.ChannelId}.", e);
        }

        if (outcome.Status == ChannelFailureStatus.NotApplicable)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"Channel {request.ChannelId} has no commitment to broadcast (not funded, closed "
                                    + "or already resolving on chain).");

        var state = _channelMemoryRepository.TryGetChannelState(request.ChannelId, out var current)
                        ? current
                        : Domain.Channels.Enums.ChannelState.Failed;
        return new ForceCloseChannelClientResponse(request.ChannelId, state, Enum.GetName(outcome.Status)!,
                                                   outcome.CommitmentTxId);
    }
}
namespace NLightning.Daemon.Handlers;

using Domain.Channels.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Interfaces;

/// <summary>
/// Starts the cooperative close of a channel through <see cref="IChannelCloseService"/> and waits for the closing
/// transaction (ClientCommand 13).
/// </summary>
/// <remarks>
/// The wait is bounded by <see cref="CloseChannelClientRequest.WaitSeconds"/> (default
/// <see cref="DefaultWaitSeconds"/>, at most <see cref="MaxWaitSeconds"/>; 0 returns once our <c>shutdown</c> is out).
/// The close goes on after the wait: <c>listchannels</c> shows it (ShuttingDown, Negotiating, Closing, Closed). An
/// unknown channel and a channel that can't be closed now are <see cref="ErrorCodes.InvalidChannel"/> and
/// <see cref="ErrorCodes.InvalidOperation"/>.
/// </remarks>
public sealed class CloseChannelClientHandler
    : IClientCommandHandler<CloseChannelClientRequest, CloseChannelClientResponse>
{
    /// <summary>The wait when the request does not choose one.</summary>
    public const uint DefaultWaitSeconds = 30;

    /// <summary>The longest wait a request may ask for (it holds one IPC pipe instance).</summary>
    public const uint MaxWaitSeconds = 300;

    private readonly IChannelCloseService _channelCloseService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.CloseChannel;

    public CloseChannelClientHandler(IChannelCloseService channelCloseService)
    {
        _channelCloseService = channelCloseService;
    }

    /// <inheritdoc/>
    public async Task<CloseChannelClientResponse> HandleAsync(CloseChannelClientRequest request,
                                                              CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var waitSeconds = request.WaitSeconds ?? DefaultWaitSeconds;
        if (waitSeconds > MaxWaitSeconds)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The wait must be between 0 and {MaxWaitSeconds} seconds.");
        if (request.FeeRatePerKw is 0)
            throw new ClientException(ErrorCodes.InvalidOperation, "The feerate must be positive.");

        try
        {
            var result = await _channelCloseService.CloseChannelAsync(
                             request.ChannelId,
                             new ChannelCloseRequest(request.FeeRatePerKw, !request.NoFeeRange,
                                                     TimeSpan.FromSeconds(waitSeconds)), ct);
            return new CloseChannelClientResponse(result.ChannelId, result.State, result.ClosingTxId);
        }
        catch (KeyNotFoundException e)
        {
            throw new ClientException(ErrorCodes.InvalidChannel, $"Unknown channel {request.ChannelId}.", e);
        }
        catch (InvalidOperationException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
        }
    }
}
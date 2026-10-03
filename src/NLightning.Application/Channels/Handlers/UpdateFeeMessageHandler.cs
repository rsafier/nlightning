using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Interfaces;
using Services;

/// <summary>
/// Receives <c>update_fee</c> (BOLT 2, plan N6-T1, D9): only the funder may send it (B2-FEE-R02), the feerate must lie
/// in [<see cref="ChannelStateTransitionService.MinAcceptableFeeratePerKw"/>,
/// <see cref="ChannelStateTransitionService.MaxAcceptableFeeratePerKw"/>] (B2-FEE-R01) and the funder must afford it
/// on our commitment (B2-FEE-R03). The update is persisted; the peer's <c>commitment_signed</c> commits it.
/// </summary>
public class UpdateFeeMessageHandler : IChannelMessageHandler<UpdateFeeMessage>
{
    private readonly ILogger<UpdateFeeMessageHandler> _logger;
    private readonly ChannelStateTransitionService _transitions;

    public UpdateFeeMessageHandler(ILogger<UpdateFeeMessageHandler> logger, ChannelStateTransitionService transitions)
    {
        _logger = logger;
        _transitions = transitions;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(UpdateFeeMessage message, ChannelState currentState,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        var channel = _transitions.GetUpdatableChannel(payload.ChannelId, currentState, "update_fee");

        CommitmentsResult result;
        try
        {
            result = channel.Commitments!.ReceiveFee(payload.FeeratePerKw,
                                                     ChannelStateTransitionService.MinAcceptableFeeratePerKw,
                                                     ChannelStateTransitionService.MaxAcceptableFeeratePerKw);
        }
        catch (CommitmentViolationException e)
        {
            throw ChannelStateTransitionService.ToPeerException(e, payload.ChannelId);
        }

        await _transitions.CommitAsync(channel, result);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Peer updated the feerate of channel {ChannelId} to {FeeratePerKw} sat/kw",
                             payload.ChannelId, payload.FeeratePerKw);

        return [];
    }
}
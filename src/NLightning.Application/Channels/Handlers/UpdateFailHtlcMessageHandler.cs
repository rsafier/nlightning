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
/// Receives <c>update_fail_htlc</c> for an HTLC we offered (BOLT 2, plan N6-T1). The failure is persisted with its
/// <c>attribution_data</c> TLV when present (BOLT 4, NL-326), so the switch can wrap it upstream and the origin verify
/// it even after a restart; the <c>OutgoingHtlcFailed</c> event is raised only once the removal is irrevocably
/// committed (B2-FWD-02).
/// </summary>
public class UpdateFailHtlcMessageHandler : IChannelMessageHandler<UpdateFailHtlcMessage>
{
    private readonly ILogger<UpdateFailHtlcMessageHandler> _logger;
    private readonly ChannelStateTransitionService _transitions;

    public UpdateFailHtlcMessageHandler(ILogger<UpdateFailHtlcMessageHandler> logger,
                                        ChannelStateTransitionService transitions)
    {
        _logger = logger;
        _transitions = transitions;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(UpdateFailHtlcMessage message,
                                                                  ChannelState currentState,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        var channel = _transitions.GetUpdatableChannel(payload.ChannelId, currentState, "update_fail_htlc");

        CommitmentsResult result;
        try
        {
            result = channel.Commitments!.ReceiveFail(payload.Id, payload.Reason.ToArray(),
                                                      message.AttributionDataTlv?.AttributionData ?? []);
        }
        catch (CommitmentViolationException e)
        {
            throw ChannelStateTransitionService.ToPeerException(e, payload.ChannelId);
        }

        await _transitions.CommitAsync(channel, result);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("HTLC {HtlcId} on channel {ChannelId} was failed by the peer", payload.Id,
                             payload.ChannelId);

        return [];
    }
}
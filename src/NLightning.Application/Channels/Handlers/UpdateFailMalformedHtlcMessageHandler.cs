using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Interfaces;
using Services;

/// <summary>
/// Receives <c>update_fail_malformed_htlc</c> for an HTLC we offered (BOLT 2, plan N6-T1). A <c>failure_code</c>
/// without the BADONION bit gets a <c>warning</c> and the connection is closed (B2-DEL-R04, NL-023; BOLT 2 allows that
/// instead of failing the channel) before any other check.
/// </summary>
public class UpdateFailMalformedHtlcMessageHandler : IChannelMessageHandler<UpdateFailMalformedHtlcMessage>
{
    private readonly ILogger<UpdateFailMalformedHtlcMessageHandler> _logger;
    private readonly ChannelStateTransitionService _transitions;

    public UpdateFailMalformedHtlcMessageHandler(ILogger<UpdateFailMalformedHtlcMessageHandler> logger,
                                                 ChannelStateTransitionService transitions)
    {
        _logger = logger;
        _transitions = transitions;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(UpdateFailMalformedHtlcMessage message,
                                                                  ChannelState currentState,
                                                                  FeatureOptions negotiatedFeatures,
                                                                  CompactPubKey peerPubKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.Payload;
        if ((payload.FailureCode & (ushort)FailureCodeFlags.BadOnion) == 0)
            throw new ChannelWarningException(
                $"update_fail_malformed_htlc failure_code 0x{payload.FailureCode:x4} has no BADONION bit",
                payload.ChannelId, "update_fail_malformed_htlc failure_code must have the BADONION bit set")
            {
                CloseConnection = true
            };

        var channel = _transitions.GetUpdatableChannel(payload.ChannelId, currentState,
                                                       "update_fail_malformed_htlc");

        CommitmentsResult result;
        try
        {
            result = channel.Commitments!.ReceiveFailMalformed(payload.Id, payload.FailureCode,
                                                               payload.Sha256OfOnion.ToArray());
        }
        catch (CommitmentViolationException e)
        {
            throw ChannelStateTransitionService.ToPeerException(e, payload.ChannelId);
        }

        await _transitions.CommitAsync(channel, result);

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("HTLC {HtlcId} on channel {ChannelId} was failed as malformed (0x{FailureCode:x4})",
                             payload.Id, payload.ChannelId, payload.FailureCode);

        return [];
    }
}
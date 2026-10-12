using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Quiescence;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Interfaces;

/// <summary>
/// Handles the peer's <c>stfu</c> (BOLT 2 "Channel Quiescence", type 2; splicing plan Q1-T1, NL-019). Thin: it runs
/// under the channel's lock (<c>ChannelManager</c>) and delegates to <see cref="IQuiescenceService.OnStfuReceived"/>,
/// which owns the quiescence state and the rules (<see cref="QuiescenceRules"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A channel that is not loaded (known to the database only: closed, stale): a <c>warning</c> for the channel,
/// connection kept (nothing to quiesce).</item>
/// <item>A channel of another peer: <c>error</c> for the channel, as the other handlers do.</item>
/// <item>No <see cref="IQuiescenceService"/> registered: we can never reply with our own <c>stfu</c>, and the sender
/// now considers the channel quiescing (only a disconnection ends that), so a <c>warning</c> and the connection closed;
/// the channel is not failed (the pre-quiescence behavior of NL-019).</item>
/// <item>Otherwise the service's reply <c>stfu</c> (<c>initiator</c> = 0) is returned when it can go now; a second
/// <c>stfu</c>, one without <c>option_quiesce</c> negotiated or an unsolicited reply comes back from the service as a
/// <see cref="ChannelWarningException"/> with <c>CloseConnection</c> (Q-S-01, Q-S-03).</item>
/// </list>
/// </remarks>
public class StfuMessageHandler : IChannelMessageHandler<StfuMessage>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<StfuMessageHandler> _logger;
    private readonly IQuiescenceService? _quiescenceService;

    public StfuMessageHandler(IChannelMemoryRepository channelMemoryRepository, ILogger<StfuMessageHandler> logger,
                              IQuiescenceService? quiescenceService = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _quiescenceService = quiescenceService;
    }

    public Task<IReadOnlyList<IChannelMessage>> HandleAsync(StfuMessage message, ChannelState currentState,
                                                            FeatureOptions negotiatedFeatures,
                                                            CompactPubKey peerPubKey)
    {
        var channelId = message.Payload.ChannelId;
        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            throw new ChannelWarningException($"stfu for channel {channelId}, which is not loaded", channelId,
                                              "stfu for a channel that is not active, message ignored");

        if (channel.RemoteNodeId != peerPubKey)
            throw new ChannelErrorException($"stfu for channel {channelId} from {peerPubKey}, not its peer", channelId,
                                            "unknown channel");

        if (_quiescenceService is null)
        {
            _logger.LogWarning("Received stfu for channel {ChannelId} from peer {Peer}, but quiescence is not enabled",
                               channelId, peerPubKey);
            throw new ChannelWarningException("Received stfu, but quiescence is not supported", channelId,
                                              "Quiescence (stfu) is not supported")
            {
                CloseConnection = true
            };
        }

        var reply = _quiescenceService.OnStfuReceived(channel, message.Payload, negotiatedFeatures.GetNodeFeatures());
        return Task.FromResult<IReadOnlyList<IChannelMessage>>(reply is null ? [] : [reply]);
    }
}
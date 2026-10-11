using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Gossip.Announcements.Interfaces;
using Interfaces;

/// <summary>
/// <c>announcement_signatures_2</c> (taproot gossip, BOLTs PR #1059, NL-878): the peer's two MuSig2 partial
/// signatures of a public simple taproot channel's <c>channel_announcement_2</c>, handled under the channel's lock by
/// <see cref="IChannelAnnouncement2Service"/> (verified, stored, answered with ours, assembled).
/// </summary>
public class AnnouncementSignatures2MessageHandler : IChannelMessageHandler<AnnouncementSignatures2Message>
{
    private readonly IChannelAnnouncement2Service? _announcementService;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<AnnouncementSignatures2MessageHandler> _logger;

    public AnnouncementSignatures2MessageHandler(IChannelMemoryRepository channelMemoryRepository,
                                                 ILogger<AnnouncementSignatures2MessageHandler> logger,
                                                 IChannelAnnouncement2Service? announcementService = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _announcementService = announcementService;
    }

    public Task<IReadOnlyList<IChannelMessage>> HandleAsync(AnnouncementSignatures2Message message,
                                                           ChannelState currentState,
                                                           FeatureOptions negotiatedFeatures,
                                                           CompactPubKey peerPubKey)
    {
        var payload = message.Payload;
        var channelId = payload.ChannelId;

        // BOLTs #1059: only between peers that negotiated option_gossip_v2
        if (_announcementService is null || negotiatedFeatures.OptionGossipV2 == FeatureSupport.No)
            throw new ChannelWarningException($"announcement_signatures_2 for channel {channelId} without gossip v2",
                                              channelId, "announcement_signatures_2: option_gossip_v2 not negotiated");

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
        {
            _logger.LogDebug("Ignoring announcement_signatures_2 for channel {ChannelId}: it is not loaded", channelId);
            return Task.FromResult<IReadOnlyList<IChannelMessage>>([]);
        }

        if (channel.RemoteNodeId != peerPubKey)
            throw new ChannelErrorException(
                $"announcement_signatures_2 for channel {channelId} from {peerPubKey}, not its peer", channelId,
                "unknown channel");

        if (channel.State != ChannelState.Open || channel.LocalShutdownScript is not null
                                               || channel.RemoteShutdownScript is not null)
        {
            _logger.LogDebug("Ignoring announcement_signatures_2 for channel {ChannelId} in state {State}", channelId,
                             Enum.GetName(channel.State));
            return Task.FromResult<IReadOnlyList<IChannelMessage>>([]);
        }

        return Task.FromResult(_announcementService.OnAnnouncementSignatures2(channel, peerPubKey, payload));
    }
}
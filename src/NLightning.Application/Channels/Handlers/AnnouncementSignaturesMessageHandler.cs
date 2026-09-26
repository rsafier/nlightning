using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Gossip.Announcements;
using Gossip.Announcements.Interfaces;
using Interfaces;

/// <summary>
/// Handles the peer's <c>announcement_signatures</c> (BOLT 7, type 259; plan G1-T3, NL-342): its half of our public
/// channel's <c>channel_announcement</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A channel that is shutting down, closing or closed: ignored (nothing is announced after a <c>shutdown</c>).</item>
/// <item>A private channel: a <c>warning</c> (BOLT 7: MUST NOT be sent without <c>announce_channel</c>).</item>
/// <item>A <c>short_channel_id</c> other than the channel's funding output: a <c>warning</c> (BOLT 7 SHOULD).</item>
/// <item>A bad node or bitcoin signature: a <c>warning</c> and the connection closed (BOLT 7 MAY; plan D10: the channel
/// is never failed over announcement data).</item>
/// <item>Valid: the signatures are stored (persisted before anything is sent). Before we sent <c>channel_ready</c>
/// they are only stored (BOLT 7: defer); the announcement is assembled later, and only when they still verify for the
/// channel's real short channel id.</item>
/// <item>Then, when our own may go out (<see cref="IChannelAnnouncementService.CanSendAnnouncementSignatures"/>) and
/// did not go out on this connection yet, ours is the reply (BOLT 7: "MUST respond with its own"), marked sent in the
/// same save.</item>
/// <item>With both halves and the announcement depth, the <c>channel_announcement</c> is assembled and handed on
/// (<see cref="IChannelAnnouncementService.OnChannelAnnounced"/>, BOLT 7 SHOULD queue it).</item>
/// </list>
/// A repeat of the stored signatures saves nothing (only our reply, if one is due, is persisted and sent).
/// </remarks>
public class AnnouncementSignaturesMessageHandler : IChannelMessageHandler<AnnouncementSignaturesMessage>
{
    private readonly IChannelAnnouncementService _announcementService;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<AnnouncementSignaturesMessageHandler> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IUnitOfWork _unitOfWork;

    public AnnouncementSignaturesMessageHandler(IChannelAnnouncementService announcementService,
                                                IChannelMemoryRepository channelMemoryRepository,
                                                ILogger<AnnouncementSignaturesMessageHandler> logger,
                                                IUnitOfWork unitOfWork, TimeProvider? timeProvider = null)
    {
        _announcementService = announcementService;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(AnnouncementSignaturesMessage message,
                                                                 ChannelState currentState,
                                                                 FeatureOptions negotiatedFeatures,
                                                                 CompactPubKey peerPubKey)
    {
        var payload = message.Payload;
        var channelId = payload.ChannelId;

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
        {
            // Known to the database only (closed, stale): nothing to announce
            _logger.LogDebug("Ignoring announcement_signatures for channel {ChannelId}: it is not loaded", channelId);
            return [];
        }

        if (channel.RemoteNodeId != peerPubKey)
            throw new ChannelErrorException(
                $"announcement_signatures for channel {channelId} from {peerPubKey}, not its peer", channelId,
                "unknown channel");

        if (channel.State is not (ChannelState.V1FundingSigned or ChannelState.ReadyForThem
                               or ChannelState.ReadyForUs or ChannelState.Open)
         || channel.LocalShutdownScript is not null || channel.RemoteShutdownScript is not null)
        {
            _logger.LogDebug("Ignoring announcement_signatures for channel {ChannelId} in state {State}", channelId,
                             Enum.GetName(channel.State));
            return [];
        }

        if (!channel.AnnounceChannel)
            throw new ChannelWarningException(
                $"announcement_signatures for private channel {channelId}", channelId,
                "announcement_signatures for a channel opened without announce_channel");

        CheckShortChannelId(channel, payload.ShortChannelId);

        var signatures = new ChannelAnnouncementSignatures(payload.NodeSignature, payload.BitcoinSignature);
        if (!_announcementService.VerifyRemoteSignatures(channel, payload.ShortChannelId, signatures))
            throw new ChannelWarningException(
                $"Invalid announcement_signatures for channel {channelId} ({payload.ShortChannelId})", channelId,
                "announcement_signatures: invalid node_signature or bitcoin_signature")
            {
                CloseConnection = true
            };

        var isNew = channel.RemoteAnnouncementSignatures != signatures;
        if (isNew)
            channel.SetRemoteAnnouncementSignatures(signatures);

        // BOLT 7: defer until we sent channel_ready (ReadyForUs or Open); the stored half is checked again then
        AnnouncementSignaturesMessage? reply = null;
        if (_announcementService.CanSendAnnouncementSignatures(channel)
         && !_announcementService.WasSentOnConnection(channelId))
        {
            reply = _announcementService.CreateAnnouncementSignatures(channel);
            channel.MarkAnnouncementSignaturesSent(_timeProvider.GetUtcNow());
        }

        if (isNew || reply is not null)
            await PersistAsync(channel);

        if (reply is not null)
            _announcementService.MarkSentOnConnection(channelId, peerPubKey);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Stored the announcement_signatures of channel {ChannelId} ({ShortChannelId}){Reply}", channelId,
                payload.ShortChannelId, reply is null ? string.Empty : "; replying with ours");

        if (_announcementService.TryAssembleAnnouncement(channel) is { } announcement)
            _announcementService.OnChannelAnnounced(channel, announcement);

        return reply is null ? [] : [reply];
    }

    /// <summary>
    /// BOLT 7: SHOULD send a <c>warning</c> when the <c>short_channel_id</c> does not match the funding transaction.
    /// Before our funding confirmation only the output index can be checked.
    /// </summary>
    private static void CheckShortChannelId(ChannelModel channel, ShortChannelId shortChannelId)
    {
        var matches = ChannelAnnouncementService.HasShortChannelId(channel)
                          ? channel.ShortChannelId == shortChannelId
                          : channel.FundingOutput?.Index is { } index && shortChannelId.OutputIndex == index;
        if (!matches)
            throw new ChannelWarningException(
                $"announcement_signatures for channel {channel.ChannelId} names {shortChannelId}, not its funding "
              + "output", channel.ChannelId, "announcement_signatures: short_channel_id does not match the funding output");
    }

    private async Task PersistAsync(ChannelModel channel)
    {
        await _unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await _unitOfWork.SaveChangesAsync();
        _channelMemoryRepository.UpdateChannel(channel);
    }
}
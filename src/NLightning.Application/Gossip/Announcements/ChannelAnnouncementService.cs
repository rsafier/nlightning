using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Announcements;

using Channels.Splicing.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Gossip.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <inheritdoc cref="IChannelAnnouncementService"/>
/// <remarks>
/// <para>A singleton. The per-connection record of what was sent lives in memory only: a restart or a new connection
/// sends again, as BOLT 7 asks.</para>
/// <para>Splices (splicing plan §3.7, SP2-B-T3, SP-G-01): the announcement always names the channel's current funding
/// (its short channel id and funding keys, NL-478), so a splice locked both ways is announced by the same path as the
/// original funding once its block has the announcement depth; the lock forgets the halves of the replaced funding
/// (<see cref="OnShortChannelIdChanged"/>, <see cref="ChannelModel.ResetAnnouncementSignatures"/>). A peer's half for a
/// pending splice we have not sent <c>splice_locked</c> for is deferred in memory
/// (<see cref="DeferRemoteAnnouncementSignatures"/>) and taken after the lock
/// (<see cref="ProcessDeferredRemoteAnnouncementSignaturesAsync"/>). The pending fundings and their <c>splice_locked</c>
/// flags come from the optional <see cref="ISpliceStatePort"/> (the engine's fundings without it).</para>
/// </remarks>
public sealed class ChannelAnnouncementService : IChannelAnnouncementService
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IGossipSignatureVerifier _signatureVerifier;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ChannelAnnouncementService> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly OwnGossipPublisher _publisher;
    private readonly IChannelUpdateService? _channelUpdateService;
    private readonly INodeAnnouncementService? _nodeAnnouncementService;
    private readonly GossipOptions _gossipOptions;
    private readonly NodeOptions _nodeOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ISpliceStatePort? _spliceStatePort;
    private readonly IRetiredScidMap? _retiredScidMap;

    // Channel id -> the peer's announcement_signatures for a splice we have not sent splice_locked for (BOLT 7 SHOULD)
    private readonly ConcurrentDictionary<ChannelId, (ShortChannelId ShortChannelId, ChannelAnnouncementSignatures
        Signatures)> _deferred = new();

    // Channel id -> the peer whose current connection got our announcement_signatures
    private readonly ConcurrentDictionary<ChannelId, CompactPubKey> _sentOnConnection = new();

    // Channel id -> the signature hash of the announcement last handed on, so a repeat is not handed on again
    private readonly ConcurrentDictionary<ChannelId, Hash> _announced = new();

    public ChannelAnnouncementService(IBlockchainMonitor blockchainMonitor,
                                      IGossipSignatureVerifier signatureVerifier, ILightningSigner lightningSigner,
                                      ILogger<ChannelAnnouncementService> logger, IMessageFactory messageFactory,
                                      OwnGossipPublisher publisher, IOptions<NodeOptions> nodeOptions,
                                      IOptions<GossipOptions>? gossipOptions = null,
                                      IChannelUpdateService? channelUpdateService = null,
                                      INodeAnnouncementService? nodeAnnouncementService = null,
                                      TimeProvider? timeProvider = null,
                                      ISpliceStatePort? spliceStatePort = null,
                                      IRetiredScidMap? retiredScidMap = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _signatureVerifier = signatureVerifier;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _messageFactory = messageFactory;
        _publisher = publisher;
        _channelUpdateService = channelUpdateService;
        _nodeAnnouncementService = nodeAnnouncementService;
        _nodeOptions = nodeOptions.Value;
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _spliceStatePort = spliceStatePort;
        _retiredScidMap = retiredScidMap;
    }

    /// <summary>
    /// Whether the channel is announced as far as its persisted state tells: public, confirmed, both halves of
    /// <c>announcement_signatures</c> exchanged (ours sent at the announcement depth), and not closing. After a restart
    /// this holds before the announcement is assembled again, so our <c>channel_update</c> stays public.
    /// </summary>
    public static bool IsAnnounced(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.AnnounceChannel && HasShortChannelId(channel)
            && channel.RemoteAnnouncementSignatures is not null
            && channel.LocalAnnouncementSignaturesSentAt is not null
            && channel.State is ChannelState.Open or ChannelState.ShuttingDown or ChannelState.Negotiating;
    }

    /// <inheritdoc />
    public bool CanSendAnnouncementSignatures(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // BOLT 7: only with announce_channel, after channel_ready was sent and received and before any shutdown; on
        // mainnet only once public channels are allowed there (plan D12)
        return channel.AnnounceChannel
            && _gossipOptions.ArePublicChannelsAllowed(_nodeOptions.BitcoinNetwork)
            && channel.State == ChannelState.Open
            && channel.LocalShutdownScript is null && channel.RemoteShutdownScript is null
            && !channel.DataLossDetected
            && channel.RemoteKeySet is not null
            && IsAtAnnouncementDepth(channel);
    }

    /// <inheritdoc />
    public AnnouncementSignaturesMessage CreateAnnouncementSignatures(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!CanSendAnnouncementSignatures(channel))
            throw new InvalidOperationException(
                $"Channel {channel.ChannelId} can't be announced now (state, flag, shutdown or depth)");

        var signatures = SignOurHalf(channel, out _);
        return _messageFactory.CreateAnnouncementSignaturesMessage(channel.ChannelId, channel.ShortChannelId,
                                                                   signatures.NodeSignature,
                                                                   signatures.BitcoinSignature);
    }

    /// <inheritdoc />
    public bool VerifyRemoteSignatures(ChannelModel channel, ShortChannelId shortChannelId,
                                       ChannelAnnouncementSignatures signatures)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(signatures);
        if (channel.RemoteKeySet is null)
            return false;

        var unsigned = ChannelAnnouncementBuilder.BuildUnsigned(channel, shortChannelId,
                                                                _lightningSigner.GetNodePublicKey(), ChainHash);
        return _signatureVerifier.VerifyAll(
            ChannelAnnouncementBuilder.GetRemoteSignatureChecks(unsigned, channel, signatures));
    }

    /// <inheritdoc />
    public bool WasSentOnConnection(ChannelId channelId) => _sentOnConnection.ContainsKey(channelId);

    /// <inheritdoc />
    public void MarkSentOnConnection(ChannelId channelId, CompactPubKey peer) => _sentOnConnection[channelId] = peer;

    /// <inheritdoc />
    public void OnPeerConnectionChanged(CompactPubKey peer)
    {
        foreach (var (channelId, sentTo) in _sentOnConnection)
            if (sentTo == peer)
                _sentOnConnection.TryRemove(new KeyValuePair<ChannelId, CompactPubKey>(channelId, sentTo));
    }

    /// <inheritdoc />
    public void OnShortChannelIdChanged(ChannelId channelId)
    {
        _sentOnConnection.TryRemove(channelId, out _);
        if (_announced.TryRemove(channelId, out _))
            _logger.LogInformation("The short channel id of announced channel {ChannelId} moved; it is announced again "
                                 + "once the new funding block is deep enough", channelId);
    }

    /// <inheritdoc />
    public ChannelAnnouncementPayload? TryAssembleAnnouncement(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.RemoteAnnouncementSignatures is not { } remote || channel.LocalAnnouncementSignaturesSentAt is null
         || !CanSendAnnouncementSignatures(channel))
            return null;

        var local = SignOurHalf(channel, out var unsigned);
        var ourNodeId = _lightningSigner.GetNodePublicKey();
        var announcement = ChannelAnnouncementBuilder.Assemble(unsigned, ourNodeId, local, remote);
        if (_signatureVerifier.VerifyAll(ChannelAnnouncementBuilder.GetAllSignatureChecks(announcement)))
            return announcement;

        // The peer's half was stored for another short channel id (received before our funding confirmation, or
        // before a reorg moved the funding transaction): it is useless now. It is forgotten, so the channel is not
        // treated as announced (private update, dont_forward set) and ours goes out again on the next connection,
        // where the peer answers with a half for the current short channel id
        _logger.LogWarning(
            "The stored announcement_signatures of channel {ChannelId} don't sign its announcement of {ShortChannelId}; "
          + "forgetting them", channel.ChannelId, channel.ShortChannelId);
        DiscardRemoteHalf(channel);
        return null;
    }

    /// <inheritdoc />
    public void OnChannelAnnounced(ChannelModel channel, ChannelAnnouncementPayload announcement)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(announcement);

        var hash = announcement.GetSignatureHash();
        if (_announced.TryGetValue(channel.ChannelId, out var known) && known == hash)
            return;

        _announced[channel.ChannelId] = hash;
        _logger.LogInformation("Channel {ChannelId} is announced as {ShortChannelId}", channel.ChannelId,
                               announcement.ShortChannelId);
        _publisher.PublishChannelAnnouncement(announcement, channel.FundingOutput!.Amount);

        // BOLT 7: our update is public from now on (dont_forward clear, real short channel id), then our node
        _channelUpdateService?.OnChannelAnnounced(channel);
        _nodeAnnouncementService?.RequestAnnouncement();
    }

    /// <inheritdoc />
    public bool IsAnnouncementComplete(ChannelId channelId) => _announced.ContainsKey(channelId);

    /// <inheritdoc />
    public async Task<AnnouncementSignaturesMessage?> PrepareOwnAnnouncementSignaturesAsync(
        ChannelModel channel, CompactPubKey peerPubKey, IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        if (!CanSendAnnouncementSignatures(channel) || WasSentOnConnection(channel.ChannelId))
            return null;

        // BOLT 7: sent once at the depth, and again on a reconnection while the peer's half is missing (with both
        // halves exchanged, a peer that lacks ours sends its own on reconnection and gets ours as the reply)
        if (channel.RemoteAnnouncementSignatures is not null && channel.LocalAnnouncementSignaturesSentAt is not null)
            return null;

        var message = CreateAnnouncementSignatures(channel);
        channel.MarkAnnouncementSignaturesSent(_timeProvider.GetUtcNow());
        await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await unitOfWork.SaveChangesAsync();
        MarkSentOnConnection(channel.ChannelId, peerPubKey);

        _logger.LogInformation("Sending our announcement_signatures for channel {ChannelId} ({ShortChannelId})",
                               channel.ChannelId, channel.ShortChannelId);
        return message;
    }

    /// <inheritdoc />
    public async Task CompleteAnnouncementAsync(ChannelModel channel, IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        if (IsAnnouncementComplete(channel.ChannelId) || !IsAnnounced(channel))
            return;

        if (TryAssembleAnnouncement(channel) is { } announcement)
        {
            OnChannelAnnounced(channel, announcement);
            return;
        }

        // The stored half did not sign the current announcement and was forgotten: persist that, so a restart does not
        // treat the channel as announced again
        if (channel.RemoteAnnouncementSignatures is null)
        {
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
            await unitOfWork.SaveChangesAsync();
        }
    }

    /// <inheritdoc />
    public bool IsReadyForAnnouncementSignatures(ChannelModel channel, TxId fundingTxId)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // SP-G-01: a splice is announced only once locked both ways, when it became the current funding; a pending one
        // (at most one side's splice_locked) or a replaced one never is
        return channel.FundingOutput?.TransactionId is { } current && current == fundingTxId
            && CanSendAnnouncementSignatures(channel);
    }

    /// <inheritdoc />
    public bool ShouldDeferRemoteAnnouncementSignatures(ChannelModel channel, ShortChannelId shortChannelId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!channel.AnnounceChannel || (HasShortChannelId(channel) && channel.ShortChannelId == shortChannelId))
            return false;

        // NL-490 (BOLT 7): a late half for a funding a splice replaced matches one of the channel's fundings, so it
        // gets no warning; DeferRemoteAnnouncementSignatures drops it
        if (IsRetiredShortChannelId(channel.ChannelId, shortChannelId))
            return true;

        // BOLT 7: SHOULD defer a splice's announcement_signatures until we sent splice_locked for it. Before our own
        // depth the splice's short channel id is not known to us, so the funding output index is all we can match
        return GetPendingFundings(channel)
           .Any(f => !f.SpliceLockedSent
                  && (f.ShortChannelId is { } known
                          ? known == shortChannelId
                          : f.OutputIndex == shortChannelId.OutputIndex));
    }

    /// <inheritdoc />
    public void DeferRemoteAnnouncementSignatures(ChannelId channelId, ShortChannelId shortChannelId,
                                                  ChannelAnnouncementSignatures signatures)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        if (IsRetiredShortChannelId(channelId, shortChannelId))
        {
            // NL-490: the replaced funding is never announced again (SP-G-01); its late half is ignored, no warning
            _logger.LogDebug("Ignoring the announcement_signatures of channel {ChannelId} for {ShortChannelId}, a "
                           + "funding a splice replaced", channelId, shortChannelId);
            return;
        }

        _deferred[channelId] = (shortChannelId, signatures);
        _logger.LogInformation("Deferring the announcement_signatures of channel {ChannelId} for splice "
                             + "{ShortChannelId} until our splice_locked", channelId, shortChannelId);
    }

    /// <inheritdoc />
    public async Task<AnnouncementSignaturesMessage?> ProcessDeferredRemoteAnnouncementSignaturesAsync(
        ChannelModel channel, CompactPubKey peerPubKey, IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        if (!_deferred.TryRemove(channel.ChannelId, out var deferred))
            return null;

        if (!HasShortChannelId(channel) || deferred.ShortChannelId != channel.ShortChannelId
         || !VerifyRemoteSignatures(channel, deferred.ShortChannelId, deferred.Signatures))
        {
            _logger.LogWarning("Dropping the deferred announcement_signatures of channel {ChannelId} for "
                             + "{ShortChannelId}: they do not sign the announcement of {Current}", channel.ChannelId,
                               deferred.ShortChannelId, channel.ShortChannelId);
            return null;
        }

        channel.SetRemoteAnnouncementSignatures(deferred.Signatures);
        AnnouncementSignaturesMessage? reply = null;
        if (CanSendAnnouncementSignatures(channel) && !WasSentOnConnection(channel.ChannelId))
        {
            reply = CreateAnnouncementSignatures(channel);
            channel.MarkAnnouncementSignaturesSent(_timeProvider.GetUtcNow());
        }

        await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await unitOfWork.SaveChangesAsync();
        if (reply is not null)
            MarkSentOnConnection(channel.ChannelId, peerPubKey);

        _logger.LogInformation("Stored the deferred announcement_signatures of channel {ChannelId} ({ShortChannelId})",
                               channel.ChannelId, channel.ShortChannelId);
        if (TryAssembleAnnouncement(channel) is { } announcement)
            OnChannelAnnounced(channel, announcement);
        return reply;
    }

    private ChainHash ChainHash => _nodeOptions.BitcoinNetwork.ChainHash;

    /// <summary>Whether <paramref name="shortChannelId"/> is a short channel id the channel had before a splice lock
    /// (still in the <see cref="IRetiredScidMap"/>, which keeps it 72 blocks).</summary>
    private bool IsRetiredShortChannelId(ChannelId channelId, ShortChannelId shortChannelId) =>
        _retiredScidMap is not null && _retiredScidMap.TryResolve(shortChannelId, out var retiredOf)
                                    && retiredOf == channelId;

    private IReadOnlyList<ChannelFunding> GetPendingFundings(ChannelModel channel)
    {
        try
        {
            return (_spliceStatePort?.GetFundings(channel) ?? channel.Commitments?.Fundings)?.Pending ?? [];
        }
        catch (InvalidOperationException)
        {
            // No funding outpoint yet: no splice either
            return [];
        }
    }

    /// <summary>Forgets the peer's half and keeps when ours was sent (the model resets both together).</summary>
    private static void DiscardRemoteHalf(ChannelModel channel)
    {
        var sentAt = channel.LocalAnnouncementSignaturesSentAt;
        channel.ResetAnnouncementSignatures();
        if (sentAt is { } ourHalfSentAt)
            channel.MarkAnnouncementSignaturesSent(ourHalfSentAt);
    }

    private bool IsAtAnnouncementDepth(ChannelModel channel)
    {
        if (!HasShortChannelId(channel) || channel.FundingOutput is null)
            return false;

        var depth = _gossipOptions.GetAnnouncementDepth(_nodeOptions.BitcoinNetwork);
        var tip = _blockchainMonitor.LastProcessedBlockHeight;
        var fundingHeight = channel.ShortChannelId.BlockHeight;
        return tip >= fundingHeight && tip - fundingHeight + 1 >= depth;
    }

    /// <summary>
    /// True once the funding transaction confirmed and the channel has its real short channel id (an unknown one is the
    /// default value, without bytes).
    /// </summary>
    internal static bool HasShortChannelId(ChannelModel channel) => ((byte[]?)channel.ShortChannelId) is not null;

    private ChannelAnnouncementSignatures SignOurHalf(ChannelModel channel, out ChannelAnnouncementPayload unsigned)
    {
        unsigned = ChannelAnnouncementBuilder.BuildUnsigned(channel, channel.ShortChannelId,
                                                            _lightningSigner.GetNodePublicKey(), ChainHash);
        return _lightningSigner.SignChannelAnnouncement(channel.ChannelId, unsigned.GetSignedData(),
                                                        channel.ShortChannelId);
    }
}
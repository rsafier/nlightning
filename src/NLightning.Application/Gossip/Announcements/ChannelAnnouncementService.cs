using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Announcements;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <inheritdoc cref="IChannelAnnouncementService"/>
/// <remarks>
/// A singleton. The per-connection record of what was sent lives in memory only: a restart or a new connection sends
/// again, as BOLT 7 asks.
/// </remarks>
public sealed class ChannelAnnouncementService : IChannelAnnouncementService
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IGossipSignatureVerifier _signatureVerifier;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ChannelAnnouncementService> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IOwnGossipSink _ownGossipSink;
    private readonly GossipOptions _gossipOptions;
    private readonly NodeOptions _nodeOptions;

    // Channel id -> the peer whose current connection got our announcement_signatures
    private readonly ConcurrentDictionary<ChannelId, CompactPubKey> _sentOnConnection = new();

    // Channel id -> the signature hash of the announcement last handed on, so a repeat is not handed on again
    private readonly ConcurrentDictionary<ChannelId, Hash> _announced = new();

    public ChannelAnnouncementService(IBlockchainMonitor blockchainMonitor,
                                      IGossipSignatureVerifier signatureVerifier, ILightningSigner lightningSigner,
                                      ILogger<ChannelAnnouncementService> logger, IMessageFactory messageFactory,
                                      IOwnGossipSink ownGossipSink, IOptions<NodeOptions> nodeOptions,
                                      IOptions<GossipOptions>? gossipOptions = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _signatureVerifier = signatureVerifier;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _messageFactory = messageFactory;
        _ownGossipSink = ownGossipSink;
        _nodeOptions = nodeOptions.Value;
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
    }

    /// <inheritdoc />
    public bool CanSendAnnouncementSignatures(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // BOLT 7: only with announce_channel, after channel_ready was sent and received and before any shutdown
        return channel.AnnounceChannel
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
        // before a reorg moved the funding transaction): it is useless now, the peer sends it again on reconnection
        _logger.LogWarning(
            "The stored announcement_signatures of channel {ChannelId} don't sign its announcement of {ShortChannelId}",
            channel.ChannelId, channel.ShortChannelId);
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
        _ownGossipSink.AddOwnChannelAnnouncement(announcement, channel.FundingOutput!.Amount);
    }

    private ChainHash ChainHash => _nodeOptions.BitcoinNetwork.ChainHash;

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
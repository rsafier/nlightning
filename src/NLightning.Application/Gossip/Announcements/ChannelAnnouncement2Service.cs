using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Announcements;

using Channels.Splicing.Interfaces;
using Channels.Taproot;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Enums;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Gossip.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// The <c>channel_announcement_2</c> of our public simple taproot channels (taproot gossip, BOLTs PR #1059 draft
/// <c>4eef3dfa</c>, NL-878 T7): the MuSig2 session with the peer (two nonces and two partial signatures per side),
/// its assembly and publication, and our <c>channel_update_2</c>/<c>node_announcement_2</c> once it is announced.
/// </summary>
/// <remarks>
/// <para><b>Nonces.</b> At the announcement depth each side sends its two announcement nonces in a re-sent
/// <c>channel_ready</c> (TLVs 0/2, the original fields repeated), or, on a reconnection, in its
/// <c>channel_reestablish</c> (TLV 7, with <c>my_current_funding_locked</c> retransmit bit 1 while the exchange is not
/// complete in this process). The peer's nonces are kept even when they arrive before our own depth (the draft's
/// "SHOULD ignore" would deadlock two nodes whose blocks arrive a moment apart). Every session belongs to one
/// connection: a new connection starts a new one with fresh nonces (the signer drops the old secret halves).</para>
/// <para><b>Signatures.</b> Once both nonce pairs are known and the funding is deep enough, we sign (the signer checks
/// the announcement, consumes the nonces) and send <c>announcement_signatures_2</c>; the peer's two partial signatures
/// are verified against its nonces and keys. With both pairs the four partial signatures are aggregated and the final
/// signature is checked like any received <c>channel_announcement_2</c> before we publish it.</para>
/// <para><b>Splices</b> (NL-1131). A spliced channel is announced again under its new funding: our nonces for the
/// splice ride our <c>splice_locked</c> (TLVs 0/2, bound to the splice transaction), the peer's ride its own; once the
/// splice is locked both ways and 6 deep, <c>announcement_signatures_2</c> name the splice's txid and short channel id
/// and the new <c>channel_announcement_2</c> is published (the old short channel id keeps forwarding through the retired
/// map; the graphs see the old funding spent). A session belongs to one funding: the splice's session waits beside the
/// announced old funding until the lock. On a reconnection the nonces ride <c>channel_reestablish</c> TLV 7 for the
/// funding our <c>my_current_funding_locked</c> names (the current funding, or a splice whose <c>splice_locked</c> we
/// sent); a spliced channel never re-sends <c>channel_ready</c> for its nonces.</para>
/// <para>Nothing of a session is persisted: a restart re-runs it on the next connection (<see cref="AnnouncedChannels2"/>).
/// </para>
/// </remarks>
public sealed class ChannelAnnouncement2Service : IChannelAnnouncement2Service
{
    private readonly AnnouncedChannels2 _announced;
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelUpdateService? _channelUpdateService;
    private readonly GossipOptions _gossipOptions;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ChannelAnnouncement2Service> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IMusig2Service _musig2;
    private readonly INodeAnnouncementService? _nodeAnnouncementService;
    private readonly NodeOptions _nodeOptions;
    private readonly OwnGossipPublisher _publisher;
    private readonly ISpliceStatePort? _spliceStatePort;
    private readonly IGossipV2SignatureVerifier _verifier;

    private readonly ConcurrentDictionary<ChannelId, Session> _sessions = new();

    public ChannelAnnouncement2Service(AnnouncedChannels2 announced, IBlockchainMonitor blockchainMonitor,
                                       ILightningSigner lightningSigner, ILogger<ChannelAnnouncement2Service> logger,
                                       IMessageFactory messageFactory, IMusig2Service musig2,
                                       OwnGossipPublisher publisher, IGossipV2SignatureVerifier verifier,
                                       IOptions<NodeOptions> nodeOptions,
                                       IOptions<GossipOptions>? gossipOptions = null,
                                       IChannelUpdateService? channelUpdateService = null,
                                       INodeAnnouncementService? nodeAnnouncementService = null,
                                       ISpliceStatePort? spliceStatePort = null)
    {
        _announced = announced;
        _blockchainMonitor = blockchainMonitor;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _messageFactory = messageFactory;
        _musig2 = musig2;
        _publisher = publisher;
        _verifier = verifier;
        _nodeOptions = nodeOptions.Value;
        _gossipOptions = gossipOptions?.Value ?? new GossipOptions();
        _channelUpdateService = channelUpdateService;
        _nodeAnnouncementService = nodeAnnouncementService;
        _spliceStatePort = spliceStatePort;
    }

    /// <inheritdoc />
    public bool IsV2Channel(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.AnnounceChannel && channel.ChannelParams.OptionSimpleTaproot
                                       && _nodeOptions.Features.IsGossipV2Advertised;
    }

    /// <inheritdoc />
    public bool IsAnnounced(ChannelId channelId) => _announced.IsAnnounced(channelId);

    /// <inheritdoc />
    public ChannelAnnouncement2Payload BuildUnsigned(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return BuildUnsigned(channel, GetCurrentFunding(channel));
    }

    /// <inheritdoc />
    public IReadOnlyList<IChannelMessage> Advance(ChannelModel channel, CompactPubKey peer)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsDue(channel))
            return [];

        // A splice's session (its nonces went out in splice_locked) waits for the lock beside the current funding
        var current = channel.FundingOutput!.TransactionId!.Value;
        if (_sessions.TryGetValue(channel.ChannelId, out var waiting) && waiting.Peer == peer
                                                                       && waiting.FundingTxId != current)
            return [];

        var session = GetSession(channel, peer);
        if (session.Done || (_announced.IsAnnounced(channel.ChannelId) && !session.RetransmitRequested
                                                                       && session.Theirs is null))
            return [];

        var messages = new List<IChannelMessage>();
        if (!session.OursSent)
        {
            // BOLTs #1059: channel_ready names the original funding only; a spliced channel's nonces ride splice_locked
            // or the next channel_reestablish
            if (IsSpliced(channel))
                return [];

            // BOLTs #1059: at the announcement depth, a channel_ready carrying our announcement nonces
            var ours = CreateNonces(channel, session, GetCurrentFunding(channel));
            messages.Add(CreateNonceChannelReady(channel, ours));
            session.OursSent = true;
            _logger.LogInformation("Sending our channel_announcement_2 nonces for channel {ChannelId} "
                                 + "({ShortChannelId})", channel.ChannelId, channel.ShortChannelId);
        }

        messages.AddRange(TrySignAndAssemble(channel, session));
        return messages;
    }

    /// <inheritdoc />
    public IReadOnlyList<IChannelMessage> OnChannelReadyNonces(ChannelModel channel, CompactPubKey peer,
                                                               AnnouncementNodeNonceTlv? nodeNonce,
                                                               AnnouncementBitcoinNonceTlv? bitcoinNonce)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (nodeNonce is null && bitcoinNonce is null)
            return [];

        // BOLTs #1059: one nonce without the other MUST be ignored, SHOULD get a warning
        if (nodeNonce is null || bitcoinNonce is null)
            throw new ChannelWarningException(
                $"channel_ready of channel {channel.ChannelId} carries only one announcement nonce", channel.ChannelId,
                "channel_ready: announcement_node_pubnonce and announcement_bitcoin_pubnonce go together");

        if (!IsV2Channel(channel) || channel.State != ChannelState.Open || IsSpliced(channel)
         || channel.FundingOutput?.TransactionId is null)
        {
            _logger.LogDebug("Ignoring the channel_ready announcement nonces of channel {ChannelId}: not a public "
                           + "taproot channel we announce on its original funding now", channel.ChannelId);
            return [];
        }

        var session = GetSession(channel, peer);
        session.Theirs = new ChannelAnnouncement2Nonces(nodeNonce.Nonce, bitcoinNonce.Nonce);
        session.Done = false;
        _logger.LogDebug("Stored the channel_announcement_2 nonces of channel {ChannelId}", channel.ChannelId);
        return Advance(channel, peer);
    }

    /// <inheritdoc />
    public (AnnouncementNodeNonceTlv Node, AnnouncementBitcoinNonceTlv Bitcoin)? CreateSpliceLockedNonces(
        ChannelModel channel, CompactPubKey peer, ChannelFunding splice)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(splice);
        if (!IsV2Channel(channel) || channel.State != ChannelState.Open || channel.RemoteFundingPubKey is null)
            return null;
        if (splice.ShortChannelId is not { } shortChannelId)
        {
            _logger.LogWarning("No announcement nonces in the splice_locked of {TxId} for channel {ChannelId}: the "
                             + "splice has no short channel id", splice.FundingTxId, channel.ChannelId);
            return null;
        }

        // The peer's nonces for this splice may have come first (its splice_locked); a session for anything else ends
        var session = _sessions.TryGetValue(channel.ChannelId, out var existing) && existing.Peer == peer
                                                                                 && existing.FundingTxId
                                                                                 == splice.FundingTxId
                          ? existing
                          : NewSession(channel.ChannelId, peer, splice.FundingTxId, shortChannelId);
        session.ShortChannelId = shortChannelId;
        try
        {
            var ours = CreateNonces(channel, session, FromSplice(splice, shortChannelId));
            session.OursSent = true;
            _logger.LogInformation("Sending our channel_announcement_2 nonces for splice {TxId} of channel {ChannelId} "
                                 + "({ShortChannelId}) in splice_locked", splice.FundingTxId, channel.ChannelId,
                                   shortChannelId);
            return (new AnnouncementNodeNonceTlv(ours.NodeNonce), new AnnouncementBitcoinNonceTlv(ours.BitcoinNonce));
        }
        catch (SignerException e)
        {
            _logger.LogWarning(e, "No announcement nonces for splice {TxId} of channel {ChannelId}",
                               splice.FundingTxId, channel.ChannelId);
            return null;
        }
    }

    /// <inheritdoc />
    public void OnSpliceLockedNonces(ChannelModel channel, CompactPubKey peer, TxId spliceTxId,
                                     AnnouncementNodeNonceTlv? nodeNonce, AnnouncementBitcoinNonceTlv? bitcoinNonce)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (nodeNonce is null && bitcoinNonce is null)
            return;

        // BOLTs #1059: as for channel_ready, both nonces or none
        if (nodeNonce is null || bitcoinNonce is null)
            throw new ChannelWarningException(
                $"splice_locked of channel {channel.ChannelId} carries only one announcement nonce", channel.ChannelId,
                "splice_locked: announcement_node_pubnonce and announcement_bitcoin_pubnonce go together");

        if (!IsV2Channel(channel) || channel.State != ChannelState.Open)
        {
            _logger.LogDebug("Ignoring the splice_locked announcement nonces of channel {ChannelId}: not a public "
                           + "taproot channel we announce now", channel.ChannelId);
            return;
        }

        var session = _sessions.TryGetValue(channel.ChannelId, out var existing) && existing.Peer == peer
                                                                                 && existing.FundingTxId == spliceTxId
                          ? existing
                          : NewSession(channel.ChannelId, peer, spliceTxId, default);
        session.Theirs = new ChannelAnnouncement2Nonces(nodeNonce.Nonce, bitcoinNonce.Nonce);
        session.Done = false;
        _logger.LogDebug("Stored the channel_announcement_2 nonces of splice {TxId} of channel {ChannelId}",
                         spliceTxId, channel.ChannelId);
    }

    /// <inheritdoc />
    public IReadOnlyList<IChannelMessage> OnSpliceLocked(ChannelModel channel, CompactPubKey peer)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsV2Channel(channel))
            return [];

        // The old funding's announcement is void: the channel is announced again under the splice
        _announced.Remove(channel.ChannelId);
        if (channel.FundingOutput?.TransactionId is not { } current
         || !_sessions.TryGetValue(channel.ChannelId, out var session))
            return [];

        if (session.Peer != peer || session.FundingTxId != current)
        {
            // Nonces for another funding (an RBF sibling that did not lock, the replaced funding): the next
            // channel_reestablish starts a session for this one
            if (_sessions.TryRemove(new KeyValuePair<ChannelId, Session>(channel.ChannelId, session)))
                _lightningSigner.DiscardChannelAnnouncement2Nonces(channel.ChannelId);
            _logger.LogInformation("Channel {ChannelId} locked {TxId}; its channel_announcement_2 waits for the next "
                                 + "channel_reestablish (no announcement nonces were exchanged for it)",
                                   channel.ChannelId, current);
            return [];
        }

        session.ShortChannelId = channel.ShortChannelId;
        session.Done = false;
        return TrySignAndAssemble(channel, session);
    }

    /// <inheritdoc />
    public (MyCurrentFundingLockedTlv? FundingLocked, AnnouncementNoncesTlv? Nonces) CreateReestablishTlvs(
        ChannelModel channel, CompactPubKey peer, TxId? fundingLockedTxId = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsV2Channel(channel) || channel.State != ChannelState.Open || !HasShortChannelId(channel)
         || channel.FundingOutput?.TransactionId is not { } fundingTxId)
            return (null, null);

        // BOLTs #1059: the nonces apply to the funding my_current_funding_locked names: the current one, or a splice
        // whose splice_locked we sent and the peer may not have received
        var target = fundingLockedTxId is { } named && named != fundingTxId
                         ? GetPendingSplice(channel, named) is { ShortChannelId: { } spliceScid } splice
                               ? FromSplice(splice, spliceScid)
                               : (AnnouncementFunding?)null
                         : GetCurrentFunding(channel);
        if (target is not { } funding)
        {
            _logger.LogDebug("No announcement nonces in the channel_reestablish of channel {ChannelId}: its "
                           + "my_current_funding_locked names {TxId}, which has no short channel id here",
                             channel.ChannelId, fundingLockedTxId);
            return (null, null);
        }

        // A new connection: a new session with fresh nonces (the signer drops any older secret halves)
        var session = NewSession(channel.ChannelId, peer, funding.TxId, funding.ShortChannelId);
        ChannelAnnouncement2Nonces ours;
        try
        {
            ours = CreateNonces(channel, session, funding);
        }
        catch (SignerException e)
        {
            _logger.LogWarning(e, "No channel_announcement_2 nonces for channel {ChannelId}", channel.ChannelId);
            return (null, null);
        }

        session.OursSent = true;

        // BOLTs #1059: bit 1 asks the peer to (re)transmit announcement_signatures_2 for the named funding while the
        // exchange is not complete in this process (a splice is never announced before its lock); the nonces go out
        // either way, so the peer can ask us too
        var flags = funding.TxId == fundingTxId && _announced.IsAnnounced(channel.ChannelId)
                        ? (byte)0
                        : MyCurrentFundingLockedTlv.AnnouncementSignatures2Flag;
        return (new MyCurrentFundingLockedTlv(funding.TxId, flags),
                new AnnouncementNoncesTlv(ours.NodeNonce, ours.BitcoinNonce));
    }

    /// <inheritdoc />
    public IReadOnlyList<IChannelMessage> OnReestablish(ChannelModel channel, CompactPubKey peer,
                                                        ChannelReestablishMessage message)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        if (!IsV2Channel(channel))
            return [];

        var askedForUs = message.MyCurrentFundingLockedTlv is { } locked
                      && (locked.RetransmitFlags & MyCurrentFundingLockedTlv.AnnouncementSignatures2Flag) != 0;
        if (message.AnnouncementNoncesTlv is not { } nonces)
        {
            if (askedForUs)
                throw new ChannelWarningException(
                    $"channel_reestablish of channel {channel.ChannelId} asks for announcement_signatures_2 without "
                  + "announcement_nonces", channel.ChannelId,
                    "channel_reestablish: retransmit bit 1 without announcement_nonces");
            return [];
        }

        if (channel.FundingOutput?.TransactionId is not { } current)
            return [];

        // BOLTs #1059: the nonces apply to the funding the peer's my_current_funding_locked names (the current one
        // when it names none)
        var named = message.MyCurrentFundingLockedTlv?.FundingTxId ?? current;
        var theirs = new ChannelAnnouncement2Nonces(nonces.NodeNonce, nonces.BitcoinNonce);
        if (!_sessions.TryGetValue(channel.ChannelId, out var session) || session.Peer != peer)
        {
            _logger.LogDebug("Ignoring the announcement nonces of channel {ChannelId}: our channel_reestablish carried "
                           + "none", channel.ChannelId);
            return [];
        }

        if (session.FundingTxId != named)
        {
            // The peer names a splice whose splice_locked we have not sent yet: its nonces wait for ours, which ride
            // our splice_locked (the session of the funding we named is moot: the splice replaces it)
            if (named != current && GetPendingSplice(channel, named) is { } splice)
            {
                var spliceSession = NewSession(channel.ChannelId, peer, named, splice.ShortChannelId ?? default);
                spliceSession.Theirs = theirs;
                _logger.LogDebug("Stored the channel_announcement_2 nonces of splice {TxId} of channel {ChannelId} from "
                               + "channel_reestablish", named, channel.ChannelId);
                return [];
            }

            _logger.LogDebug("Ignoring the announcement nonces of channel {ChannelId}: they name funding {Named}, ours "
                           + "{Ours}", channel.ChannelId, named, session.FundingTxId);
            return [];
        }

        session.Theirs = theirs;
        session.RetransmitRequested = askedForUs;
        if (named != current)
            return [];

        session.Done = !askedForUs && _announced.IsAnnounced(channel.ChannelId);
        return Advance(channel, peer);
    }

    /// <inheritdoc />
    public IReadOnlyList<IChannelMessage> OnAnnouncementSignatures2(ChannelModel channel, CompactPubKey peer,
                                                                    AnnouncementSignatures2Payload payload)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(payload);

        if (!IsV2Channel(channel))
            throw new ChannelWarningException(
                $"announcement_signatures_2 for channel {channel.ChannelId}, which is not a public taproot channel",
                channel.ChannelId, "announcement_signatures_2 for a channel we do not announce with gossip v2");

        // BOLTs #1059: a funding txid that is not one of the channel's, or a short channel id that is not the
        // canonical one of that funding: warning and close
        if (channel.FundingOutput?.TransactionId is { } current && payload.FundingTxId != current
                                                                 && GetPendingSplice(channel, payload.FundingTxId)
                                                                        is not null)
        {
            // A splice we have not locked yet (the peer locked first): its signatures come again once both locked
            _logger.LogWarning("Ignoring announcement_signatures_2 of channel {ChannelId} for splice {TxId}, which is "
                             + "not locked here yet", channel.ChannelId, payload.FundingTxId);
            return [];
        }

        if (channel.FundingOutput?.TransactionId is not { } fundingTxId || payload.FundingTxId != fundingTxId)
            throw new ChannelWarningException(
                $"announcement_signatures_2 of channel {channel.ChannelId} names funding {payload.FundingTxId}",
                channel.ChannelId, "announcement_signatures_2: unknown funding_txid")
            { CloseConnection = true };
        if (!HasShortChannelId(channel) || payload.ShortChannelId != channel.ShortChannelId)
            throw new ChannelWarningException(
                $"announcement_signatures_2 of channel {channel.ChannelId} names {payload.ShortChannelId}",
                channel.ChannelId, "announcement_signatures_2: short_channel_id is not the funding output's")
            {
                CloseConnection = true
            };

        if (!_sessions.TryGetValue(channel.ChannelId, out var session) || session.Peer != peer
         || session.FundingTxId != fundingTxId || session.Ours is null || session.Theirs is null)
        {
            // BOLTs #1059 MAY warn and close; the next reestablish starts a session with nonces both ways
            _logger.LogWarning("Ignoring announcement_signatures_2 of channel {ChannelId}: no nonces were exchanged on "
                             + "this connection", channel.ChannelId);
            return [];
        }

        var unsigned = BuildUnsigned(channel);
        var theirs = new ChannelAnnouncement2PartialSignatures(payload.NodePartialSignature,
                                                               payload.BitcoinPartialSignature);
        if (!VerifyRemotePartials(channel, unsigned, session, theirs))
            throw new ChannelWarningException(
                $"Invalid announcement_signatures_2 for channel {channel.ChannelId}", channel.ChannelId,
                "announcement_signatures_2: invalid partial signature")
            { CloseConnection = true };

        session.TheirSignatures = theirs;
        _logger.LogInformation("Stored the announcement_signatures_2 of channel {ChannelId} ({ShortChannelId})",
                               channel.ChannelId, channel.ShortChannelId);

        // The peer asks for ours by sending its own (BOLTs #1059: once both nonce pairs are exchanged)
        session.RetransmitRequested = true;
        return TrySignAndAssemble(channel, session);
    }

    /// <inheritdoc />
    public void OnPeerConnectionChanged(CompactPubKey peer)
    {
        foreach (var (channelId, session) in _sessions)
        {
            if (session.Peer != peer)
                continue;

            if (_sessions.TryRemove(new KeyValuePair<ChannelId, Session>(channelId, session)))
                _lightningSigner.DiscardChannelAnnouncement2Nonces(channelId);
        }
    }

    /// <inheritdoc />
    public void OnFundingChanged(ChannelId channelId)
    {
        if (_sessions.TryRemove(channelId, out _))
            _lightningSigner.DiscardChannelAnnouncement2Nonces(channelId);
        _announced.Remove(channelId);
    }

    /// <summary>Open, public taproot, gossip v2 on, a confirmed funding at the announcement depth, no shutdown.</summary>
    private bool IsDue(ChannelModel channel) =>
        IsV2Channel(channel) && channel.State == ChannelState.Open
                             && _gossipOptions.ArePublicChannelsAllowed(_nodeOptions.BitcoinNetwork)
                             && channel.LocalShutdownScript is null && channel.RemoteShutdownScript is null
                             && !channel.DataLossDetected && channel.RemoteKeySet is not null
                             && channel.FundingOutput?.TransactionId is not null
                             && IsAtAnnouncementDepth(channel);

    private bool IsAtAnnouncementDepth(ChannelModel channel)
    {
        if (!HasShortChannelId(channel))
            return false;

        var depth = _gossipOptions.GetAnnouncementDepth(_nodeOptions.BitcoinNetwork);
        var tip = _blockchainMonitor.LastProcessedBlockHeight;
        var fundingHeight = channel.ShortChannelId.BlockHeight;
        return tip >= fundingHeight && tip - fundingHeight + 1 >= depth;
    }

    /// <summary>The session of the channel's current funding on this connection (a new one otherwise).</summary>
    private Session GetSession(ChannelModel channel, CompactPubKey peer)
    {
        var fundingTxId = channel.FundingOutput!.TransactionId!.Value;
        var session = _sessions.GetOrAdd(channel.ChannelId,
                                         _ => new Session(peer, fundingTxId, channel.ShortChannelId));
        if (session.Peer == peer && session.FundingTxId == fundingTxId
                                 && session.ShortChannelId == channel.ShortChannelId)
            return session;

        return NewSession(channel.ChannelId, peer, fundingTxId, channel.ShortChannelId);
    }

    private Session NewSession(ChannelId channelId, CompactPubKey peer, TxId fundingTxId,
                               ShortChannelId shortChannelId)
    {
        var session = new Session(peer, fundingTxId, shortChannelId);
        _sessions[channelId] = session;
        _lightningSigner.DiscardChannelAnnouncement2Nonces(channelId);
        return session;
    }

    private ChannelAnnouncement2Nonces CreateNonces(ChannelModel channel, Session session, AnnouncementFunding funding)
    {
        var ours = _lightningSigner.CreateChannelAnnouncement2Nonces(channel.ChannelId,
                                                                     BuildUnsigned(channel, funding));
        session.Ours = ours;
        session.OurSignatures = null;
        return ours;
    }

    /// <summary>
    /// The unsigned announcement of <paramref name="funding"/> (the current funding, or a splice before its lock): our
    /// node id and the peer's in ascending order, each funding key next to its node, the outpoint and capacity, no
    /// merkle root (BIP 86 funding, see <c>GossipV2SignatureVerifier</c>, NL-1130).
    /// </summary>
    private ChannelAnnouncement2Payload BuildUnsigned(ChannelModel channel, AnnouncementFunding funding)
    {
        var ourNodeId = _lightningSigner.GetNodePublicKey();
        var weAreNode1 = ((ReadOnlySpan<byte>)ourNodeId).SequenceCompareTo(channel.RemoteNodeId) < 0;
        var (node1, node2) = weAreNode1 ? (ourNodeId, channel.RemoteNodeId) : (channel.RemoteNodeId, ourNodeId);
        var (key1, key2) = weAreNode1
                               ? (funding.LocalFundingKey, funding.RemoteFundingKey)
                               : (funding.RemoteFundingKey, funding.LocalFundingKey);

        return ChannelAnnouncement2Payload.Create(_nodeOptions.BitcoinNetwork.ChainHash, [], funding.ShortChannelId,
                                                  funding.CapacitySatoshis, node1, node2, key1, key2, [],
                                                  funding.TxId, funding.OutputIndex);
    }

    private static AnnouncementFunding GetCurrentFunding(ChannelModel channel)
    {
        if (channel.FundingOutput is not { TransactionId: { } fundingTxId, Index: { } index } funding
         || channel.RemoteFundingPubKey is not { } remoteFundingKey)
            throw new InvalidOperationException($"Channel {channel.ChannelId} has no funding output yet");
        if (!HasShortChannelId(channel))
            throw new InvalidOperationException($"Channel {channel.ChannelId} has no short channel id yet");

        return new AnnouncementFunding(fundingTxId, index, (ulong)funding.Amount.Satoshi, channel.ShortChannelId,
                                       channel.LocalFundingPubKey, remoteFundingKey);
    }

    private static AnnouncementFunding FromSplice(ChannelFunding splice, ShortChannelId shortChannelId) =>
        new(splice.FundingTxId, splice.OutputIndex, splice.CapacitySatoshis, shortChannelId,
            splice.LocalFundingPubKey, splice.RemoteFundingPubKey);

    /// <summary>The channel's pending splice <paramref name="fundingTxId"/> (with the splice service's flags), if any.</summary>
    private ChannelFunding? GetPendingSplice(ChannelModel channel, TxId fundingTxId)
    {
        if (_spliceStatePort is null)
            return null;

        try
        {
            return _spliceStatePort.GetFundings(channel).Pending.FirstOrDefault(f => f.FundingTxId == fundingTxId);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The channel runs on a splice (a rotated funding key, splicing plan D5), not its original funding.</summary>
    private static bool IsSpliced(ChannelModel channel) => channel.LocalFundingKeyIndex != 0;

    /// <summary>
    /// Our <c>channel_ready</c> again (what the open sent: the point of local commitment 1, our verification nonce for
    /// the next local commitment, the alias or scid) with the announcement nonces (TLVs 0 and 2).
    /// </summary>
    private ChannelReadyMessage CreateNonceChannelReady(ChannelModel channel, ChannelAnnouncement2Nonces ours)
    {
        var nextLocal = (channel.Commitments?.LocalCommit.Number ?? 0) + 1;
        var secondPoint = _lightningSigner.GetPerCommitmentPoint(channel.ChannelId, 1);
        var verificationNonce = TaprootChannelNonces.GetCurrentFundingNonce(_lightningSigner, channel, nextLocal);

        ShortChannelIdTlv? aliasOrScid = null;
        if (channel.LocalAliases is { Count: > 0 } aliases)
            aliasOrScid = new ShortChannelIdTlv(aliases.First());
        else if (HasShortChannelId(channel))
            aliasOrScid = new ShortChannelIdTlv(channel.ShortChannelId);

        return new ChannelReadyMessage(new ChannelReadyPayload(channel.ChannelId, secondPoint), aliasOrScid,
                                       new NextLocalNonceTlv(verificationNonce),
                                       new AnnouncementNodeNonceTlv(ours.NodeNonce),
                                       new AnnouncementBitcoinNonceTlv(ours.BitcoinNonce));
    }

    private IReadOnlyList<IChannelMessage> TrySignAndAssemble(ChannelModel channel, Session session)
    {
        if (!IsDue(channel) || session.Ours is not { } ours || session.Theirs is not { } theirs
         || session.FundingTxId != channel.FundingOutput!.TransactionId)
            return [];

        var messages = new List<IChannelMessage>();
        var unsigned = BuildUnsigned(channel);
        if (session.OurSignatures is null
         && (!_announced.IsAnnounced(channel.ChannelId) || session.RetransmitRequested))
        {
            var signatures = _lightningSigner.SignChannelAnnouncement2(channel.ChannelId, unsigned, theirs.NodeNonce,
                                                                       theirs.BitcoinNonce);
            session.OurSignatures = signatures;
            messages.Add(_messageFactory.CreateAnnouncementSignatures2Message(
                             channel.ChannelId, channel.ShortChannelId, signatures.NodeSignature,
                             signatures.BitcoinSignature, channel.FundingOutput!.TransactionId!.Value));
            _logger.LogInformation("Sending our announcement_signatures_2 for channel {ChannelId} ({ShortChannelId})",
                                   channel.ChannelId, channel.ShortChannelId);
        }

        if (session.OurSignatures is { } local && session.TheirSignatures is { } remote)
        {
            Assemble(channel, unsigned, ours, theirs, local, remote);
            session.Done = true;
        }

        return messages;
    }

    private bool VerifyRemotePartials(ChannelModel channel, ChannelAnnouncement2Payload unsigned, Session session,
                                      ChannelAnnouncement2PartialSignatures theirs)
    {
        try
        {
            var musigSession = CreateMusigSession(unsigned, session.Ours!.Value, session.Theirs!.Value);
            return _musig2.VerifyPartialSignature(theirs.NodeSignature, session.Theirs.Value.NodeNonce,
                                                  channel.RemoteNodeId, musigSession)
                && _musig2.VerifyPartialSignature(theirs.BitcoinSignature, session.Theirs.Value.BitcoinNonce,
                                                  channel.RemoteFundingPubKey!.Value, musigSession);
        }
        catch (MusigException e)
        {
            _logger.LogWarning(e, "The announcement_signatures_2 of channel {ChannelId} could not be checked",
                               channel.ChannelId);
            return false;
        }
    }

    private Domain.Crypto.Models.MusigSigningSession CreateMusigSession(ChannelAnnouncement2Payload unsigned,
                                                                        ChannelAnnouncement2Nonces ours,
                                                                        ChannelAnnouncement2Nonces theirs)
    {
        var aggregate = _musig2.AggregatePubKeys(_musig2.SortPubKeys([
            unsigned.NodeId1, unsigned.NodeId2, unsigned.BitcoinKey1!.Value, unsigned.BitcoinKey2!.Value
        ]));
        return _musig2.CreateSession(aggregate,
                                     [ours.NodeNonce, ours.BitcoinNonce, theirs.NodeNonce, theirs.BitcoinNonce],
                                     (byte[])unsigned.GetSignatureHash());
    }

    /// <summary>
    /// The final signature from the four partial signatures, checked as a received announcement is (funding output,
    /// aggregate key, BIP 340), then published with our <c>channel_update_2</c> and <c>node_announcement_2</c>.
    /// </summary>
    private void Assemble(ChannelModel channel, ChannelAnnouncement2Payload unsigned, ChannelAnnouncement2Nonces ours,
                          ChannelAnnouncement2Nonces theirs, ChannelAnnouncement2PartialSignatures local,
                          ChannelAnnouncement2PartialSignatures remote)
    {
        var musigSession = CreateMusigSession(unsigned, ours, theirs);
        var signature = _musig2.AggregatePartialSignatures(
            [local.NodeSignature, local.BitcoinSignature, remote.NodeSignature, remote.BitcoinSignature],
            musigSession);
        var announcement = unsigned.WithSignature(new CompactSignature(signature));

        var fundingScript = _musig2.AggregateTaprootKeyPath(channel.LocalFundingPubKey,
                                                            channel.RemoteFundingPubKey!.Value)
                                   .GetTaprootScriptPubKey();
        var check = _verifier.CheckChannelProof(announcement, fundingScript);
        if (check != GossipV2ProofResult.Valid)
        {
            _logger.LogError("The assembled channel_announcement_2 of channel {ChannelId} does not verify ({Result})",
                             channel.ChannelId, check);
            return;
        }

        if (!_announced.Set(channel.ChannelId, announcement))
        {
            _logger.LogDebug("Channel {ChannelId} was announced already; the new signature is not published again",
                             channel.ChannelId);
            return;
        }

        _logger.LogInformation("Channel {ChannelId} is announced with channel_announcement_2 as {ShortChannelId}",
                               channel.ChannelId, announcement.ShortChannelId);
        _publisher.PublishChannelAnnouncement2(announcement, channel.FundingOutput!.Amount);
        _channelUpdateService?.OnChannelAnnounced2(channel);
        _nodeAnnouncementService?.RequestAnnouncement();
    }

    private static bool HasShortChannelId(ChannelModel channel) => ((byte[]?)channel.ShortChannelId) is not null;

    /// <summary>What a <c>channel_announcement_2</c> names of the funding it announces.</summary>
    private readonly record struct AnnouncementFunding(TxId TxId, ushort OutputIndex, ulong CapacitySatoshis,
                                                       ShortChannelId ShortChannelId, CompactPubKey LocalFundingKey,
                                                       CompactPubKey RemoteFundingKey);

    /// <summary>One connection's MuSig2 session of the announcement of one funding of a channel.</summary>
    private sealed class Session(CompactPubKey peer, TxId fundingTxId, ShortChannelId shortChannelId)
    {
        public CompactPubKey Peer { get; } = peer;

        public TxId FundingTxId { get; } = fundingTxId;

        public ShortChannelId ShortChannelId { get; set; } = shortChannelId;

        public ChannelAnnouncement2Nonces? Ours { get; set; }

        public bool OursSent { get; set; }

        public ChannelAnnouncement2Nonces? Theirs { get; set; }

        public ChannelAnnouncement2PartialSignatures? OurSignatures { get; set; }

        public ChannelAnnouncement2PartialSignatures? TheirSignatures { get; set; }

        public bool RetransmitRequested { get; set; }

        public bool Done { get; set; }
    }
}
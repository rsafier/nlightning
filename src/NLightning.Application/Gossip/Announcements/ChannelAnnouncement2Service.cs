using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Announcements;

using Channels.Taproot;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Models;
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
/// <para>Nothing of a session is persisted: a restart re-runs it on the next connection (<see cref="AnnouncedChannels2"/>).
/// Splice re-announcement (<c>splice_locked</c> TLVs 0/2) is not implemented: splicing a public taproot channel is
/// refused (NL-1131).</para>
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
    private readonly IGossipV2SignatureVerifier _verifier;

    private readonly ConcurrentDictionary<ChannelId, Session> _sessions = new();

    public ChannelAnnouncement2Service(AnnouncedChannels2 announced, IBlockchainMonitor blockchainMonitor,
                                       ILightningSigner lightningSigner, ILogger<ChannelAnnouncement2Service> logger,
                                       IMessageFactory messageFactory, IMusig2Service musig2,
                                       OwnGossipPublisher publisher, IGossipV2SignatureVerifier verifier,
                                       IOptions<NodeOptions> nodeOptions,
                                       IOptions<GossipOptions>? gossipOptions = null,
                                       IChannelUpdateService? channelUpdateService = null,
                                       INodeAnnouncementService? nodeAnnouncementService = null)
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
        if (channel.FundingOutput is not { TransactionId: { } fundingTxId, Index: { } index } funding
         || channel.RemoteFundingPubKey is not { } remoteFundingKey)
            throw new InvalidOperationException($"Channel {channel.ChannelId} has no funding output yet");
        if (!HasShortChannelId(channel))
            throw new InvalidOperationException($"Channel {channel.ChannelId} has no short channel id yet");

        var ourNodeId = _lightningSigner.GetNodePublicKey();
        var weAreNode1 = ((ReadOnlySpan<byte>)ourNodeId).SequenceCompareTo(channel.RemoteNodeId) < 0;
        var (node1, node2) = weAreNode1 ? (ourNodeId, channel.RemoteNodeId) : (channel.RemoteNodeId, ourNodeId);
        var (key1, key2) = weAreNode1
                               ? (channel.LocalFundingPubKey, remoteFundingKey)
                               : (remoteFundingKey, channel.LocalFundingPubKey);

        // BIP 86 funding: both keys, no merkle root (see GossipV2SignatureVerifier, NL-1130)
        return ChannelAnnouncement2Payload.Create(_nodeOptions.BitcoinNetwork.ChainHash, [], channel.ShortChannelId,
                                                  (ulong)funding.Amount.Satoshi, node1, node2, key1, key2, [],
                                                  fundingTxId, index);
    }

    /// <inheritdoc />
    public IReadOnlyList<IChannelMessage> Advance(ChannelModel channel, CompactPubKey peer)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsDue(channel))
            return [];

        var session = GetSession(channel, peer);
        if (session.Done || (_announced.IsAnnounced(channel.ChannelId) && !session.RetransmitRequested
                                                                       && session.Theirs is null))
            return [];

        var messages = new List<IChannelMessage>();
        if (!session.OursSent)
        {
            // BOLTs #1059: at the announcement depth, a channel_ready carrying our announcement nonces
            var ours = CreateNonces(channel, session);
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

        if (!IsV2Channel(channel) || channel.State != ChannelState.Open)
        {
            _logger.LogDebug("Ignoring the announcement nonces of channel {ChannelId}: not a public taproot channel "
                           + "we announce now", channel.ChannelId);
            return [];
        }

        var session = GetSession(channel, peer);
        session.Theirs = new ChannelAnnouncement2Nonces(nodeNonce.Nonce, bitcoinNonce.Nonce);
        session.Done = false;
        _logger.LogDebug("Stored the channel_announcement_2 nonces of channel {ChannelId}", channel.ChannelId);
        return Advance(channel, peer);
    }

    /// <inheritdoc />
    public (MyCurrentFundingLockedTlv? FundingLocked, AnnouncementNoncesTlv? Nonces) CreateReestablishTlvs(
        ChannelModel channel, CompactPubKey peer)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsV2Channel(channel) || channel.State != ChannelState.Open || !HasShortChannelId(channel)
         || channel.FundingOutput?.TransactionId is not { } fundingTxId)
            return (null, null);

        // A new connection: a new session with fresh nonces (the signer drops any older secret halves)
        var session = NewSession(channel, peer);
        ChannelAnnouncement2Nonces ours;
        try
        {
            ours = CreateNonces(channel, session);
        }
        catch (SignerException e)
        {
            _logger.LogWarning(e, "No channel_announcement_2 nonces for channel {ChannelId}", channel.ChannelId);
            return (null, null);
        }

        session.OursSent = true;

        // BOLTs #1059: bit 1 asks the peer to (re)transmit announcement_signatures_2 for the current funding while
        // the exchange is not complete in this process; the nonces go out either way, so the peer can ask us too
        var flags = _announced.IsAnnounced(channel.ChannelId)
                        ? (byte)0
                        : MyCurrentFundingLockedTlv.AnnouncementSignatures2Flag;
        return (new MyCurrentFundingLockedTlv(fundingTxId, flags),
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

        if (message.MyCurrentFundingLockedTlv?.FundingTxId is { } named
         && channel.FundingOutput?.TransactionId is { } current && named != current)
        {
            _logger.LogDebug("Ignoring the announcement nonces of channel {ChannelId}: they name funding {Named}, not "
                           + "{Current}", channel.ChannelId, named, current);
            return [];
        }

        if (!_sessions.TryGetValue(channel.ChannelId, out var session) || session.Peer != peer)
        {
            _logger.LogDebug("Ignoring the announcement nonces of channel {ChannelId}: our channel_reestablish carried "
                           + "none", channel.ChannelId);
            return [];
        }

        session.Theirs = new ChannelAnnouncement2Nonces(nonces.NodeNonce, nonces.BitcoinNonce);
        session.RetransmitRequested = askedForUs;
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
         || session.Ours is null || session.Theirs is null)
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

    private Session GetSession(ChannelModel channel, CompactPubKey peer)
    {
        var session = _sessions.GetOrAdd(channel.ChannelId, _ => new Session(peer, channel.ShortChannelId));
        if (session.Peer == peer && session.ShortChannelId == channel.ShortChannelId)
            return session;

        return NewSession(channel, peer);
    }

    private Session NewSession(ChannelModel channel, CompactPubKey peer)
    {
        var session = new Session(peer, channel.ShortChannelId);
        _sessions[channel.ChannelId] = session;
        _lightningSigner.DiscardChannelAnnouncement2Nonces(channel.ChannelId);
        return session;
    }

    private ChannelAnnouncement2Nonces CreateNonces(ChannelModel channel, Session session)
    {
        var ours = _lightningSigner.CreateChannelAnnouncement2Nonces(channel.ChannelId, BuildUnsigned(channel));
        session.Ours = ours;
        session.OurSignatures = null;
        return ours;
    }

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
        if (!IsDue(channel) || session.Ours is not { } ours || session.Theirs is not { } theirs)
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

    /// <summary>One connection's MuSig2 session of a channel's announcement.</summary>
    private sealed class Session(CompactPubKey peer, ShortChannelId shortChannelId)
    {
        public CompactPubKey Peer { get; } = peer;

        public ShortChannelId ShortChannelId { get; } = shortChannelId;

        public ChannelAnnouncement2Nonces? Ours { get; set; }

        public bool OursSent { get; set; }

        public ChannelAnnouncement2Nonces? Theirs { get; set; }

        public ChannelAnnouncement2PartialSignatures? OurSignatures { get; set; }

        public ChannelAnnouncement2PartialSignatures? TheirSignatures { get; set; }

        public bool RetransmitRequested { get; set; }

        public bool Done { get; set; }
    }
}
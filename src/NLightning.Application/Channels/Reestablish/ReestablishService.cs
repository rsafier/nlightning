using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Reestablish;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Reestablish;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Gossip.Announcements.Interfaces;
using Services;
using Splicing.Interfaces;

/// <summary>
/// Builds our <c>channel_reestablish</c> and checks the peer's secret against our own per-commitment points (BOLT2
/// plan N7-T1/T2), and gathers what the planner needs about the channel's interactive funding transaction and fundings
/// (splicing plan SP2-A-T2: <c>next_funding</c>, <c>my_current_funding_locked</c>). Scoped: it reads the peer's
/// shachain and the interactive-tx rows through the scope's unit of work.
/// </summary>
public sealed class ReestablishService
{
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ReestablishService> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IRevocationVerifier _revocationVerifier;
    private readonly IServiceProvider? _serviceProvider;
    private readonly ChannelStateTransitionService _transitions;

    public ReestablishService(ILightningSigner lightningSigner, ILogger<ReestablishService> logger,
                              IMessageFactory messageFactory, IRevocationVerifier revocationVerifier,
                              ChannelStateTransitionService transitions, IServiceProvider? serviceProvider = null)
    {
        _lightningSigner = lightningSigner;
        _logger = logger;
        _messageFactory = messageFactory;
        _revocationVerifier = revocationVerifier;
        _serviceProvider = serviceProvider;
        _transitions = transitions;
    }

    /// <summary>
    /// Our side of a channel as the planner sees it, without the interactive-tx and splice facts. A channel without a
    /// commitment snapshot (not Open yet, or opened before the snapshot existed, NL-246) has no HTLC state: its numbers
    /// come from the channel.
    /// </summary>
    public static ReestablishLocalState GetLocalState(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.Commitments is { } commitments
                   ? ReestablishLocalState.From(commitments, channel.SentCommitDiff is not null,
                                                channel.LastSentCommitmentMessage)
                   : new ReestablishLocalState(channel.LocalCommitmentNumber, channel.RemoteCommitmentNumber, false,
                                               false, LastSentCommitmentMessage.None, false);
    }

    /// <summary>
    /// Our side of a channel as the planner sees it (<see cref="GetLocalState"/>) with the latest interactive funding
    /// transaction (a dual-funded open, or a splice with <c>option_splice</c>; SP-RE-01, SP-RE-03) and, with
    /// <c>option_splice</c> negotiated, the funding-lock facts (SP-RE-02, SP-RE-04, SP-RE-05).
    /// </summary>
    /// <param name="channel">The channel (under its lock).</param>
    /// <param name="negotiatedFeatures">The features negotiated with the peer on this connection; null to look them up
    /// (the peer manager, else our own options).</param>
    public async Task<ReestablishLocalState> GetLocalStateAsync(ChannelModel channel,
                                                                FeatureOptions? negotiatedFeatures = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var local = GetLocalState(channel);
        var spliceNegotiated = IsSpliceNegotiated(channel, negotiatedFeatures);
        var latest = channel.Version == ChannelVersion.V2 || spliceNegotiated
                         ? await GetLatestInteractiveTxAsync(channel)
                         : null;
        var splice = spliceNegotiated ? await GetSpliceStateAsync(channel) : null;
        return local with { LatestInteractiveTx = latest, Splice = splice };
    }

    /// <summary>
    /// Our <c>channel_reestablish</c> (B2-RE-08..12): <c>next_commitment_number</c> = L + 1,
    /// <c>next_revocation_number</c> = R, the peer's last secret (R - 1, from its persisted shachain; zeroes when
    /// R = 0), our point for commitment L, <c>next_funding</c> while the signing of our latest interactive transaction
    /// is not finished (SP-RE-01) and, with <c>option_splice</c>, <c>my_current_funding_locked</c> (SP-RE-02).
    /// </summary>
    /// <exception cref="InvalidOperationException">The peer's shachain does not hold secret R - 1.</exception>
    public async Task<ChannelReestablishMessage> CreateOwnAsync(ChannelModel channel,
                                                                FeatureOptions? negotiatedFeatures = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var own = ReestablishPlanner.CreateOwn(await GetLocalStateAsync(channel, negotiatedFeatures));

        var secret = new byte[ReestablishPlanner.SecretLength];
        if (own.LastReceivedSecretNumber is { } secretNumber)
        {
            using var shachain = await _transitions.LoadRemoteShachainAsync(channel.ChannelId);
            try
            {
                byte[] derived = shachain.DeriveOldSecret(PerCommitmentIndex.From(secretNumber));
                derived.CopyTo(secret, 0);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"The peer's shachain of channel {channel.ChannelId} lacks secret {secretNumber}", e);
            }
        }

        var point = _lightningSigner.GetPerCommitmentPoint(channel.ChannelId, own.CurrentPointNumber);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "channel_reestablish for {ChannelId}: next_commitment_number {Next}, next_revocation_number {Revocation}, next_funding {NextFunding}, my_current_funding_locked {FundingLocked}",
                channel.ChannelId, own.NextCommitmentNumber, own.NextRevocationNumber, own.NextFunding,
                own.MyCurrentFundingLocked);

        var reestablish = _messageFactory.CreateChannelReestablishMessage(channel.ChannelId, own.NextCommitmentNumber,
                                                                         own.NextRevocationNumber, secret, point);
        if (own.NextFunding is null && own.MyCurrentFundingLocked is null)
            return reestablish;

        return new ChannelReestablishMessage(
            reestablish.Payload,
            own.NextFunding is { } nextFunding
                ? new NextFundingTlv((byte[])nextFunding.TxId, nextFunding.RetransmitFlags)
                : null,
            own.MyCurrentFundingLocked is { } fundingLocked
                ? new MyCurrentFundingLockedTlv(fundingLocked.TxId, fundingLocked.RetransmitFlags)
                : null);
    }

    /// <summary>
    /// True when <paramref name="secret"/> is our per-commitment secret of commitment <paramref name="number"/>:
    /// checked against our point, so nothing is revealed (the peer may claim a number we never revoked).
    /// </summary>
    public bool IsOurSecret(ChannelModel channel, ulong number, ReadOnlyMemory<byte> secret)
    {
        ArgumentNullException.ThrowIfNull(channel);
        try
        {
            var point = _lightningSigner.GetPerCommitmentPoint(channel.ChannelId, number);
            return _revocationVerifier.IsValidSecret(new Secret(secret.ToArray()), point);
        }
        catch (Exception e)
        {
            // Not a valid scalar, or a number we can't derive: not our secret
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(e, "Secret check {Number} of channel {ChannelId} failed", number, channel.ChannelId);
            return false;
        }
    }

    /// <summary>
    /// Whether <c>option_splice</c> is negotiated with the channel's peer: the given features, else the peer's current
    /// connection (the peer manager), else our own options (in-process harnesses without a peer manager).
    /// </summary>
    private bool IsSpliceNegotiated(ChannelModel channel, FeatureOptions? negotiatedFeatures)
    {
        var features = negotiatedFeatures;
        if (features is null)
        {
            if (_serviceProvider?.GetService<IPeerManager>() is { } peerManager)
                features = peerManager.GetPeer(channel.RemoteNodeId) is { } peer
                        && peer.TryGetPeerService(out var service)
                               ? service.Features
                               : null;
            else
                features = _serviceProvider?.GetService<IOptions<NodeOptions>>()?.Value.Features;
        }

        return features is { OptionSplice: not FeatureSupport.No };
    }

    /// <summary>
    /// The latest constructed, not aborted interactive-tx row of the channel (rows exist from our
    /// <c>commitment_signed</c> on), or null when there is none or the rows cannot be read.
    /// </summary>
    private async Task<ReestablishInteractiveTxState?> GetLatestInteractiveTxAsync(ChannelModel channel)
    {
        IReadOnlyList<InteractiveTxSessionModel>? rows;
        try
        {
            if (_serviceProvider?.GetService<IUnitOfWork>()?.InteractiveTxSessionDbRepository is not { } sessions)
                return null;

            rows = await sessions.GetByChannelIdAsync(channel.ChannelId);
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            return null;
        }

        var latest = rows?.Where(s => s.State != InteractiveTxSessionState.Aborted && s.ConstructedTx is not null)
                          .MaxBy(s => s.CreatedAt);
        if (latest?.ConstructedTx is not { } transaction)
            return null;

        return new ReestablishInteractiveTxState(transaction.TxId,
                                                 latest.Purpose is InteractiveTxPurpose.Splice
                                                                or InteractiveTxPurpose.SpliceRbf,
                                                 latest.CommitmentSignedSent, latest.CommitmentSignedReceived,
                                                 latest.TxSignaturesSent, latest.TxSignaturesReceived,
                                                 SendsTxSignaturesFirst(channel, latest));
    }

    /// <summary>IT-SIG-01 over the negotiated inputs; without our node id, whether we already sent ours.</summary>
    private bool SendsTxSignaturesFirst(ChannelModel channel, InteractiveTxSessionModel session)
    {
        if (_serviceProvider?.GetService<ISecureKeyManager>() is not { } keyManager)
            return session.TxSignaturesSent;

        try
        {
            return TxSignaturesOrder.LocalSendsFirst(session.Inputs, keyManager.GetNodePubKey(),
                                                     channel.RemoteNodeId);
        }
        catch (ArgumentException)
        {
            return session.TxSignaturesSent;
        }
    }

    /// <summary>
    /// The funding-lock facts for <c>my_current_funding_locked</c>: the fundings (the splice state port's, else the
    /// channel's funding output), the last <c>splice_locked</c> we sent (a pending splice we marked sent, else the
    /// current funding when it is a locked splice), and the announcement state; null while the funding outpoint is
    /// unknown.
    /// </summary>
    private async Task<ReestablishSpliceState?> GetSpliceStateAsync(ChannelModel channel)
    {
        FundingSet fundings;
        try
        {
            if (_serviceProvider?.GetService<ISpliceStatePort>() is { } port)
                fundings = port.GetFundings(channel);
            else if (channel.FundingOutput is { } output && ChannelFunding.FromFundingOutput(output) is { } current)
                fundings = FundingSet.Single(current);
            else
                return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var currentFunding = fundings.Current;
        var currentIsSplice = currentFunding.Kind != ChannelFundingKind.Initial;
        var lastSpliceLockedSent = fundings.Pending.LastOrDefault(f => f.SpliceLockedSent) is { } lastSent
                                         ? lastSent.FundingTxId
                                         : currentIsSplice
                                             ? currentFunding.FundingTxId
                                             : (TxId?)null;
        var channelReadySent = channel.State is ChannelState.ReadyForUs or ChannelState.Open
                                             or ChannelState.ShuttingDown or ChannelState.Negotiating
                                             or ChannelState.Closing;

        var received = fundings.Pending.Where(f => f.AnnouncementSignaturesReceived).Select(f => f.FundingTxId)
                               .ToHashSet();
        // The channel's stored half signs the original funding's announcement: it is never reset when a splice locks,
        // so for a locked splice only that funding's own flag counts (SP-RE-02: bit 0 while we lack its half)
        if (currentIsSplice
                ? currentFunding.AnnouncementSignaturesReceived
               || await IsAnnouncementSignaturesReceivedStoredAsync(channel, currentFunding.FundingTxId)
                : channel.RemoteAnnouncementSignatures is not null)
            received.Add(currentFunding.FundingTxId);

        var ready = new HashSet<TxId>();
        if (channel.AnnounceChannel && _serviceProvider?.GetService<IChannelAnnouncementService>() is { } announcements)
        {
            foreach (var funding in fundings.Active)
            {
                if (IsReadyForAnnouncementSignatures(announcements, channel, funding.FundingTxId))
                    ready.Add(funding.FundingTxId);
            }
        }

        return new ReestablishSpliceState(channelReadySent, currentFunding.FundingTxId, lastSpliceLockedSent,
                                          channel.AnnounceChannel,
                                          fundings.Pending
                                                  .Select(f => new ReestablishPendingSplice(
                                                              f.FundingTxId, f.SpliceLockedSent,
                                                              f.SpliceLockedReceived))
                                                  .ToList(),
                                          received, ready, currentIsSplice);
    }

    /// <summary>
    /// Our <c>tx_signatures</c> for the interactive funding transaction <paramref name="fundingTxId"/> rebuilt from its
    /// stored <c>InteractiveTxSessions</c> row (our witnesses and <c>shared_input_signature</c> are saved before the
    /// first one goes out), for a <c>next_funding</c> retransmission when the interactive-tx driver no longer holds
    /// the negotiation (after a restart). The same message as the driver's own rebuild; null when the row does not
    /// exist, is aborted or our <c>tx_signatures</c> was never sent (nothing signed to send again).
    /// </summary>
    public async Task<TxSignaturesMessage?> CreateStoredTxSignaturesAsync(ChannelModel channel, TxId fundingTxId)
    {
        ArgumentNullException.ThrowIfNull(channel);
        IReadOnlyList<InteractiveTxSessionModel>? rows;
        try
        {
            if (_serviceProvider?.GetService<IUnitOfWork>()?.InteractiveTxSessionDbRepository is not { } sessions)
                return null;

            rows = await sessions.GetByChannelIdAsync(channel.ChannelId);
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            return null;
        }

        var row = rows?.Where(s => s.State != InteractiveTxSessionState.Aborted
                                && s.ConstructedTx is { } tx && tx.TxId == fundingTxId)
                      .MaxBy(s => s.CreatedAt);
        if (row is not { TxSignaturesSent: true, ConstructedTx: { } transaction })
            return null;

        return new TxSignaturesMessage(
            new TxSignaturesPayload(channel.ChannelId, transaction.TxId, (row.OurWitnesses ?? []).ToList()),
            row.OurSharedInputSignature is { } signature ? new SharedInputSignatureTlv(signature) : null);
    }

    /// <summary>The stored <c>ChannelFundings</c> row's <c>AnnouncementSignaturesReceived</c> of a funding.</summary>
    private async Task<bool> IsAnnouncementSignaturesReceivedStoredAsync(ChannelModel channel, TxId fundingTxId)
    {
        try
        {
            if (_serviceProvider?.GetService<IUnitOfWork>()?.ChannelFundingDbRepository is not { } fundings)
                return false;

            return (await fundings.GetByChannelIdAsync(channel.ChannelId))
               .Any(f => f.FundingTxId == fundingTxId && f.AnnouncementSignaturesReceived);
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// SP-G-01 through the announcement service; until lane SP2-B implements
    /// <see cref="IChannelAnnouncementService.IsReadyForAnnouncementSignatures"/>, the channel's current funding when
    /// <see cref="IChannelAnnouncementService.CanSendAnnouncementSignatures"/> says so.
    /// </summary>
    internal static bool IsReadyForAnnouncementSignatures(IChannelAnnouncementService announcements,
                                                          ChannelModel channel, TxId fundingTxId)
    {
        try
        {
            return announcements.IsReadyForAnnouncementSignatures(channel, fundingTxId);
        }
        catch (NotImplementedException)
        {
            return channel.FundingOutput?.TransactionId == fundingTxId
                && announcements.CanSendAnnouncementSignatures(channel);
        }
    }
}
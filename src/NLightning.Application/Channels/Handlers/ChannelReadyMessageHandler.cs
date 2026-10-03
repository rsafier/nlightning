using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Handlers;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using DualFunding;
using Interfaces;
using Services;

public class ChannelReadyMessageHandler : IChannelMessageHandler<ChannelReadyMessage>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly DualFundedOpenService? _dualFundedOpenService;
    private readonly ILogger<ChannelReadyMessageHandler> _logger;
    private readonly ulong? _maxDustHtlcExposureMsat;
    private readonly IUnitOfWork _unitOfWork;

    /// <param name="channelMemoryRepository">The channels in memory.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="unitOfWork">The scope's unit of work.</param>
    /// <param name="nodeOptions">Gives the dust exposure policy stored with the first commitment state
    /// (<see cref="NodeOptions.MaxDustHtlcExposureMsat"/>, NL-254); without it the state has none.</param>
    /// <param name="dualFundedOpenService">Defers an early <c>channel_ready</c> of a dual-funded open with several
    /// signed RBF attempts until our confirmation tells which one confirmed (NL-528); none without dual funding.</param>
    public ChannelReadyMessageHandler(IChannelMemoryRepository channelMemoryRepository,
                                      ILogger<ChannelReadyMessageHandler> logger, IUnitOfWork unitOfWork,
                                      IOptions<NodeOptions>? nodeOptions = null,
                                      DualFundedOpenService? dualFundedOpenService = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _dualFundedOpenService = dualFundedOpenService;
        _logger = logger;
        _unitOfWork = unitOfWork;
        _maxDustHtlcExposureMsat = nodeOptions?.Value.MaxDustHtlcExposureMsat;
    }

    public async Task<IReadOnlyList<IChannelMessage>> HandleAsync(
        ChannelReadyMessage message, ChannelState currentState, FeatureOptions negotiatedFeatures,
        CompactPubKey peerPubKey)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Processing ChannelReadyMessage with ChannelId: {ChannelId} from Peer: {PeerPubKey}",
                             message.Payload.ChannelId, peerPubKey);

        var payload = message.Payload;

        // A closing channel was Open before its first shutdown: this is the retransmission BOLT 2 requires after a
        // reconnection when neither side has signed a commitment since (B2-RE-15), not a protocol violation
        if (currentState is ChannelState.ShuttingDown or ChannelState.Negotiating or ChannelState.Closing)
        {
            _logger.LogDebug("Ignoring a retransmitted channel_ready for channel {ChannelId} in state {State}",
                             payload.ChannelId, Enum.GetName(currentState));
            return [];
        }

        if (currentState is not (ChannelState.V1FundingSigned
                              or ChannelState.ReadyForThem
                              or ChannelState.ReadyForUs
                              or ChannelState.Open))
            throw new ChannelErrorException(
                $"Unexpected ChannelReady message in state {Enum.GetName(currentState)}",
                payload.ChannelId,
                "Protocol violation: unexpected ChannelReady message");

        // Check if there's a channel for this peer
        if (!_channelMemoryRepository.TryGetChannel(payload.ChannelId, out var channel))
            throw new ChannelErrorException("Channel not found", payload.ChannelId,
                                            "This channel is not ready to be opened");

        var mustUseScidAlias = channel.ChannelParams.UseScidAlias > FeatureSupport.No;
        if (mustUseScidAlias && message.ShortChannelIdTlv is null)
            throw new ChannelWarningException("No ShortChannelIdTlv provided",
                                              payload.ChannelId,
                                              "This channel requires a ShortChannelIdTlv to be provided");

        // NL-528: a dual-funded open with several signed RBF attempts builds its first commitment state on the attempt
        // that confirmed, which only our own confirmation tells; the peer's channel_ready waits for it
        if (currentState == ChannelState.V1FundingSigned && channel.Version == ChannelVersion.V2
                                                         && _dualFundedOpenService is not null
                                                         && await _dualFundedOpenService.TryDeferChannelReadyAsync(
                                                                message, negotiatedFeatures, _unitOfWork))
            return [];

        // Store their second per-commitment point, only on the first channel_ready (the remote index counts down
        // from 2^48-1, so it is still at the first index until we store it). The first commitment state snapshot is
        // built now, while the point of the peer's current commitment is still known (NL-232), and saved with it.
        ChannelCommitments? firstSnapshot = null;
        if (channel.RemoteKeySet!.CurrentPerCommitmentIndex == CryptoConstants.FirstPerCommitmentIndex)
        {
            if (channel.Commitments is null)
                firstSnapshot = TryCreateFirstSnapshot(channel, channel.RemoteKeySet.CurrentPerCommitmentCompactPoint,
                                                       payload.SecondPerCommitmentPoint);

            channel.RemoteKeySet.UpdatePerCommitmentPoint(payload.SecondPerCommitmentPoint);
        }

        // NL-717: the peer's alias (channel_ready short_channel_id) is kept whatever the channel type, the first one
        // received: BOLT 2 lets the receiver use it and makes its sender always recognize it, and a peer can resolve an
        // unannounced channel only by it (Eclair maps a private channel by its own alias, not the real short channel
        // id), so our blinded paths name such a channel by it
        var aliasLearned = false;
        if (message.ShortChannelIdTlv is { } aliasTlv && channel.RemoteAlias is null)
        {
            channel.RemoteAlias = aliasTlv.ShortChannelId;
            aliasLearned = true;
        }

        switch (currentState)
        {
            case ChannelState.Open or ChannelState.ReadyForThem: // Handle ScidAlias
                {
                    if (aliasLearned)
                    {
                        if (_logger.IsEnabled(LogLevel.Debug))
                            _logger.LogDebug("Stored remote alias {Alias} for channel {ChannelId}", channel.RemoteAlias,
                                             payload.ChannelId);

                        await PersistChannelAsync(channel);
                    }
                    else if (mustUseScidAlias)
                    {
                        if (ShouldReplaceAlias())
                        {
                            var oldAlias = channel.RemoteAlias;
                            channel.RemoteAlias = message.ShortChannelIdTlv!.ShortChannelId;

                            if (_logger.IsEnabled(LogLevel.Debug))
                                _logger.LogDebug(
                                    "Updated remote alias for channel {ChannelId} from {OldAlias} to {NewAlias}",
                                    payload.ChannelId, oldAlias, channel.RemoteAlias);

                            await PersistChannelAsync(channel);
                        }
                        else if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug(
                                "Keeping existing remote alias {ExistingAlias} for channel {ChannelId}",
                                channel.RemoteAlias,
                                payload.ChannelId);
                        }
                    }
                    else if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Received duplicate ChannelReady message for channel {ChannelId} in Open state",
                                         payload.ChannelId);

                    break;
                }
            case ChannelState.ReadyForUs: // We already sent our ChannelReady, now they sent theirs
                {
                    // Valid transition: ReadyForUs -> Open
                    channel.UpdateState(ChannelState.Open);
                    await PersistChannelAsync(channel, firstSnapshot);

                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation("Channel {ChannelId} is now open", payload.ChannelId);

                    // The application learns the channel is usable from the memory repository's
                    // IChannelMemoryRepository.OnChannelOpened, which PersistChannelAsync's UpdateChannel raises for
                    // this transition (NL-054); our channel_update goes out through it too
                    break;
                }
            case ChannelState.V1FundingSigned: // First ChannelReady
                {
                    // Valid transition: V1FundingSigned -> ReadyForThem
                    channel.UpdateState(ChannelState.ReadyForThem);
                    await PersistChannelAsync(channel, firstSnapshot);

                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation(
                            "Received ChannelReady from peer for channel {ChannelId}, waiting for funding confirmation",
                            payload.ChannelId);

                    break;
                }
        }

        return []; // No further action needed
    }

    /// <summary>
    /// The channel's first commitment state (plan N6-T1), or null (logged) when the open flow left something
    /// inconsistent: the channel then works as before, without HTLCs.
    /// </summary>
    private ChannelCommitments? TryCreateFirstSnapshot(ChannelModel channel, CompactPubKey remoteCurrentPoint,
                                                       CompactPubKey remoteNextPoint)
    {
        try
        {
            return ChannelStateTransitionService.CreateInitialCommitments(channel, remoteCurrentPoint,
                                                                          remoteNextPoint, _maxDustHtlcExposureMsat);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException)
        {
            _logger.LogError(e, "Cannot build the commitment state of channel {ChannelId}: HTLCs are not possible on it",
                             channel.ChannelId);
            return null;
        }
    }

    /// <summary>
    /// Persists a channel to the database using a scoped Unit of Work, together with its first commitment state
    /// snapshot when one is given (one save); the snapshot is attached to the model only after the save.
    /// </summary>
    private async Task PersistChannelAsync(ChannelModel channel, ChannelCommitments? firstSnapshot = null)
    {
        try
        {
            // Check if the channel already exists
            _ = await _unitOfWork.ChannelDbRepository.GetByIdAsync(channel.ChannelId)
             ?? throw new ChannelWarningException("Channel not found in database", channel.ChannelId,
                                                  "Sorry, we had an internal error");
            if (firstSnapshot is not null)
                await _unitOfWork.ChannelStateDbRepository.InitializeAsync(firstSnapshot);
            await _unitOfWork.ChannelDbRepository.UpdateAsync(channel);
            await _unitOfWork.SaveChangesAsync();

            if (firstSnapshot is not null)
                channel.UpdateCommitments(firstSnapshot);

            _channelMemoryRepository.UpdateChannel(channel);

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Successfully persisted channel {ChannelId} to database", channel.ChannelId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist channel {ChannelId} to database", channel.ChannelId);
            throw;
        }
    }

    private static bool ShouldReplaceAlias()
    {
        return RandomNumberGenerator.GetInt32(0, 2) switch
        {
            0 => true,
            _ => false
        };
    }
}
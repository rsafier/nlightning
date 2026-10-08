using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Services;

using Domain.Bitcoin.Constants;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Constants;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Payments.Keysend;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.Tlv;
using Domain.Signing.Vls;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Crypto.Hashes;
using Interfaces;

/// <summary>
/// The send side of the BOLT 2 normal operation (plan N6-T2, §3.10): our <c>update_add_htlc</c>,
/// <c>update_fulfill_htlc</c>, <c>update_fail_htlc</c>, <c>update_fail_malformed_htlc</c> and <c>update_fee</c>.
/// </summary>
/// <remarks>
/// <para>
/// Singleton. Each operation takes the channel's lock, checks the preconditions, runs the commitment engine, persists
/// the transition in one save (<see cref="ChannelStateTransitionService.CommitAsync"/>: I1, I2), enqueues the wire
/// message through <see cref="IChannelMessagePublisher"/> while still holding the lock, then asks the
/// <see cref="ICommitScheduler"/> to sign once the lock is released.
/// </para>
/// <para>
/// Preconditions: HTLCs enabled (<see cref="NodeOptions.HtlcsEnabled"/>), channel <see cref="ChannelState.Open"/> (or
/// <see cref="ChannelState.ShuttingDown"/> for removals, and for fee updates while HTLCs are left; never for an add)
/// with a commitment snapshot, not failed, no data loss, plus the engine's BOLT 2 sender rules, and the channel's link
/// is up (<see cref="IPeerLivenessProbe"/>: the peer is connected on the connection the channel was opened or
/// reestablished on). An add is also refused while the chain monitor's processing is halted (NL-216,
/// <see cref="ChainProcessingHalt"/>) and while the node drains for a graceful shutdown (NL-591, <see cref="NodeDrain"/>);
/// removals and fee updates are not, they only lower the risk. Every operation
/// needs the link, removals and fee updates too: a message raised for a peer that
/// is not connected is dropped, and there is no retransmission until channel_reestablish (N7), so an update persisted
/// for an away peer would later be covered by a <c>commitment_signed</c> the peer can't verify. A refused removal is
/// not lost: the HTLC stays locked in and its event is replayed (startup; N7 after the reestablish). A failed
/// precondition throws <see cref="CommitmentRefusedException"/> with nothing persisted or sent; an unknown channel
/// throws <see cref="KeyNotFoundException"/>.
/// </para>
/// <para>
/// Quiescence (BOLT 2 "Channel Quiescence", splicing plan Q1-T4): while the channel is quiescing or quiescent
/// (<see cref="QuiescenceState.BlocksNewLocalUpdates"/> of the optional <see cref="IQuiescenceService"/>) every update is
/// refused with <see cref="ChannelQuiescentException"/> (Q-S-04 once we sent <c>stfu</c>, else Q-R-02), nothing
/// persisted or sent. It is temporary: when the quiescence ends the service replays the channel's pending HTLC events,
/// so a refused fulfill or fail is sent then. The commit scheduler still signs what is pending (the drain).
/// </para>
/// <para>
/// The <see cref="HtlcOrigin"/> of an offer is validated and staged with
/// <see cref="IChannelStateDbRepository.SetHtlcOriginAsync"/> in the same save as the add (NL-250), so after a restart
/// the resolution of the outgoing HTLC can always be routed back to its payment or forward circuit.
/// </para>
/// <para>
/// Attribution (BOLT 4 <c>option_attribution_data</c>, NL-326): the overloads taking an
/// <see cref="AttributedErrorPacket"/> or <see cref="AttributedFulfillment"/> persist the <c>attribution_data</c> (and
/// <c>fulfillment_payload</c>) with the removal and send them as TLVs; <see cref="GetHoldTimeAsync"/> gives the hold
/// time to put in them, measured with the injected <see cref="TimeProvider"/> from the HTLC's persisted receipt time.
/// </para>
/// </remarks>
public sealed class ChannelOperationsService : IChannelOperations
{
    private const string AddOperation = "update_add_htlc";
    private const string FeeOperation = "update_fee";

    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IVlsChannelSigner? _vlsSigner;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelMessagePublisher _channelMessagePublisher;
    private readonly ICommitScheduler _commitScheduler;
    private readonly ILogger<ChannelOperationsService> _logger;
    private readonly INodeDrainState? _nodeDrainState;
    private readonly NodeOptions _nodeOptions;
    private readonly IPeerLivenessProbe _peerLivenessProbe;
    private readonly IQuiescenceService? _quiescenceService;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Payments.Events.HtlcEventMonitor? _htlcMonitor;

    public ChannelOperationsService(IChannelLockProvider channelLockProvider,
                                    IChannelMemoryRepository channelMemoryRepository,
                                    IChannelMessagePublisher channelMessagePublisher, ICommitScheduler commitScheduler,
                                    ILogger<ChannelOperationsService> logger, IOptions<NodeOptions> nodeOptions,
                                    IPeerLivenessProbe peerLivenessProbe, IServiceScopeFactory serviceScopeFactory,
                                    IBlockchainMonitor? blockchainMonitor = null, TimeProvider? timeProvider = null,
                                    IQuiescenceService? quiescenceService = null,
                                    INodeDrainState? nodeDrainState = null, Payments.Events.HtlcEventMonitor? htlcMonitor = null,
                                    IVlsChannelSigner? vlsSigner = null)
    {
        _htlcMonitor = htlcMonitor;
        _vlsSigner = vlsSigner;
        _nodeDrainState = nodeDrainState;
        _quiescenceService = quiescenceService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _blockchainMonitor = blockchainMonitor;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _channelMessagePublisher = channelMessagePublisher;
        _commitScheduler = commitScheduler;
        _logger = logger;
        _nodeOptions = nodeOptions.Value;
        _peerLivenessProbe = peerLivenessProbe;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <inheritdoc />
    public Task<ulong> OfferHtlcAsync(ChannelId channelId, LightningMoney amount, Hash paymentHash,
                                      uint cltvExpiry, OnionPacket onion, BlindedPathTlv? pathKey,
                                      HtlcOrigin origin, CancellationToken cancellationToken = default) =>
        OfferHtlcAsync(channelId, amount, paymentHash, cltvExpiry, onion, pathKey, origin, [], cancellationToken);

    /// <inheritdoc />
    public async Task<ulong> OfferHtlcAsync(ChannelId channelId, LightningMoney amount, Hash paymentHash,
                                            uint cltvExpiry, OnionPacket onion, BlindedPathTlv? pathKey,
                                            HtlcOrigin origin, IReadOnlyList<CustomRecord> wireCustomRecords,
                                            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wireCustomRecords);
        // NL-1182: checked (sorted, and small enough for BOLT 8 with the blinded_path) before anything is staged; an
        // add too large to send would be committed and persisted but never reach the peer. Persisted with the HTLC
        // for its retransmissions
        var encodedRecords = WireCustomRecordCodec.EncodeForUpdateAddHtlc(wireCustomRecords, pathKey is not null);
        ArgumentNullException.ThrowIfNull(amount);
        if (_vlsSigner is not null && amount.MilliSatoshi % 1_000 != 0)
            throw new CommitmentRefusedException("VLS-AMOUNT-PRECISION",
                                                 "VLS requires HTLC amounts in whole satoshis");
        if (!origin.IsValid)
            throw new ArgumentException("The HTLC origin routes nowhere", nameof(origin));
        if (onion.Length == 0)
            throw new ArgumentException("The onion is empty", nameof(onion));

        // NL-216: while the node does not follow the chain it cannot time the HTLC out on chain; take no new risk
        if (_blockchainMonitor is { IsChainProcessingHalted: true })
            throw new CommitmentRefusedException(ChainProcessingHalt.RequirementId,
                                                 ChainProcessingHalt.Refusal(AddOperation));

        // NL-591: a node draining for its shutdown takes no new HTLC (forwards fail back upstream)
        if (_nodeDrainState is { IsDraining: true })
            throw new CommitmentRefusedException(NodeDrain.RequirementId, NodeDrain.Refusal(AddOperation));

        using var observedOrigin = _htlcMonitor?.WithOrigin(origin);
        var height = _blockchainMonitor?.LastProcessedBlockHeight;
        var result = await RunAsync(channelId, AddOperation,
                                    c => c.SendAdd(amount.MilliSatoshi, paymentHash, cltvExpiry, onion.ToBytes(),
                                                   pathKey?.PathKey, height is > 0 ? height : null, encodedRecords),
                                    cancellationToken,
                                    (unitOfWork, added) => unitOfWork.ChannelStateDbRepository.SetHtlcOriginAsync(
                                        channelId, AddedHtlcKey(added), origin));
        var htlcId = AddedHtlcKey(result).Id;

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Offered HTLC {HtlcId} of {Amount} on channel {ChannelId} ({Origin})", htlcId, amount,
                             channelId, origin.Kind);

        return htlcId;
    }

    /// <inheritdoc />
    public Task FulfillHtlcAsync(ChannelId channelId, ulong htlcId, Secret paymentPreimage,
                                 CancellationToken cancellationToken = default) =>
        FulfillAsync(channelId, htlcId, paymentPreimage, null, cancellationToken);

    /// <inheritdoc />
    public Task FulfillHtlcAsync(ChannelId channelId, ulong htlcId, Secret paymentPreimage,
                                 Func<IUnitOfWork, Task> stageWithFulfill,
                                 CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stageWithFulfill);
        return FulfillAsync(channelId, htlcId, paymentPreimage, (unitOfWork, _) => stageWithFulfill(unitOfWork),
                            cancellationToken);
    }

    /// <inheritdoc />
    public Task FulfillHtlcAsync(ChannelId channelId, ulong htlcId, Secret paymentPreimage,
                                 AttributedFulfillment attribution, Func<IUnitOfWork, Task>? stageWithFulfill = null,
                                 CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        if (attribution.FulfillmentPayload is { Length: > OnionConstants.MaxFulfillmentPayloadLength } payload)
            throw new ArgumentException(
                $"The fulfillment_payload is {payload.Length} bytes; at most {OnionConstants.MaxFulfillmentPayloadLength}",
                nameof(attribution));

        return FulfillAsync(channelId, htlcId, paymentPreimage,
                            stageWithFulfill is null ? null : (unitOfWork, _) => stageWithFulfill(unitOfWork),
                            cancellationToken, attribution.AttributionData, attribution.FulfillmentPayload ?? []);
    }

    /// <inheritdoc />
    public async Task FailHtlcAsync(ChannelId channelId, ulong htlcId, ReadOnlyMemory<byte> reason,
                                    CancellationToken cancellationToken = default)
    {
        await RunAsync(channelId, "update_fail_htlc", c => c.SendFail(htlcId, reason.ToArray()),
                       cancellationToken);
    }

    /// <inheritdoc />
    public async Task FailHtlcAsync(ChannelId channelId, ulong htlcId, AttributedErrorPacket errorPacket,
                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(errorPacket);
        await RunAsync(channelId, "update_fail_htlc",
                       c => c.SendFail(htlcId, errorPacket.Reason.ToArray(), errorPacket.AttributionData.ToArray()),
                       cancellationToken);
    }

    /// <inheritdoc />
    public async Task<uint> GetHoldTimeAsync(ChannelId channelId, ulong htlcId,
                                             CancellationToken cancellationToken = default)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var addedAt = await unitOfWork.ChannelStateDbRepository.GetHtlcAddedAtAsync(
                          channelId, new HtlcKey(HtlcDirection.Incoming, htlcId));

        return addedAt is { } received ? AttributionHoldTime.FromDuration(_timeProvider.GetUtcNow() - received) : 0;
    }

    /// <inheritdoc />
    public async Task FailMalformedHtlcAsync(ChannelId channelId, ulong htlcId, FailureCode failureCode,
                                             Hash sha256OfOnion, CancellationToken cancellationToken = default)
    {
        await RunAsync(channelId, "update_fail_malformed_htlc",
                       c => c.SendFailMalformed(htlcId, (ushort)failureCode, (byte[])sha256OfOnion),
                       cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpdateFeeAsync(ChannelId channelId, uint feeratePerKw,
                                     CancellationToken cancellationToken = default)
    {
        await RunAsync(channelId, FeeOperation, c => c.SendFee(feeratePerKw), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Idempotent: storing the same secret again only rewrites it.</remarks>
    public async Task RecordOnionSecretAsync(ChannelId channelId, ulong htlcId, Secret sharedSecret,
                                             CancellationToken cancellationToken = default)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        using var channelLock = await _channelLockProvider.AcquireAsync(channelId, cancellationToken);

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            throw new KeyNotFoundException($"Channel {channelId} is not loaded");
        var htlcKey = new HtlcKey(HtlcDirection.Incoming, htlcId);
        if (channel.Commitments?.GetHtlc(HtlcDirection.Incoming, htlcId) is null)
            throw new CommitmentRefusedException("B2-DEL-00", $"No HTLC {htlcId} offered by the peer");

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.ChannelStateDbRepository.SetOnionSharedSecretAsync(channelId, htlcKey, sharedSecret);
        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>
    /// One update: lock, preconditions, engine, persist, enqueue; then (outside the lock) schedule the signature and
    /// hand the transition's events to the switch.
    /// </summary>
    private async Task<CommitmentsResult> RunAsync(ChannelId channelId, string operationName,
                                                   Func<ChannelCommitments, CommitmentsResult> operation,
                                                   CancellationToken cancellationToken,
                                                   Func<IUnitOfWork, CommitmentsResult, Task>? stageWithTransition = null)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        CommitmentsResult result;
        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            var channel = GetOperableChannel(channelId, operationName);
            ThrowIfQuiescing(channelId, operationName);
            if (!await _peerLivenessProbe.IsAliveAsync(channelId, channel.RemoteNodeId, cancellationToken))
                throw new CommitmentRefusedException("B2-NO-02",
                                                     $"{operationName} refused: the peer of channel {channelId} is not connected on the channel's link");

            // The engine throws CommitmentRefusedException for a broken sender rule; nothing is persisted then
            result = operation(channel.Commitments!);

            var transitions = scope.ServiceProvider.GetRequiredService<ChannelStateTransitionService>();
            await transitions.CommitAsync(channel, result, null,
                                          stageWithTransition is null
                                              ? null
                                              : unitOfWork => stageWithTransition(unitOfWork, result));
            _channelMessagePublisher.Publish(channel.RemoteNodeId,
                                             result.Outbound.Select(o => transitions.ToWireMessage(channel, o))
                                                   .ToList());
        }

        _commitScheduler.Schedule(channelId);
        await RaiseDomainEventsAsync(scope);
        return result;
    }

    private async Task FulfillAsync(ChannelId channelId, ulong htlcId, Secret paymentPreimage,
                                    Func<IUnitOfWork, CommitmentsResult, Task>? stageWithTransition,
                                    CancellationToken cancellationToken, byte[]? attributionData = null,
                                    byte[]? fulfillmentPayload = null)
    {
        await RunAsync(channelId, "update_fulfill_htlc", c =>
        {
            using var sha256 = new Sha256();
            return c.SendFulfill(htlcId, paymentPreimage, sha256, attributionData?.ToArray() ?? [],
                                 fulfillmentPayload?.ToArray() ?? []);
        }, cancellationToken, stageWithTransition);
    }

    /// <summary>The key of the HTLC an <c>update_add_htlc</c> transition added.</summary>
    private static HtlcKey AddedHtlcKey(CommitmentsResult result) =>
        result.Outbound.OfType<OutboundAddHtlc>().Single().Htlc.Key;

    /// <summary>The channel an operation may change (see the class remarks).</summary>
    private ChannelModel GetOperableChannel(ChannelId channelId, string operationName)
    {
        if (!_nodeOptions.HtlcsEnabled)
            throw new CommitmentRefusedException("B2-NO-02", $"{operationName} refused: HTLCs are disabled on this node");

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            throw new KeyNotFoundException($"Channel {channelId} is not loaded");

        if (channel.State == ChannelState.Failed)
            throw new CommitmentRefusedException("B2-NO-02", $"{operationName} refused: channel {channelId} failed");

        if (!ChannelStateTransitionService.CarriesUpdates(channel.State))
            throw new CommitmentRefusedException("B2-NO-02",
                                                 $"{operationName} refused: channel {channelId} is {Enum.GetName(channel.State)}");

        // BOLT 2 after shutdown: no new HTLC (B2-ADD-S13), and once no HTLC is left no update at all (B2-SHUT-S07)
        if (channel.State == ChannelState.ShuttingDown && operationName == AddOperation)
            throw new CommitmentRefusedException("B2-ADD-S13",
                                                 $"{operationName} refused: channel {channelId} is shutting down");
        if (channel.State == ChannelState.ShuttingDown && operationName == FeeOperation
         && channel.Commitments is { Htlcs.IsEmpty: true })
            throw new CommitmentRefusedException("B2-SHUT-S07",
                                                 $"{operationName} refused: channel {channelId} is shutting down with no HTLC left");

        if (channel.DataLossDetected)
            throw new CommitmentRefusedException("B2-RE-DL",
                                                 $"{operationName} refused: channel {channelId} lost data");

        if (channel.Commitments is null)
            throw new CommitmentRefusedException("B2-NO-02",
                                                 $"{operationName} refused: channel {channelId} has no commitment state");

        return channel;
    }

    /// <summary>
    /// Q-S-04 / Q-R-02: no update of ours while the channel is quiescing or quiescent (checked under the lock, where the
    /// quiescence state changes).
    /// </summary>
    private void ThrowIfQuiescing(ChannelId channelId, string operationName)
    {
        if (_quiescenceService?.GetState(channelId) is not { BlocksNewLocalUpdates: true } state)
            return;

        var requirementId = state.StfuSent ? "Q-S-04" : "Q-R-02";
        throw new ChannelQuiescentException(channelId, requirementId,
                                            $"{operationName} refused: channel {channelId} is "
                                          + (state.IsQuiescent ? "quiescent" : "quiescing"));
    }

    /// <summary>
    /// Sends are not expected to raise events, but anything a transition raised goes to the switch like after a peer
    /// message: outside the lock, logged on failure (re-derived on startup).
    /// </summary>
    private async Task RaiseDomainEventsAsync(IServiceScope scope)
    {
        var events = scope.ServiceProvider.GetRequiredService<ChannelDomainEventQueue>().Drain();
        if (events.Count == 0 || scope.ServiceProvider.GetService<IHtlcSwitch>() is not { } htlcSwitch)
            return;

        foreach (var channelEvent in events)
        {
            try
            {
                await htlcSwitch.HandleAsync(channelEvent, CancellationToken.None);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "HTLC switch failed on {Event} for HTLC {HtlcId} of channel {ChannelId}",
                                 channelEvent.GetType().Name, channelEvent.HtlcId, channelEvent.ChannelId);
            }
        }
    }
}
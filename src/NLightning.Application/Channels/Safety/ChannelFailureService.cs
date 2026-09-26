using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Channels.Safety;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// The fail-the-channel service (BOLT2 plan N9-T4, D10; partial NL-094): persist <see cref="ChannelState.Failed"/> and
/// the <c>error</c>, build and fully sign our latest local commitment, publish it, send the <c>error</c>; and, once the
/// commitment confirms, persist <see cref="ChannelState.Closed"/>.
/// </summary>
/// <remarks>
/// <para>Singleton; see <see cref="IChannelFailureService"/> for the contract. Invariants: only the latest local
/// commitment is ever signed for broadcast (it is read from the snapshot under the channel's lock, and the signer
/// refuses an older number; I4); nothing is broadcast after proven data loss (checked here and by the signer after
/// <c>MarkDataLoss</c>; I12, B2-RE-23).</para>
/// <para>Persist before broadcast: Failed + error are saved (under the lock) before the commitment is built, so no
/// later update can change it (every update is refused on a Failed channel); the watch of the commitment txid is
/// saved by <see cref="IBlockchainMonitor.PublishAndWatchTransactionAsync"/> before the publish. A broadcast
/// repeated in the same process (a new block while the HTLC is still past its deadline) is skipped; after a restart
/// the stored watch makes it a rebroadcast (<see cref="IBitcoinChainService.SendTransactionAsync"/>).</para>
/// </remarks>
public sealed class ChannelFailureService : IChannelFailureService, IDisposable
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelErrorSender _channelErrorSender;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly LocalCommitmentBroadcastBuilder _commitmentBuilder;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ChannelFailureService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ChannelSafetyOptions _options;
    private readonly Network _network;

    // Commitments published by this process, per channel (a repeated failure does not republish every block)
    private readonly ConcurrentDictionary<ChannelId, TxId> _published = new();

    private int _started;

    public ChannelFailureService(IBlockchainMonitor blockchainMonitor, IChannelErrorSender channelErrorSender,
                                 IChannelLockProvider channelLockProvider,
                                 IChannelMemoryRepository channelMemoryRepository,
                                 LocalCommitmentBroadcastBuilder commitmentBuilder, ILightningSigner lightningSigner,
                                 ILogger<ChannelFailureService> logger, IServiceScopeFactory serviceScopeFactory,
                                 IServiceProvider serviceProvider, IOptions<NodeOptions> nodeOptions,
                                 IOptions<ChannelSafetyOptions>? safetyOptions = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _channelErrorSender = channelErrorSender;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _commitmentBuilder = commitmentBuilder;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _serviceProvider = serviceProvider;
        _options = safetyOptions?.Value ?? new ChannelSafetyOptions();
        _network = Network.GetNetwork(nodeOptions.Value.BitcoinNetwork) ?? Network.RegTest;
    }

    /// <inheritdoc />
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _blockchainMonitor.OnTransactionConfirmed += HandleTransactionConfirmed;
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) == 1)
            _blockchainMonitor.OnTransactionConfirmed -= HandleTransactionConfirmed;
    }

    /// <inheritdoc />
    public Task<ChannelFailureOutcome> FailChannelAsync(ChannelFailedException failure,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return FailChannelAsync(failure.FailedChannelId,
                                new ChannelFailureRequest(failure.Message,
                                                          failure.PeerMessage
                                                       ?? ChannelFailedException.DefaultPeerMessage,
                                                          failure.MustBroadcast, failure.RequirementId),
                                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ChannelFailureOutcome> FailChannelAsync(ChannelId channelId, ChannelFailureRequest request,
                                                              CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ErrorMessage error;
        ChannelModel channel;
        SignedLocalCommitment? commitment = null;
        ChannelFailureOutcome? outcome = null;

        using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var loaded))
                throw new KeyNotFoundException($"Channel {channelId} is not loaded");

            channel = loaded;
            if (channel.State is ChannelState.Closed or ChannelState.Stale
             || channel.State < ChannelState.V1FundingSigned)
                return new ChannelFailureOutcome(ChannelFailureStatus.NotApplicable, null);

            _logger.LogCritical("Failing channel {ChannelId} ({RequirementId}): {Reason}; broadcast: {Broadcast}",
                                channelId, request.RequirementId, request.Reason, request.Broadcast);

            using var scope = _serviceScopeFactory.CreateScope();
            error = await PersistFailedAsync(scope, channel, request);

            if (channel.DataLossDetected)
            {
                // I12 / B2-RE-23: the peer holds a newer state; our commitment is revoked from its point of view
                _lightningSigner.MarkDataLoss(channelId);
                if (request.Broadcast)
                    _logger.LogCritical("Not broadcasting the commitment of channel {ChannelId}: data loss was "
                                      + "detected, the peer must close it", channelId);
                outcome = new ChannelFailureOutcome(ChannelFailureStatus.RefusedDataLoss, null);
            }
            else if (!request.Broadcast)
            {
                outcome = new ChannelFailureOutcome(ChannelFailureStatus.FailedWithoutBroadcast, null);
            }
            else
            {
                try
                {
                    // Built under the lock from the snapshot that is now frozen (Failed refuses every update)
                    commitment = _commitmentBuilder.Build(channel);
                }
                catch (Exception e) when (e is InvalidOperationException or SignerException)
                {
                    _logger.LogCritical(e, "Cannot build a broadcastable commitment for failed channel {ChannelId}",
                                        channelId);
                    outcome = new ChannelFailureOutcome(ChannelFailureStatus.NoBroadcastableCommitment, null);
                }
            }
        }

        if (commitment is not null)
            outcome = await PublishAsync(channelId, commitment);

        await _channelErrorSender.TrySendAsync(channel.RemoteNodeId, error);
        return outcome!;
    }

    /// <summary>The txid this process published for <paramref name="channelId"/>, if any (tests, diagnostics).</summary>
    public bool TryGetPublishedCommitment(ChannelId channelId, out TxId txId) =>
        _published.TryGetValue(channelId, out txId);

    public void Dispose() => Stop();

    private async Task<ErrorMessage> PersistFailedAsync(IServiceScope scope, ChannelModel channel,
                                                        ChannelFailureRequest request)
    {
        var messageSerializer = scope.ServiceProvider.GetRequiredService<IMessageSerializer>();

        // Already failed with a stored error (a handler's ChannelFailedException, or an earlier call): keep it
        if (channel.State == ChannelState.Failed && channel.ErrorSent is { } stored)
        {
            try
            {
                using var storedStream = new MemoryStream(stored.ToArray(), false);
                if (await messageSerializer.DeserializeMessageAsync(storedStream) is ErrorMessage storedError)
                    return storedError;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "The stored error of channel {ChannelId} can't be read", channel.ChannelId);
            }
        }

        var messageFactory = scope.ServiceProvider.GetRequiredService<IMessageFactory>();
        var error = messageFactory.CreateErrorMessage(request.PeerMessage, channel.ChannelId);
        if (channel.State == ChannelState.Failed && channel.ErrorSent is not null)
            return error;

        using var errorStream = new MemoryStream();
        await messageSerializer.SerializeAsync(error, errorStream);

        if (channel.State < ChannelState.Failed)
            channel.UpdateState(ChannelState.Failed);
        channel.MarkErrorSent(errorStream.ToArray());

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
        await unitOfWork.SaveChangesAsync();
        _channelMemoryRepository.UpdateChannel(channel);
        return error;
    }

    private async Task<ChannelFailureOutcome> PublishAsync(ChannelId channelId, SignedLocalCommitment commitment)
    {
        var txId = commitment.Transaction.TxId;
        if (_published.TryGetValue(channelId, out var published) && published == txId)
            return new ChannelFailureOutcome(ChannelFailureStatus.Rebroadcast, txId);

        try
        {
            bool watched;
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                watched = await unitOfWork.WatchedTransactionDbRepository.GetByTransactionIdAsync(txId) is not null;
            }

            if (watched)
            {
                // Published before (an earlier run): send it again, the watch is already stored
                var chainService = _serviceProvider.GetRequiredService<IBitcoinChainService>();
                try
                {
                    await chainService.SendTransactionAsync(Transaction.Load(commitment.Transaction.RawTxBytes,
                                                                             _network));
                }
                catch (Exception e)
                {
                    // Already in the mempool or a block: nothing more to do
                    _logger.LogInformation(e, "Rebroadcast of commitment {TxId} of channel {ChannelId} was refused",
                                           txId, channelId);
                }

                _published[channelId] = txId;
                return new ChannelFailureOutcome(ChannelFailureStatus.Rebroadcast, txId);
            }

            await _blockchainMonitor.PublishAndWatchTransactionAsync(channelId, commitment.Transaction,
                                                                     Math.Max(1, _options.CommitmentConfirmationDepth));
            _published[channelId] = txId;
            _logger.LogCritical("Broadcast local commitment {CommitmentNumber} ({TxId}, {HtlcOutputs} HTLC outputs) "
                              + "of failed channel {ChannelId}; its outputs are not swept yet (BOLT 5, NL-094)",
                                commitment.CommitmentNumber, txId, commitment.HtlcOutputCount, channelId);
            return new ChannelFailureOutcome(ChannelFailureStatus.Broadcast, txId);
        }
        catch (Exception e)
        {
            _logger.LogCritical(e, "Publishing commitment {TxId} of failed channel {ChannelId} failed", txId,
                                channelId);
            return new ChannelFailureOutcome(ChannelFailureStatus.PublishFailed, txId);
        }
    }

    private void HandleTransactionConfirmed(object? sender, TransactionConfirmedEventArgs args)
    {
        var watched = args.WatchedTransaction;
        if (!_channelMemoryRepository.TryGetChannel(watched.ChannelId, out var channel)
         || channel.State != ChannelState.Failed || IsFundingTransaction(channel, watched.TransactionId))
            return;

        _ = CloseAsync(watched.ChannelId, watched.TransactionId, args.Height);
    }

    private static bool IsFundingTransaction(ChannelModel channel, TxId txId) =>
        channel.FundingOutput?.TransactionId is { } fundingTxId && fundingTxId == txId;

    /// <summary>
    /// Our commitment (a watched transaction of a Failed channel other than its funding) confirmed: the channel is
    /// closed. Its outputs still need BOLT 5 (NL-094).
    /// </summary>
    private async Task CloseAsync(ChannelId channelId, TxId txId, uint height)
    {
        try
        {
            using var channelLock = await _channelLockProvider.AcquireAsync(channelId);
            if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel)
             || channel.State != ChannelState.Failed)
                return;

            channel.UpdateState(ChannelState.Closed);
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await unitOfWork.ChannelDbRepository.UpdateAsync(channel);
            await unitOfWork.SaveChangesAsync();
            _channelMemoryRepository.UpdateChannel(channel);

            _logger.LogWarning("Channel {ChannelId} closed: our commitment {TxId} confirmed at height {Height}; "
                             + "to_local and HTLC outputs are not swept (BOLT 5, NL-094)", channelId, txId, height);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to persist the close of channel {ChannelId}", channelId);
        }
    }
}
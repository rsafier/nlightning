using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Safety;

using Domain.Bitcoin.Events;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Policies;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Payments.Onion;

/// <summary>
/// The block-driven BOLT 2 HTLC deadline monitor (BOLT2 plan N9-T2; B2-CLTV-03/05/06, B2-FWD-03). On every new block
/// it evaluates <see cref="HtlcDeadlinePolicy"/> for every HTLC of every loaded <c>Open</c> or <c>Failed</c> channel
/// with a commitment snapshot:
/// <list type="bullet">
///   <item>an HTLC that must be resolved on chain (offered past <c>cltv_expiry + G</c>, fulfilled or preimage-known
///   past <c>cltv_expiry - 18</c>) fails the channel through <see cref="IChannelFailureService"/> (broadcast);</item>
///   <item>an unresolved incoming HTLC (no preimage, nothing downstream) at <c>cltv_expiry - FailBackBlocks</c> is
///   failed back upstream through <see cref="IChannelOperations.FailHtlcAsync"/> with <c>temporary_node_failure</c>
///   (or <c>update_fail_malformed_htlc</c> when its onion does not peel), encrypted with its stored shared secret.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>Singleton, outside every channel lock: it reads the immutable snapshot of each channel, and changes channels
/// only through <see cref="IChannelOperations"/> and <see cref="IChannelFailureService"/>, which take the lock. An
/// incoming HTLC's resolution (<see cref="IncomingHtlcResolution"/>) comes from the forward circuit, the outgoing HTLC
/// that carries its origin (its <see cref="HtlcRecord.KnownPreimage"/>) and our invoice for the hash; it is looked up
/// only for HTLCs whose deadline is near. An HTLC continued downstream is never failed upstream here (the outgoing
/// HTLC's own deadline protects it). Rounds never overlap; blocks that arrive during a round are coalesced into one
/// more round at the latest height.</para>
/// <para>A channel failed by this monitor is not failed again in the same process unless its publish failed (then
/// every block retries). A refused fail-back (peer away, not reestablished) is retried on the next block.</para>
/// <para>Known limits: an incoming HTLC whose invoice was settled by another HTLC of the same hash counts as
/// preimage-known (it fails the channel at the fulfillment deadline instead of failing back; the switch normally fails
/// it at lock-in); the switch could start a forward between our circuit lookup and our fail-back only if it read a
/// height at least <c>cltv_expiry_delta + ExpiryTooSoonBlocks</c> blocks older than ours.</para>
/// </remarks>
public sealed class HtlcExpiryMonitor : IHtlcExpiryMonitor, IDisposable
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IChannelFailureService _channelFailureService;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelOperations _channelOperations;
    private readonly IFailureOnionService _failureOnionService;
    private readonly IncomingOnionProcessor? _incomingOnionProcessor;
    private readonly ILogger<HtlcExpiryMonitor> _logger;
    private readonly HtlcDeadlinePolicy _policy;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    // Channels failed by this monitor in this process (not re-failed every block)
    private readonly ConcurrentDictionary<ChannelId, byte> _failedChannels = new();
    private readonly object _roundGate = new();

    private CancellationTokenSource _stopping = new();
    private Task _roundLoop = Task.CompletedTask;
    private uint _pendingHeight;
    private bool _roundScheduled;
    private int _started;

    public HtlcExpiryMonitor(IBlockchainMonitor blockchainMonitor, IChannelFailureService channelFailureService,
                             IChannelMemoryRepository channelMemoryRepository, IChannelOperations channelOperations,
                             IFailureOnionService failureOnionService, ILogger<HtlcExpiryMonitor> logger,
                             IOptions<NodeOptions> nodeOptions, IServiceScopeFactory serviceScopeFactory,
                             IOptions<ChannelSafetyOptions>? safetyOptions = null,
                             IncomingOnionProcessor? incomingOnionProcessor = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _channelFailureService = channelFailureService;
        _channelMemoryRepository = channelMemoryRepository;
        _channelOperations = channelOperations;
        _failureOnionService = failureOnionService;
        _incomingOnionProcessor = incomingOnionProcessor;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _policy = (safetyOptions?.Value ?? new ChannelSafetyOptions()).CreatePolicy(nodeOptions.Value.Routing);
    }

    /// <summary>The deadlines this monitor applies.</summary>
    public HtlcDeadlinePolicy Policy => _policy;

    /// <inheritdoc />
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;

        lock (_roundGate)
            _stopping = new CancellationTokenSource();

        _blockchainMonitor.OnNewBlockDetected += HandleNewBlockDetected;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("HTLC expiry monitor started (G {Grace}, fulfill deadline {Fulfill}, fail back "
                                 + "{FailBack} blocks before expiry)", _policy.GraceBlocks,
                                   _policy.FulfillSafetyBlocks, _policy.FailBackBlocks);
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return;

        _blockchainMonitor.OnNewBlockDetected -= HandleNewBlockDetected;
        Task loop;
        lock (_roundGate)
        {
            _stopping.Cancel();
            loop = _roundLoop;
        }

        try
        {
            await loop;
        }
        catch (OperationCanceledException)
        {
            // Stopping
        }
    }

    /// <summary>Waits until no round is running or scheduled (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task loop;
            lock (_roundGate)
                loop = _roundLoop;

            await loop;
            lock (_roundGate)
            {
                if (!_roundScheduled && _roundLoop.IsCompleted)
                    return;
            }
        }
    }

    /// <inheritdoc />
    public async Task CheckAsync(uint height, CancellationToken cancellationToken = default)
    {
        var channels = _channelMemoryRepository.FindChannels(c => c.Commitments is not null
                                                               && c.State is ChannelState.Open
                                                                             or ChannelState.Failed);
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await CheckChannelAsync(channel, height, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "HTLC deadline check of channel {ChannelId} at height {Height} failed",
                                 channel.ChannelId, height);
            }
        }
    }

    public void Dispose()
    {
        _blockchainMonitor.OnNewBlockDetected -= HandleNewBlockDetected;
        lock (_roundGate)
            _stopping.Cancel();
    }

    private void HandleNewBlockDetected(object? sender, NewBlockEventArgs args)
    {
        lock (_roundGate)
        {
            if (args.Height > _pendingHeight)
                _pendingHeight = args.Height;

            if (_roundScheduled || _stopping.IsCancellationRequested)
                return;

            _roundScheduled = true;
            var token = _stopping.Token;

            // Never block the chain monitor: rounds run on the thread pool, one at a time
            _roundLoop = Task.Run(() => RunRoundsAsync(token), CancellationToken.None);
        }
    }

    private async Task RunRoundsAsync(CancellationToken cancellationToken)
    {
        uint done = 0;
        while (true)
        {
            uint height;
            lock (_roundGate)
            {
                if (_pendingHeight == done || cancellationToken.IsCancellationRequested)
                {
                    _roundScheduled = false;
                    return;
                }

                height = _pendingHeight;
            }

            try
            {
                await CheckAsync(height, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_roundGate)
                    _roundScheduled = false;
                return;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "HTLC deadline round at height {Height} failed", height);
            }

            done = height;
        }
    }

    private async Task CheckChannelAsync(ChannelModel channel, uint height, CancellationToken cancellationToken)
    {
        var commitments = channel.Commitments;
        if (commitments is null)
            return;

        (HtlcRecord Htlc, HtlcDeadlineDecision Decision)? channelFailure = null;
        var failBacks = new List<HtlcRecord>();
        IServiceScope? scope = null;
        try
        {
            foreach (var htlc in commitments.Htlcs.Values)
            {
                var resolution = IncomingHtlcResolution.Unresolved;
                if (_policy.NeedsIncomingResolution(htlc, height))
                {
                    scope ??= _serviceScopeFactory.CreateScope();
                    resolution = await ResolveIncomingAsync(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                                                            channel.ChannelId, htlc);
                }

                var decision = _policy.Evaluate(htlc, height, resolution);
                switch (decision.Action)
                {
                    case HtlcDeadlineAction.FailChannel:
                        channelFailure ??= (htlc, decision);
                        break;
                    case HtlcDeadlineAction.FailBackUpstream:
                        failBacks.Add(htlc);
                        break;
                }
            }

            if (channelFailure is { } failure)
            {
                await FailChannelAsync(channel, failure.Htlc, failure.Decision, height, cancellationToken);
                return;
            }

            if (channel.State != ChannelState.Open || failBacks.Count == 0)
                return;

            scope ??= _serviceScopeFactory.CreateScope();
            foreach (var htlc in failBacks)
                await FailBackAsync(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), channel.ChannelId, htlc,
                                    height, cancellationToken);
        }
        finally
        {
            scope?.Dispose();
        }
    }

    private async Task FailChannelAsync(ChannelModel channel, HtlcRecord htlc, HtlcDeadlineDecision decision,
                                        uint height, CancellationToken cancellationToken)
    {
        if (_failedChannels.ContainsKey(channel.ChannelId))
            return;

        var reason = $"{Enum.GetName(htlc.Direction)} HTLC {htlc.Id} (cltv_expiry {htlc.CltvExpiry}, "
                   + $"{Enum.GetName(htlc.State)}) passed its deadline {decision.DeadlineHeight} at height {height}";
        var outcome = await _channelFailureService.FailChannelAsync(
                          channel.ChannelId,
                          new ChannelFailureRequest(reason, "htlc timed out", Broadcast: true, decision.RequirementId),
                          cancellationToken);

        // A failed publish is retried on the next block; everything else is final for this process
        if (outcome.Status != ChannelFailureStatus.PublishFailed)
            _failedChannels.TryAdd(channel.ChannelId, 0);

        _logger.LogCritical("Channel {ChannelId} failed ({RequirementId}): {Reason}; {Status} {TxId}",
                            channel.ChannelId, decision.RequirementId, reason, outcome.Status,
                            outcome.CommitmentTxId);
    }

    /// <summary>
    /// What an incoming, locked-in, not removed HTLC is waiting for (see <see cref="IncomingHtlcResolution"/>).
    /// </summary>
    private async Task<IncomingHtlcResolution> ResolveIncomingAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                                    HtlcRecord htlc)
    {
        var circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(channelId, htlc.Id);
        if (circuit is { Status: ForwardCircuitStatus.Fulfilled })
            return IncomingHtlcResolution.PreimageKnown;

        if (circuit is { Status: ForwardCircuitStatus.Failed })
            return IncomingHtlcResolution.Unresolved;

        var outgoing = await unitOfWork.ChannelStateDbRepository
                                       .FindHtlcsByOriginAsync(HtlcOrigin.Forwarded(channelId, htlc.Id));
        var outgoingKeys = outgoing.ToList();
        if (circuit is { OutgoingChannelId: { } outChannel, OutgoingHtlcId: { } outId })
            outgoingKeys.Add((outChannel, new HtlcKey(HtlcDirection.Outgoing, outId)));

        foreach (var (outgoingChannelId, outgoingKey) in outgoingKeys)
        {
            if (_channelMemoryRepository.TryGetChannel(outgoingChannelId, out var outgoingChannel)
             && outgoingChannel.Commitments?.GetHtlc(outgoingKey.Direction, outgoingKey.Id) is
             { KnownPreimage: not null })
                return IncomingHtlcResolution.PreimageKnown;
        }

        // A forward in progress (Pending/Offered circuit, or an outgoing HTLC with its origin): the downstream decides
        if (circuit is not null || outgoingKeys.Count > 0)
            return IncomingHtlcResolution.AwaitingDownstream;

        // Final hop: our invoice for the hash was accepted/settled, so we hold the preimage; otherwise the switch may
        // still settle it, so it gets the final-hop (fulfillment) deadline, not the forwarding distance
        var invoice = await unitOfWork.InvoiceDbRepository.GetByPaymentHashAsync(htlc.PaymentHash);
        return invoice is { Status: InvoiceStatus.Accepted or InvoiceStatus.Settled }
                   ? IncomingHtlcResolution.PreimageKnown
                   : IncomingHtlcResolution.UnresolvedFinalHop;
    }

    private async Task FailBackAsync(IUnitOfWork unitOfWork, ChannelId channelId, HtlcRecord htlc, uint height,
                                     CancellationToken cancellationToken)
    {
        try
        {
            var secret = await unitOfWork.ChannelStateDbRepository.GetOnionSharedSecretAsync(channelId, htlc.Key);
            if (secret is null && _incomingOnionProcessor is not null && !htlc.OnionRoutingPacket.IsEmpty)
            {
                // Never processed (e.g. before a restart): peel again, without the replay check
                var processed = await _incomingOnionProcessor.ProcessAsync(htlc.OnionRoutingPacket, htlc.PaymentHash,
                                                                           replayOwner: null, htlc.PathKey);
                if (processed is IncomingOnionMalformed malformed)
                {
                    await _channelOperations.FailMalformedHtlcAsync(channelId, htlc.Id, malformed.FailureCode,
                                                                    new Hash(malformed.Sha256OfOnion.ToArray()),
                                                                    cancellationToken);
                    LogFailedBack(channelId, htlc, height, "update_fail_malformed_htlc");
                    return;
                }

                secret = processed.SharedSecretOrNull;
            }

            if (secret is not { } sharedSecret)
            {
                _logger.LogError("Cannot fail back HTLC {HtlcId} of channel {ChannelId} before its expiry "
                               + "{CltvExpiry}: no onion shared secret", htlc.Id, channelId, htlc.CltvExpiry);
                return;
            }

            var reason = _failureOnionService.CreateErrorPacket(sharedSecret, FailureMessage.TemporaryNodeFailure());
            await _channelOperations.FailHtlcAsync(channelId, htlc.Id, reason, cancellationToken);
            LogFailedBack(channelId, htlc, height, "temporary_node_failure");
        }
        catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
        {
            // Peer away, not reestablished, or already removed: retried on the next block
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Fail-back of HTLC {HtlcId} of channel {ChannelId} refused ({Reason}); retrying "
                                     + "on the next block", htlc.Id, channelId, e.Message);
        }
    }

    private void LogFailedBack(ChannelId channelId, HtlcRecord htlc, uint height, string how)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning("Failed back HTLC {HtlcId} of channel {ChannelId} with {How}: cltv_expiry {CltvExpiry}, "
                             + "height {Height} (fail-back {FailBack} blocks before expiry)", htlc.Id, channelId, how,
                               htlc.CltvExpiry, height, _policy.FailBackBlocks);
    }
}
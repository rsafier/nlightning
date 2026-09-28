using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Onchain.Fees;

using Channels.Splicing;
using Channels.Splicing.Interfaces;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The splice auto-bump (wave SPR, SPR-T3, lane SPR-B): on each block, RBFs every pending splice of ours whose latest
/// attempt has waited <see cref="SpliceOptions.AutoBumpAfterBlocks"/> blocks, through
/// <see cref="ISpliceService.BumpAsync(SpliceBumpRequest, CancellationToken)"/> at the fee service's estimate, at least
/// the IT-RBF-01 minimum of the latest attempt (<see cref="InteractiveTxRbfRules.GetMinimumNextFeerate"/>). Off unless
/// <c>Splice:AutoBumpAfterBlocks</c> is set (null or 0 = off).
/// </summary>
/// <remarks>
/// <para>A splice is bumped when, at the round's height: the channel is <see cref="ChannelState.Open"/>; its latest
/// pending attempt (<see cref="FundingSet.LatestAttempt"/>) is unconfirmed, as are all its siblings, and no
/// <c>splice_locked</c> was sent for any of them; the splice is ours (our side contributed to it, a non-zero
/// <see cref="ChannelFunding.LocalBalanceDeltaMsat"/>, or the service's last negotiation on the channel is ours and
/// named one of the attempts); it has fewer than <see cref="SpliceOptions.MaxRbfAttempts"/> RBF attempts; no splice
/// negotiation runs on the channel and no bump of ours is running; and the latest attempt has waited
/// <c>AutoBumpAfterBlocks</c> blocks since it was first broadcast (its <c>BroadcastTransactions</c> row's
/// <c>FirstBroadcastHeight</c>; without a row, the height this bumper first saw it) and since our last bump attempt on
/// it. So a splice is bumped at most once per interval: a bump the peer refuses (or that finds the peer offline) is
/// tried again one interval later, and a bump that succeeds starts the interval of the new attempt.</para>
/// <para>Spending limits (the operator's, not the acceptor bound <see cref="SpliceOptions.MaxFeeratePerKw"/>): the
/// feerate is clamped to <see cref="SpliceOptions.AutoBumpMaxFeeratePerKw"/>; a splice whose IT-RBF-01 minimum is
/// already above it is not bumped (logged once per interval, left to <c>bumpsplice</c>); every request carries
/// <see cref="SpliceOptions.AutoBumpMaxFeeSat"/> as its fee cap. A round waits for each bump at most
/// <see cref="SpliceOptions.AutoBumpMaxWait"/>; a slower negotiation goes on (reported with its current state) and its
/// channel is skipped until it ends, so one slow peer never delays the other channels. The service's rules (<c>SpliceRules.CheckSendRbf</c>) still decide: a refusal is reported as an
/// <see cref="SpliceNegotiationState.Aborted"/> result with its reason, never thrown.</para>
/// <para>Lifecycle: <see cref="Start"/> after the chain monitor and the peer manager are started,
/// <see cref="StopAsync"/> before the chain monitor is stopped (waits for a running round). Rounds run one at a time on
/// their own task and coalesce, as <c>OnionReplayBlockPruner</c>'s: blocks that arrive while one runs are covered by a
/// single round at the highest height seen. Registered by
/// <see cref="SpliceAutoBumperServiceCollectionExtensions.AddSpliceAutoBumper"/>.</para>
/// </remarks>
public sealed class SpliceAutoBumper : ISpliceAutoBumper
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ISpliceService _spliceService;
    private readonly ISpliceStatePort _statePort;
    private readonly IFeeService _feeService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SpliceAutoBumper> _logger;
    private readonly SpliceOptions _options;
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly Lock _gate = new();

    // Under _gate
    private readonly Dictionary<TxId, uint> _firstSeen = [];
    private readonly Dictionary<ChannelId, (TxId LatestAttempt, uint Height)> _lastBump = [];
    private readonly HashSet<ChannelId> _running = [];
    private readonly HashSet<Task<SpliceResult>> _inFlight = [];
    private uint _lastRoundHeight;
    private CancellationTokenSource? _stopping;
    private Task _round = Task.CompletedTask;
    private uint _requestedHeight;
    private bool _roundScheduled;

    public SpliceAutoBumper(IChannelMemoryRepository channelMemoryRepository, ISpliceService spliceService,
                            ISpliceStatePort statePort, IFeeService feeService, IServiceProvider serviceProvider,
                            ILogger<SpliceAutoBumper> logger, IOptions<SpliceOptions>? options = null,
                            IBlockchainMonitor? blockchainMonitor = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _spliceService = spliceService;
        _statePort = statePort;
        _feeService = feeService;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options?.Value ?? new SpliceOptions();
        _blockchainMonitor = blockchainMonitor;
    }

    /// <summary>Whether the auto-bump is on (<see cref="SpliceOptions.AutoBumpAfterBlocks"/> set and above 0).</summary>
    public bool IsEnabled => _options.AutoBumpAfterBlocks is > 0;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpliceResult>> BumpStaleSplicesAsync(uint height,
                                                                         CancellationToken cancellationToken = default)
    {
        if (_options.AutoBumpAfterBlocks is not ({ } interval and > 0))
            return [];

        lock (_gate)
        {
            if (height <= _lastRoundHeight)
                return [];

            _lastRoundHeight = height;
        }

        var channels = _channelMemoryRepository.FindChannels(c => c.State == ChannelState.Open);
        var due = new List<(ChannelId ChannelId, ChannelFunding LatestAttempt, uint Waited)>();
        var latestAttempts = new HashSet<TxId>();
        using (var scope = _serviceProvider.CreateScope())
        {
            var unitOfWork = scope.ServiceProvider.GetService<IUnitOfWork>();
            foreach (var channel in channels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await FindDueAttemptAsync(channel, height, interval, unitOfWork, latestAttempts) is { } attempt)
                    due.Add((channel.ChannelId, attempt.LatestAttempt, attempt.Waited));
            }
        }

        Forget(latestAttempts);
        if (due.Count == 0)
            return [];

        // One estimate per round, read only when a splice is due
        var estimate = await GetEstimateAsync(cancellationToken);
        var bumps = new List<Task<SpliceResult>>(due.Count);
        var ceiling = _options.AutoBumpMaxFeeratePerKw;
        foreach (var (channelId, latest, waited) in due)
        {
            var minimum =
                InteractiveTxRbfRules.GetMinimumNextFeerate(latest.FeeratePerKw ?? _options.MinFeeratePerKw);
            if (minimum > ceiling)
            {
                // Counted as a try, so the warning comes once per interval, not on every block
                lock (_gate)
                    _lastBump[channelId] = (latest.FundingTxId, height);

                _logger.LogWarning("Splice {TxId} of channel {ChannelId} waited {Blocks} blocks, but its next feerate "
                                 + "(at least {Minimum} sat/kw) is above Splice:AutoBumpMaxFeeratePerKw {Max}; not "
                                 + "bumping it (bumpsplice can)", latest.FundingTxId, channelId, waited, minimum,
                                   ceiling);
                continue;
            }

            // The estimate, at least the RBF minimum, clamped to the operator's ceiling
            var feerate = Math.Min(Math.Max(estimate, minimum), ceiling);
            bumps.Add(BumpWithinWaitAsync(channelId, latest.FundingTxId, feerate, height, cancellationToken));
        }

        return bumps.Count == 0 ? [] : await Task.WhenAll(bumps);
    }

    /// <summary>Subscribes to new blocks when the auto-bump is on. A second call does nothing.</summary>
    public void Start()
    {
        if (!IsEnabled || _blockchainMonitor is null)
            return;

        lock (_gate)
        {
            if (_stopping is not null)
                return;

            _stopping = new CancellationTokenSource();
        }

        _blockchainMonitor.OnNewBlockDetected += HandleNewBlockDetected;
        _logger.LogInformation("Splice auto-bump on: pending splices of ours are bumped after {Blocks} blocks, at most "
                             + "{Attempts} RBF attempts each", _options.AutoBumpAfterBlocks, _options.MaxRbfAttempts);
    }

    /// <summary>Unsubscribes and waits for a running round. Does nothing when not started.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? stopping;
        Task round;
        lock (_gate)
        {
            stopping = _stopping;
            _stopping = null;
            round = _round;
        }

        if (stopping is null)
            return;

        _blockchainMonitor!.OnNewBlockDetected -= HandleNewBlockDetected;
        await stopping.CancelAsync();
        try
        {
            await round;
        }
        catch (OperationCanceledException)
        {
            // Stopping
        }

        // Bumps a round stopped waiting for end with the cancellation
        Task[] inFlight;
        lock (_gate)
            inFlight = [.. _inFlight];
        try
        {
            await Task.WhenAll(inFlight);
        }
        catch (OperationCanceledException)
        {
            // Stopping
        }
        finally
        {
            stopping.Dispose();
        }
    }

    /// <summary>Waits until no round is running or scheduled (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task round;
            lock (_gate)
                round = _round;

            await round;

            lock (_gate)
            {
                if (ReferenceEquals(round, _round) && !_roundScheduled)
                    return;
            }
        }
    }

    /// <summary>
    /// The channel's latest splice attempt when it is due for a bump at <paramref name="height"/> (see the remarks),
    /// with the blocks it waited; null otherwise. Remembers the attempt in <paramref name="latestAttempts"/>.
    /// </summary>
    private async Task<(ChannelFunding LatestAttempt, uint Waited)?> FindDueAttemptAsync(
        ChannelModel channel, uint height, uint interval, IUnitOfWork? unitOfWork, HashSet<TxId> latestAttempts)
    {
        FundingSet fundings;
        try
        {
            fundings = _statePort.GetFundings(channel);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (fundings.LatestAttempt is not { } latest)
            return null;

        latestAttempts.Add(latest.FundingTxId);
        if (fundings.Pending.Any(p => p.ConfirmedHeight is not null || p.SpliceLockedSent))
            return null;

        var negotiation = _spliceService.GetNegotiation(channel.ChannelId);
        if (negotiation is { State: not (SpliceNegotiationState.Signed or SpliceNegotiationState.Aborted) })
            return null;

        if (!IsOurs(fundings, latest, negotiation))
            return null;

        var rbfAttempts = fundings.Pending.Count - 1;
        if (rbfAttempts >= _options.MaxRbfAttempts)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Splice {TxId} of channel {ChannelId} has {Attempts} RBF attempts (the most, "
                               + "Splice:MaxRbfAttempts); not bumping it", latest.FundingTxId, channel.ChannelId,
                                 rbfAttempts);
            return null;
        }

        var broadcast = unitOfWork is null
                            ? null
                            : await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(
                                  latest.FundingTxId);
        if (broadcast is { State: not BroadcastState.Pending })
            return null;

        uint baseline;
        lock (_gate)
        {
            if (_running.Contains(channel.ChannelId))
                return null;

            if (!_firstSeen.TryGetValue(latest.FundingTxId, out var firstSeen))
                _firstSeen[latest.FundingTxId] = firstSeen = height;

            baseline = broadcast?.FirstBroadcastHeight ?? firstSeen;
            if (_lastBump.TryGetValue(channel.ChannelId, out var last) && last.LatestAttempt == latest.FundingTxId)
                baseline = Math.Max(baseline, last.Height);
        }

        if (height < (ulong)baseline + interval)
            return null;

        return (latest, height - baseline);
    }

    /// <summary>
    /// The splice is ours to bump: our side contributed to it, or the service's last negotiation on the channel was
    /// ours and named one of its attempts.
    /// </summary>
    private static bool IsOurs(FundingSet fundings, ChannelFunding latest, SpliceNegotiationModel? negotiation) =>
        latest.LocalBalanceDeltaMsat != 0
     || negotiation is { IsInitiator: true, SpliceTxId: { } txId } && fundings.Pending.Any(p => p.FundingTxId == txId);

    /// <summary>
    /// Starts the bump and waits for it at most <see cref="SpliceOptions.AutoBumpMaxWait"/>. A bump still negotiating
    /// then goes on (its channel stays in <c>_running</c> until it ends, <see cref="StopAsync"/> waits for it) and the
    /// round reports the negotiation's current state, so a slow peer never holds up the other channels' bumps.
    /// </summary>
    private async Task<SpliceResult> BumpWithinWaitAsync(ChannelId channelId, TxId latestAttempt, uint feeratePerKw,
                                                         uint height, CancellationToken cancellationToken)
    {
        var bump = BumpAsync(channelId, latestAttempt, feeratePerKw, height, cancellationToken);
        lock (_gate)
            _inFlight.Add(bump);

        _ = bump.ContinueWith(t =>
        {
            lock (_gate)
                _inFlight.Remove(t);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            return await bump.WaitAsync(_options.AutoBumpMaxWait, cancellationToken);
        }
        catch (TimeoutException) when (!bump.IsCompleted)
        {
            var negotiation = _spliceService.GetNegotiation(channelId);
            var state = negotiation?.State ?? SpliceNegotiationState.AwaitingQuiescence;
            _logger.LogInformation("Splice bump of channel {ChannelId} still {State} after {Wait}; it goes on and the "
                                 + "round moves on", channelId, state, _options.AutoBumpMaxWait);
            return new SpliceResult(channelId, state, negotiation?.SpliceTxId);
        }
    }

    private async Task<SpliceResult> BumpAsync(ChannelId channelId, TxId latestAttempt, uint feeratePerKw, uint height,
                                               CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _running.Add(channelId);
            _lastBump[channelId] = (latestAttempt, height);
        }

        try
        {
            _logger.LogInformation("Bumping splice {TxId} of channel {ChannelId} at block {Height}: RBF at {Feerate} "
                                 + "sat/kw, our fee at most {MaxFee} sat", latestAttempt, channelId, height,
                                   feeratePerKw, _options.AutoBumpMaxFeeSat);
            var result = await _spliceService.BumpAsync(
                             new SpliceBumpRequest(channelId, feeratePerKw, _options.AutoBumpMaxFeeSat),
                             cancellationToken);
            _logger.LogInformation("Splice bump of channel {ChannelId}: {State}, txid {TxId}{Reason}", channelId,
                                   result.State, result.SpliceTxId,
                                   result.FailureReason is null ? string.Empty : $" ({result.FailureReason})");
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A refused rule, the peer offline, a timeout: tried again one interval later
            _logger.LogWarning("Splice bump of channel {ChannelId} at {Feerate} sat/kw failed: {Reason}", channelId,
                               feeratePerKw, e.Message);
            return new SpliceResult(channelId, SpliceNegotiationState.Aborted, FailureReason: e.Message);
        }
        finally
        {
            lock (_gate)
                _running.Remove(channelId);
        }
    }

    private async Task<uint> GetEstimateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var estimate = await _feeService.GetFeeRatePerKwAsync(cancellationToken);
            return (uint)Math.Clamp(estimate?.Satoshi ?? 0, 0, uint.MaxValue);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning("No fee estimate for the splice auto-bump ({Reason}); using the RBF minimum", e.Message);
            return 0;
        }
    }

    /// <summary>Drops the heights kept for attempts that are no longer the latest of a pending splice.</summary>
    private void Forget(HashSet<TxId> latestAttempts)
    {
        lock (_gate)
        {
            foreach (var txId in _firstSeen.Keys.Where(t => !latestAttempts.Contains(t)).ToList())
                _firstSeen.Remove(txId);
            foreach (var channelId in _lastBump.Where(b => !latestAttempts.Contains(b.Value.LatestAttempt))
                                               .Select(b => b.Key).ToList())
                _lastBump.Remove(channelId);
        }
    }

    private void HandleNewBlockDetected(object? sender, NewBlockEventArgs args)
    {
        lock (_gate)
        {
            if (_stopping is null || args.Height <= _requestedHeight)
                return;

            _requestedHeight = args.Height;
            if (_roundScheduled)
                return;

            _roundScheduled = true;
            var token = _stopping.Token;
            var previous = _round;
            _round = Task.Run(() => RunRoundsAsync(previous, token), CancellationToken.None);
        }
    }

    private async Task RunRoundsAsync(Task previous, CancellationToken cancellationToken)
    {
        await previous.ConfigureAwait(false);
        while (true)
        {
            uint height;
            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested || _requestedHeight <= _lastRoundHeight)
                {
                    _roundScheduled = false;
                    return;
                }

                height = _requestedHeight;
            }

            try
            {
                await BumpStaleSplicesAsync(height, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                    _roundScheduled = false;
                return;
            }
            catch (Exception e)
            {
                // The round's height is spent; the next block tries again
                _logger.LogError(e, "Splice auto-bump round at block {Height} failed", height);
            }
        }
    }
}
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Anchors;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Constants;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Fees;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Resolvers.Local;

/// <summary>
/// The anchor CPFP (BOLT 5 plan O7-T2, B5-FAIL-06: "MUST spend <c>to_local_anchor</c> with enough fee to get the
/// commitment mined; SHOULD RBF that child if it is not enough").
/// </summary>
/// <remarks>
/// <para>Rounds: one per new block over every loaded anchor channel (<c>ChannelParams.OptionAnchorOutputs</c>) that is
/// <c>Failed</c> or <c>OnchainResolving</c>, plus one for a channel right after <c>ChannelFailureService</c> published
/// its commitment (<see cref="ScheduleCommitmentRound"/>, in the background: the fail-the-channel path may run on the
/// peer's inbound loop and must not wait for a block round). Each round of a channel runs under the channel's lock and
/// reads its <see cref="BroadcastPurpose.LocalCommitment"/> row and its <see cref="BroadcastPurpose.AnchorCpfp"/> rows;
/// everything it decides goes into one save, and is published (and the wallet reservation released) after the lock.
/// Rounds never overlap; a round missed while one runs is coalesced into the next block's.</para>
/// <para>Pending commitment: the deadline is the earliest <c>cltv_expiry</c> of the HTLCs that are untrimmed on the
/// channel's local commitment (a trimmed HTLC has no output to resolve; none:
/// <see cref="AnchorCpfpOptions.NoDeadlineConfTarget"/>), the stake is our <c>to_local</c> plus those HTLCs, and the
/// fee cap has its floor only with a deadline (<see cref="AnchorCpfpPolicy.GetFeeCap"/>); a commitment without a
/// deadline gets no child when nothing of ours is on it or the child's floor fee is above its share of the stake; the estimate is the fee service's for
/// that target (<c>FeeEstimates</c>, floored). Without a child: none while the commitment alone pays the estimate, else
/// a child from <see cref="AnchorCpfpPolicy.DecideChild"/>, its wallet inputs reserved through
/// <see cref="IAnchorFeeInputSource"/> for the channel. With a pending child: once
/// <see cref="Domain.Onchain.Fees.SweepFeePolicy.ShouldBump"/> says it waited long enough and its package pays less than
/// the estimate, it is replaced (same anchor, the channel's reserved inputs plus more when needed, same change script)
/// at <see cref="AnchorCpfpPolicy.DecideReplacement"/>'s fee; the new row names the old one
/// (<c>ReplacesTransactionId</c>) and the old row turns <c>Replaced</c> in the same save.</para>
/// <para>Signing: the anchor input with <see cref="ILightningSigner.SignAnchorInput"/> (our funding key), the wallet
/// inputs with <see cref="ILightningSigner.SignWalletTransaction"/>; the assembled child is script-checked against every
/// spent output before it is stored. A child that cannot be signed or checked is dropped and its fresh reservation
/// released (logged once per channel).</para>
/// <para>Stored fee: a child row's <see cref="BroadcastTransactionModel.FeeratePerKw"/> is the child's own feerate over
/// its signed weight; a replacement takes <c>(rate + 1) * weight / 1000</c> (rounded up) as the old fee, an upper bound
/// that can only raise the BIP 125 minimum.</para>
/// <para>End: when the commitment is <c>Abandoned</c> or <c>Replaced</c> no child can confirm (its parent conflicts), so
/// the pending children are abandoned (never rebroadcast) and the reservation released at once. When the commitment
/// is <c>Confirmed</c> a pending child can still confirm (it spends a confirmed output): it stays pending (rebroadcast,
/// never bumped) and the reservation is kept, so no other spend (a funding transaction) conflicts with it, until the
/// chain shows our anchor spent (<see cref="IBitcoinChainService.GetConfirmedUnspentOutputAsync"/>; every child spends
/// it, so none can confirm any more) and the monitor has processed that block (a child still pending then lost), or
/// until <see cref="AnchorCpfpOptions.ConfirmedCommitmentChildWaitBlocks"/> passed. The reservation must be durable
/// (<see cref="IAnchorFeeInputSource"/>): the service never re-reserves after a restart.</para>
/// <para>Anchor sweep: once the commitment has 16 confirmations (the next block can spend with <c>nSequence</c> 16)
/// and no child is pending, the anchors that are still unspent (checked with
/// <see cref="IBitcoinChainService.GetUnspentOutputAsync"/>, mempool included; without a chain service the peer's,
/// and ours only when no child was ever made) are swept to a wallet address with empty
/// signatures when <see cref="AnchorCpfpPolicy.DecideAnchorSweep"/> says it pays for itself, else skipped (logged).
/// The sweep is published once per commitment and process and never stored: anyone may take those outputs first, and a
/// refused send is not retried.</para>
/// <para>Not covered (O7-T3/T4 and later): anchors of the peer's commitment, the fee inputs of our anchor HTLC
/// transactions, package relay for a commitment below the mempool minimum fee (the child is then refused as an orphan
/// together with it; both rows stay pending and are rebroadcast every block, so B5-FAIL-06 is not met there until the
/// pair goes through <c>submitpackage</c>).</para>
/// </remarks>
public sealed class AnchorCpfpService : IAnchorCpfpService, IDisposable
{
    private const int MaxSelectionRounds = 4;

    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IBitcoinChainService? _chainService;
    private readonly IAnchorChildTransactionBuilder _builder;
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IFeeService _feeService;
    private readonly IAnchorFeeInputSource? _feeInputSource;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<AnchorCpfpService> _logger;
    private readonly AnchorCpfpOptions _options;
    private readonly AnchorCpfpPolicy _policy;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISweepDestinationProvider _sweepDestinationProvider;

    private readonly SemaphoreSlim _roundLock = new(1, 1);
    private readonly HashSet<string> _loggedOnce = [];
    private readonly HashSet<TxId> _sweptCommitments = [];
    private readonly HashSet<ChannelId> _released = [];
    private readonly Dictionary<ChannelId, uint> _anchorSpentSeenAtTip = [];

    private CancellationTokenSource _stopping = new();
    private int _started;
    private int _pendingHeight = -1;
    private int _roundRunning;
    private int _scheduledRounds;

    public AnchorCpfpService(IBlockchainMonitor blockchainMonitor, IAnchorChildTransactionBuilder builder,
                             IChannelLockProvider channelLockProvider,
                             IChannelMemoryRepository channelMemoryRepository, IFeeService feeService,
                             ILightningSigner lightningSigner, ILogger<AnchorCpfpService> logger,
                             AnchorCpfpPolicy policy, IServiceScopeFactory serviceScopeFactory,
                             ISweepDestinationProvider sweepDestinationProvider,
                             IAnchorFeeInputSource? feeInputSource = null, AnchorCpfpOptions? options = null,
                             IBitcoinChainService? chainService = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _chainService = chainService;
        _builder = builder;
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _feeService = feeService;
        _feeInputSource = feeInputSource;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _options = options ?? new AnchorCpfpOptions();
        _policy = policy;
        _serviceScopeFactory = serviceScopeFactory;
        _sweepDestinationProvider = sweepDestinationProvider;
    }

    /// <inheritdoc />
    public void Start()
    {
        if (!_options.Enabled || Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _stopping = new CancellationTokenSource();
        _blockchainMonitor.OnNewBlockDetected += HandleNewBlockDetected;
        if (_feeInputSource is null)
            _logger.LogWarning("No anchor fee-input source is registered: commitments of anchor channels are not "
                             + "fee-bumped (BOLT 5 plan O7-T1)");
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) != 1)
            return;

        _blockchainMonitor.OnNewBlockDetected -= HandleNewBlockDetected;
        _stopping.Cancel();
    }

    /// <summary>Waits until no round runs (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (Volatile.Read(ref _roundRunning) != 0 || Volatile.Read(ref _pendingHeight) >= 0
            || Volatile.Read(ref _scheduledRounds) != 0)
            await Task.Delay(10);
    }

    /// <inheritdoc />
    public void ScheduleCommitmentRound(ChannelId channelId)
    {
        if (!_options.Enabled)
            return;

        CancellationToken token;
        try
        {
            token = _stopping.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Interlocked.Increment(ref _scheduledRounds);
        _ = Task.Run(async () =>
        {
            try
            {
                await OnCommitmentBroadcastAsync(channelId, token);
            }
            catch (OperationCanceledException)
            {
                // Stopping; the block rounds pick the channel up after the next start
            }
            catch (Exception e)
            {
                _logger.LogError(e, "The anchor CPFP round of channel {ChannelId} failed; retried at the next block",
                                 channelId);
            }
            finally
            {
                Interlocked.Decrement(ref _scheduledRounds);
            }
        }, CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task OnCommitmentBroadcastAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !_channelMemoryRepository.TryGetChannel(channelId, out var channel)
                              || !IsAnchorChannel(channel))
            return;

        await _roundLock.WaitAsync(cancellationToken);
        try
        {
            await RunChannelAsync(channel, _blockchainMonitor.LastProcessedBlockHeight, cancellationToken);
        }
        finally
        {
            _roundLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task RunOnceAsync(uint height, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return;

        await _roundLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var channel in _channelMemoryRepository.FindChannels(IsAnchorChannel))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunChannelAsync(channel, height, cancellationToken);
            }
        }
        finally
        {
            _roundLock.Release();
        }
    }

    public void Dispose()
    {
        Stop();
        _stopping.Dispose();
        _roundLock.Dispose();
    }

    private static bool IsAnchorChannel(ChannelModel channel) =>
        channel.ChannelParams.OptionAnchorOutputs
     && channel.State is ChannelState.Failed or ChannelState.OnchainResolving;

    /// <summary>One channel's round; never throws but for cancellation.</summary>
    private async Task RunChannelAsync(ChannelModel channel, uint height, CancellationToken cancellationToken)
    {
        var channelId = channel.ChannelId;
        try
        {
            RoundResult result;
            using (await _channelLockProvider.AcquireAsync(channelId, cancellationToken))
                result = await RunChannelLockedAsync(channel, height, cancellationToken);

            await CompleteAsync(channelId, result, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Anchor round of channel {ChannelId} at height {Height} failed; retried at the next "
                              + "block", channelId, height);
        }
    }

    private async Task<RoundResult> RunChannelLockedAsync(ChannelModel channel, uint height,
                                                          CancellationToken cancellationToken)
    {
        var result = new RoundResult();
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var repository = unitOfWork.BroadcastTransactionDbRepository;
        var broadcasts = await repository.GetByChannelIdAsync(channel.ChannelId);

        var commitment = broadcasts.Where(b => b.Purpose == BroadcastPurpose.LocalCommitment)
                                   .OrderBy(b => b.State switch
                                    {
                                        BroadcastState.Confirmed => 0,
                                        BroadcastState.Pending => 1,
                                        _ => 2
                                    })
                                   .ThenByDescending(b => b.CreatedAt)
                                   .FirstOrDefault();
        if (commitment is null)
            return result;

        var children = broadcasts.Where(b => b.Purpose == BroadcastPurpose.AnchorCpfp).ToList();
        var pendingChildren = children.Where(b => b.State == BroadcastState.Pending).ToList();

        if (commitment.State != BroadcastState.Pending)
        {
            // A child of a confirmed commitment may still confirm: keep it (and its wallet inputs) until it cannot
            if (commitment.State == BroadcastState.Confirmed && pendingChildren.Count > 0
                                                             && !await ChildrenSettledAsync(channel, commitment, height))
                return result;

            // The commitment confirmed and no child can confirm any more, or the commitment can no longer confirm
            var staged = false;
            foreach (var child in pendingChildren)
                staged |= await repository.MarkAbandonedAsync(child.TransactionId);
            if (staged)
                await unitOfWork.SaveChangesAsync();

            // Once per process: also a reservation a crash left without its child row
            result.Release = true;

            if (commitment.State == BroadcastState.Confirmed)
                result.Sweep = await PlanAnchorSweepAsync(channel, commitment, children.Count > 0, height,
                                                          cancellationToken);

            return result;
        }

        var pending = await PlanChildAsync(channel, commitment, pendingChildren, height, cancellationToken);
        if (pending is null)
            return result;

        repository.Add(pending.Row);
        if (pending.Replaces is { } replaced)
            await repository.MarkReplacedAsync(replaced);

        try
        {
            await unitOfWork.SaveChangesAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException && pending.ReleaseOnFailure
                                                                      && _feeInputSource is not null)
        {
            // A first child that was not stored is never published: its fresh reservation goes back at once (the
            // exception ends the round, so RunChannelAsync never gets a result to complete)
            try
            {
                await _feeInputSource.ReleaseAsync(channel.ChannelId, CancellationToken.None);
            }
            catch (Exception releaseError)
            {
                _logger.LogError(releaseError, "Cannot release the anchor fee inputs of channel {ChannelId} after a "
                                             + "failed save", channel.ChannelId);
            }

            throw;
        }

        result.Publish = pending.Row;
        return result;
    }

    private async Task CompleteAsync(ChannelId channelId, RoundResult result, CancellationToken cancellationToken)
    {
        if (result.Publish is { } row)
        {
            var accepted = await _blockchainMonitor.PublishAsync(row);
            if (!accepted)
                _logger.LogWarning("Anchor child {TxId} of channel {ChannelId} was refused; it is sent again after "
                                 + "every block", Display(row.TransactionId), channelId);
        }

        if (result.Release && _feeInputSource is not null)
        {
            bool first;
            lock (_released)
                first = _released.Add(channelId);

            if (first)
            {
                await _feeInputSource.ReleaseAsync(channelId, cancellationToken);
                _logger.LogInformation("Released the anchor fee inputs of channel {ChannelId}", channelId);
            }
        }

        if (result.Sweep is { } sweep)
        {
            try
            {
                await _blockchainMonitor.PublishTransactionAsync(sweep);
                _logger.LogInformation("Swept the anchors of channel {ChannelId} in {TxId}", channelId,
                                       Display(sweep.TxId));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Anyone may take anchors after 16 blocks; a refusal usually means someone did
                _logger.LogInformation("Anchor sweep {TxId} of channel {ChannelId} was refused ({Reason}); not retried",
                                       Display(sweep.TxId), channelId, e.Message);
            }
        }
    }

    /// <summary>
    /// The first child or a replacement for a pending commitment, or null when none is due or possible. Called under the
    /// channel's lock; stages nothing.
    /// </summary>
    private async Task<PlannedChild?> PlanChildAsync(ChannelModel channel, BroadcastTransactionModel commitment,
                                                     IReadOnlyList<BroadcastTransactionModel> pendingChildren,
                                                     uint height, CancellationToken cancellationToken)
    {
        var channelId = channel.ChannelId;
        if (_feeInputSource is null)
            return null;

        if (channel.FundingOutput is not { } funding)
        {
            LogOnce($"{channelId}:funding", "Channel {ChannelId} has no funding output; its commitment is not "
                                          + "fee-bumped", channelId);
            return null;
        }

        var fundingPubKey = channel.LocalKeySet.FundingCompactPubKey;
        var commitmentTx = Transaction.Load(commitment.RawTransaction, Network.Main);
        if (_builder.FindAnchorOutput(commitment.RawTransaction, fundingPubKey) is not { } anchorVout)
        {
            LogOnce($"{channelId}:{commitment.TransactionId}:anchor",
                    "Commitment {TxId} of channel {ChannelId} has no anchor of ours; it cannot be fee-bumped",
                    Display(commitment.TransactionId), channelId);
            return null;
        }

        var outputsSat = commitmentTx.Outputs.Aggregate(0UL, (sum, o) => sum + (ulong)o.Value.Satoshi);
        var fundingSat = (ulong)funding.Amount.Satoshi;
        if (outputsSat > fundingSat)
            return null;

        var commitmentFee = fundingSat - outputsSat;
        var commitmentWeight = GetWeight(commitmentTx);
        var (deadline, stakeSat) = GetDeadlineAndStake(channel);
        if (deadline is null && stakeSat == 0)
        {
            LogOnce($"{channelId}:{commitment.TransactionId}:nostake",
                    "Commitment {TxId} of channel {ChannelId} carries nothing of ours and no HTLC; it is not fee-bumped",
                    Display(commitment.TransactionId), channelId);
            return null;
        }

        var target = _policy.GetConfirmationTarget(height, deadline);
        var estimate = await FeeEstimates.GetForTargetAsync(_feeService, target, _logger, cancellationToken);
        var cap = _policy.GetFeeCap(stakeSat, deadline is not null);
        var anchor = new AnchorOutpoint(commitment.TransactionId, anchorVout, fundingPubKey);

        var latest = pendingChildren.OrderByDescending(b => b.FirstBroadcastHeight)
                                    .ThenByDescending(b => b.CreatedAt)
                                    .FirstOrDefault();
        if (latest is null)
            return await PlanFirstChildAsync(channel, anchor, commitmentFee, commitmentWeight, estimate, cap, deadline,
                                             height, cancellationToken);

        // Past the deadline the commitment still has to confirm (to_local, the HTLC transactions): keep bumping every
        // RbfIntervalBlocks, as just before it (SweepFeePolicy stops at a sweep's deadline)
        var bumpDeadline = deadline is { } d && height >= d ? height + 1 : deadline;
        if (!_policy.FeePolicy.ShouldBump(latest.FirstBroadcastHeight, height, bumpDeadline))
            return null;

        var oldTx = Transaction.Load(latest.RawTransaction, Network.Main);
        var oldWeight = GetWeight(oldTx);
        var oldFeeLower = (ulong)latest.FeeratePerKw * (ulong)oldWeight / 1000;
        if (_policy.PackagePays(commitmentFee, commitmentWeight, oldFeeLower, oldWeight, estimate))
            return null;

        var oldFeeUpper = ((ulong)latest.FeeratePerKw + 1) * (ulong)oldWeight / 1000 + 1;
        var changeScript = oldTx.Outputs[0].ScriptPubKey.ToBytes();
        var held = (await _feeInputSource.GetReservedAsync(channelId, cancellationToken)).ToList();
        var inputs = await EnsureFundsAsync(channelId, held, changeScript,
                                            w => _policy.DecideReplacement(commitmentFee, commitmentWeight, w,
                                                                           estimate, oldFeeUpper, cap),
                                            cancellationToken);
        if (inputs is null)
        {
            LogOnce($"{channelId}:{latest.TransactionId}:rbf",
                    "Anchor child {TxId} of channel {ChannelId} is unconfirmed since height {Since}, but no replacement "
                  + "can outbid its fee within the {Cap} sat cap or the wallet cannot pay it; it is kept",
                    Display(latest.TransactionId), channelId, latest.FirstBroadcastHeight, cap);
            return null;
        }

        var (walletInputs, decision) = inputs.Value;
        var signed = SignChild(channelId, anchor, walletInputs, changeScript, decision.FeeSat);
        if (signed is null)
            return null;

        _logger.LogWarning("Anchor child {TxId} of commitment {CommitmentTxId} (channel {ChannelId}) is unconfirmed "
                         + "since height {Since} (deadline {Deadline}); replacing it with {NewTxId}: fee {OldFee} -> "
                         + "{NewFee} sat, package {Package} sat/kw (target {Target} blocks{Capped})",
                           Display(latest.TransactionId), Display(commitment.TransactionId), channelId,
                           latest.FirstBroadcastHeight, deadline, Display(signed.Value.Transaction.TxId), oldFeeLower,
                           decision.FeeSat, decision.PackageFeeratePerKw, target, decision.Capped ? ", capped" : "");
        var row = new BroadcastTransactionModel(signed.Value.Transaction, BroadcastPurpose.AnchorCpfp, channelId,
                                                height, signed.Value.FeeratePerKw, latest.TransactionId);
        return new PlannedChild(row, latest.TransactionId, false);
    }

    private async Task<PlannedChild?> PlanFirstChildAsync(ChannelModel channel, AnchorOutpoint anchor,
                                                          ulong commitmentFee, long commitmentWeight, uint estimate,
                                                          ulong cap, uint? deadline, uint height,
                                                          CancellationToken cancellationToken)
    {
        var channelId = channel.ChannelId;
        var emptyWeight = _builder.EstimateChildWeight([], 22);
        if (_policy.DecideChild(commitmentFee, commitmentWeight, emptyWeight, estimate, cap) is null)
            return null;

        byte[] changeScript;
        try
        {
            changeScript = await _sweepDestinationProvider.GetDestinationScriptAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogOnce($"{channelId}:change", "No wallet script for the anchor child of channel {ChannelId}: {Reason}",
                    channelId, e.Message);
            return null;
        }

        var held = (await _feeInputSource!.GetReservedAsync(channelId, cancellationToken)).ToList();
        var inputs = await EnsureFundsAsync(channelId, held, changeScript,
                                            w => _policy.DecideChild(commitmentFee, commitmentWeight, w, estimate,
                                                                     cap),
                                            cancellationToken);
        if (inputs is null)
        {
            LogOnce($"{channelId}:{anchor.TxId}:funds",
                    "The wallet cannot pay the anchor child of commitment {TxId} (channel {ChannelId}); retried every "
                  + "block", Display(anchor.TxId), channelId);
            await _feeInputSource.ReleaseAsync(channelId, cancellationToken);
            return null;
        }

        var (walletInputs, decision) = inputs.Value;
        if (deadline is null && decision.FeeSat > cap)
        {
            // Only the child's floor fee is above the cap: without a deadline it is not worth the wallet's money
            LogOnce($"{channelId}:{anchor.TxId}:uneconomical",
                    "The anchor child of commitment {TxId} (channel {ChannelId}) would pay {Fee} sat, above its {Cap} "
                  + "sat share of our stake, and no HTLC has a deadline; it is not made", Display(anchor.TxId),
                    channelId, decision.FeeSat, cap);
            await _feeInputSource.ReleaseAsync(channelId, cancellationToken);
            return null;
        }

        var signed = SignChild(channelId, anchor, walletInputs, changeScript, decision.FeeSat);
        if (signed is null)
        {
            await _feeInputSource.ReleaseAsync(channelId, cancellationToken);
            return null;
        }

        lock (_released)
            _released.Remove(channelId);

        _logger.LogWarning("Commitment {TxId} of channel {ChannelId} pays {CommitmentRate} sat/kw, below the {Target} "
                         + "sat/kw estimate (deadline {Deadline}); CPFP child {ChildTxId} spends our anchor with a "
                         + "{Fee} sat fee, package {Package} sat/kw{Capped}", Display(anchor.TxId), channelId,
                           AnchorCpfpPolicy.FeeratePerKw(commitmentFee, commitmentWeight),
                           decision.TargetFeeratePerKw, deadline, Display(signed.Value.Transaction.TxId),
                           decision.FeeSat, decision.PackageFeeratePerKw, decision.Capped ? " (capped)" : "");
        var row = new BroadcastTransactionModel(signed.Value.Transaction, BroadcastPurpose.AnchorCpfp, channelId,
                                                height, signed.Value.FeeratePerKw);
        return new PlannedChild(row, null, true);
    }

    /// <summary>
    /// Reserves wallet inputs until they (and the anchor) cover the decided fee plus a change above dust, re-deciding
    /// the fee for the weight of the inputs held. Null when no fee can be decided or the wallet runs short.
    /// </summary>
    private async Task<(IReadOnlyList<AnchorWalletInput> Inputs, AnchorChildFeeDecision Decision)?> EnsureFundsAsync(
        ChannelId channelId, List<AnchorWalletInput> held, byte[] changeScript,
        Func<long, AnchorChildFeeDecision?> decide, CancellationToken cancellationToken)
    {
        var dust = ShutdownScriptValidator.GetDustThresholdSat(changeScript);
        for (var round = 0; round < MaxSelectionRounds; round++)
        {
            var weight = _builder.EstimateChildWeight(held, changeScript.Length);
            if (decide(weight) is not { } decision)
                return null;

            var available = held.Aggregate(AnchorCpfpPolicy.AnchorSat, (sum, i) => sum + i.AmountSat);
            var needed = decision.FeeSat + dust;
            if (held.Count > 0 && available >= needed)
                return (held, decision);

            var missing = needed > available ? needed - available : 1;
            var more = await _feeInputSource!.ReserveAsync(channelId, missing, decision.TargetFeeratePerKw,
                                                           cancellationToken);
            if (more is not { Count: > 0 })
                return null;

            held.AddRange(more.Where(m => !held.Any(h => h.TxId == m.TxId && h.OutputIndex == m.OutputIndex)));
        }

        return null;
    }

    /// <summary>Builds and signs a child and checks every input's script; null (logged once) when that fails.</summary>
    private (SignedTransaction Transaction, uint FeeratePerKw)? SignChild(ChannelId channelId, AnchorOutpoint anchor,
                                                                        IReadOnlyList<AnchorWalletInput> walletInputs,
                                                                        byte[] changeScript, ulong feeSat)
    {
        try
        {
            var unsigned = _builder.BuildChild(anchor, walletInputs, changeScript, feeSat);
            var anchorSignature = _lightningSigner.SignAnchorInput(channelId, unsigned.Transaction,
                                                                   unsigned.AnchorInputIndex,
                                                                   TransactionConstants.AnchorOutputAmount);

            var walletSigned = new SignedTransaction(unsigned.Transaction.TxId,
                                                     (byte[])unsigned.Transaction.RawTxBytes.Clone());
            _lightningSigner.SignWalletTransaction(walletSigned);
            var signed = _builder.AddAnchorWitness(walletSigned.RawTxBytes, unsigned.AnchorInputIndex,
                                                   anchorSignature, anchor.FundingPubKey);

            var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
            if (new TxId(tx.GetHash().ToBytes()) != unsigned.Transaction.TxId)
                throw new InvalidOperationException("Signing changed the child's txid");

            var spentOutputs = new TxOut[tx.Inputs.Count];
            spentOutputs[unsigned.AnchorInputIndex] =
                new TxOut(Money.Satoshis(AnchorCpfpPolicy.AnchorSat),
                          new Script(_builder.GetAnchorScriptPubKey(anchor.FundingPubKey)));
            for (var i = 0; i < walletInputs.Count; i++)
                spentOutputs[i + 1] = new TxOut(Money.Satoshis(walletInputs[i].AmountSat),
                                                new Script(walletInputs[i].ScriptPubKey));

            var validator = tx.CreateValidator(spentOutputs);
            for (var i = 0; i < tx.Inputs.Count; i++)
            {
                var check = validator.ValidateInput(i);
                if (check.Error is { } error)
                    throw new InvalidOperationException($"Input {i} of the child does not verify: {error}");
            }

            return (signed, AnchorCpfpPolicy.FeeratePerKw(feeSat, GetWeight(tx)));
        }
        catch (Exception e) when (e is SignerException or NotImplementedException or ArgumentException
                                      or InvalidOperationException or FormatException)
        {
            LogOnce($"{channelId}:sign:{e.GetType().Name}", e,
                    "Cannot sign the anchor child of channel {ChannelId}; the commitment is not fee-bumped this block",
                    channelId);
            return null;
        }
    }

    /// <summary>The anchor sweep of a confirmed commitment, once due and economical (and only once per process).</summary>
    private async Task<SignedTransaction?> PlanAnchorSweepAsync(ChannelModel channel,
                                                                BroadcastTransactionModel commitment,
                                                                bool anyChild, uint height,
                                                                CancellationToken cancellationToken)
    {
        if (!_options.SweepAnchors || commitment.ConfirmedHeight is not { } confirmedHeight
                                   || height + 1 < confirmedHeight + AnchorChildTransactionBuilder.AnchorCsvSequence)
            return null;

        lock (_sweptCommitments)
        {
            if (!_sweptCommitments.Add(commitment.TransactionId))
                return null;
        }

        var anchors = new List<AnchorOutpoint>();
        var ours = channel.LocalKeySet.FundingCompactPubKey;
        if (_builder.FindAnchorOutput(commitment.RawTransaction, ours) is { } ourVout)
            anchors.Add(new AnchorOutpoint(commitment.TransactionId, ourVout, ours));
        if (channel.RemoteKeySet?.FundingCompactPubKey is { } theirs
         && _builder.FindAnchorOutput(commitment.RawTransaction, theirs) is { } theirVout)
            anchors.Add(new AnchorOutpoint(commitment.TransactionId, theirVout, theirs));

        // One spent input invalidates the whole sweep: keep only the anchors nobody spent (a child of ours, also a
        // replaced one the monitor stopped tracking, or the peer's own CPFP)
        if (_chainService is null)
        {
            if (anyChild)
                anchors.RemoveAll(a => a.FundingPubKey == ours);
        }
        else
        {
            try
            {
                var unspent = new List<AnchorOutpoint>();
                foreach (var anchor in anchors)
                    if (await _chainService.GetUnspentOutputAsync(ToOutPoint(anchor.TxId, anchor.OutputIndex))
                            is not null)
                        unspent.Add(anchor);
                anchors = unspent;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Tried again at the next block
                lock (_sweptCommitments)
                    _sweptCommitments.Remove(commitment.TransactionId);
                _logger.LogInformation("Cannot check the anchors of commitment {TxId} (channel {ChannelId}): {Reason}",
                                       Display(commitment.TransactionId), channel.ChannelId, e.Message);
                return null;
            }
        }

        if (anchors.Count == 0)
            return null;

        byte[] destination;
        try
        {
            destination = await _sweepDestinationProvider.GetDestinationScriptAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogInformation("No wallet script for the anchor sweep of channel {ChannelId}: {Reason}",
                                   channel.ChannelId, e.Message);
            return null;
        }

        var weight = _builder.EstimateSweepWeight(anchors.Count, destination.Length);
        var target = _policy.GetConfirmationTarget(height, null);
        var estimate = await FeeEstimates.GetForTargetAsync(_feeService, target, _logger, cancellationToken);
        var decision = _policy.DecideAnchorSweep(anchors.Count, weight, estimate,
                                                 ShutdownScriptValidator.GetDustThresholdSat(destination));
        if (!decision.Economical)
        {
            _logger.LogInformation("Not sweeping the {Count} anchor(s) of commitment {TxId} (channel {ChannelId}): a "
                                 + "{Fee} sat fee at {Rate} sat/kw is not worth {Value} sat", anchors.Count,
                                   Display(commitment.TransactionId), channel.ChannelId, decision.FeeSat,
                                   decision.FeeratePerKw, AnchorCpfpPolicy.AnchorSat * (ulong)anchors.Count);
            return null;
        }

        return _builder.BuildAnchorSweep(anchors, destination, decision.FeeSat);
    }

    /// <summary>
    /// Whether the pending children of a confirmed commitment can no longer confirm, so their wallet inputs may go back:
    /// the wait (<see cref="AnchorCpfpOptions.ConfirmedCommitmentChildWaitBlocks"/>) passed, or our anchor, which every
    /// child spends, is spent in the active chain and the monitor has processed the block that spent it (seen in an
    /// earlier round at a bitcoind tip at or below this round's height; this round's rows were read after that
    /// processing, so a child that won is already <c>Confirmed</c>).
    /// </summary>
    private async Task<bool> ChildrenSettledAsync(ChannelModel channel, BroadcastTransactionModel commitment,
                                                  uint height)
    {
        var channelId = channel.ChannelId;
        if (commitment.ConfirmedHeight is { } confirmedHeight
         && height >= (ulong)confirmedHeight + _options.ConfirmedCommitmentChildWaitBlocks)
        {
            _logger.LogWarning("Commitment {TxId} of channel {ChannelId} confirmed at {Height} but its anchor child is "
                             + "still unconfirmed; it is abandoned and its wallet inputs released",
                               Display(commitment.TransactionId), channelId, confirmedHeight);
            return true;
        }

        if (_chainService is null
         || _builder.FindAnchorOutput(commitment.RawTransaction, channel.LocalKeySet.FundingCompactPubKey)
                is not { } anchorVout)
            return false;

        lock (_anchorSpentSeenAtTip)
        {
            if (_anchorSpentSeenAtTip.TryGetValue(channelId, out var seenAtTip) && height >= seenAtTip)
            {
                _anchorSpentSeenAtTip.Remove(channelId);
                return true;
            }
        }

        try
        {
            var outpoint = ToOutPoint(commitment.TransactionId, anchorVout);
            if (await _chainService.GetConfirmedUnspentOutputAsync(outpoint) is not null)
            {
                lock (_anchorSpentSeenAtTip)
                    _anchorSpentSeenAtTip.Remove(channelId);
                return false;
            }

            var tip = await _chainService.GetCurrentBlockHeightAsync();
            lock (_anchorSpentSeenAtTip)
                _anchorSpentSeenAtTip.TryAdd(channelId, tip);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogOnce($"{channelId}:anchor-spent-check", e,
                    "Cannot check the anchor of confirmed commitment {TxId} (channel {ChannelId}); its child keeps its "
                  + "wallet inputs", Display(commitment.TransactionId), channelId);
        }

        return false;
    }

    /// <summary>
    /// The deadline (earliest <c>cltv_expiry</c>) and our stake (to_local plus the HTLCs, in sat) of the channel's local
    /// commitment, counting only the HTLCs that have an output on it (untrimmed with our dust limit).
    /// </summary>
    private static (uint? Deadline, ulong StakeSat) GetDeadlineAndStake(ChannelModel channel)
    {
        if (channel.Commitments is not { } commitments)
            return (null, (ulong)channel.LocalBalance.Satoshi);

        var spec = commitments.LocalCommit.Spec;
        var dust = commitments.Params.Local.DustLimitSatoshis;
        var untrimmed = spec.Htlcs.Where(h => !CommitmentFeeCalculator.IsHtlcTrimmed(spec, h, dust,
                                                                                     commitments.Params.OptionAnchors))
                            .ToList();
        var htlcMsat = untrimmed.Aggregate(0UL, (sum, h) => checked(sum + h.AmountMsat));
        return (AnchorCpfpPolicy.GetDeadline(untrimmed.Select(h => h.CltvExpiry)),
                (spec.LocalMsat + htlcMsat) / 1000);
    }

    private static OutPoint ToOutPoint(TxId txId, uint outputIndex) => new(new uint256(txId), outputIndex);

    private void HandleNewBlockDetected(object? sender, NewBlockEventArgs args)
    {
        // Coalesce to the latest height; one background round at a time
        Interlocked.Exchange(ref _pendingHeight, (int)Math.Min(args.Height, int.MaxValue));
        if (Interlocked.Exchange(ref _roundRunning, 1) == 1)
            return;

        var token = _stopping.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var height = Interlocked.Exchange(ref _pendingHeight, -1);
                    if (height < 0)
                        break;

                    await RunOnceAsync((uint)height, token);
                }
            }
            catch (OperationCanceledException)
            {
                // Stopping
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Anchor CPFP round failed");
            }
            finally
            {
                Interlocked.Exchange(ref _roundRunning, 0);
            }
        }, CancellationToken.None);
    }

    /// <summary>A transaction's weight: 3 x its size without witnesses plus its full size.</summary>
    private static long GetWeight(Transaction tx)
    {
        var stripped = tx.Clone();
        foreach (var input in stripped.Inputs)
            input.WitScript = WitScript.Empty;

        return 3L * stripped.ToBytes().Length + tx.ToBytes().Length;
    }

    private void LogOnce(string key, string message, params object?[] args) => LogOnce(key, null, message, args);

    private void LogOnce(string key, Exception? exception, string message, params object?[] args)
    {
        lock (_loggedOnce)
        {
            if (!_loggedOnce.Add(key))
                return;
        }

#pragma warning disable CA2254 // The templates are constants of this class
        _logger.LogWarning(exception, message, args);
#pragma warning restore CA2254
    }

    private static string Display(TxId txId) => new uint256(txId).ToString();

    private sealed class RoundResult
    {
        public BroadcastTransactionModel? Publish { get; set; }
        public bool Release { get; set; }
        public SignedTransaction? Sweep { get; set; }
    }

    private sealed record PlannedChild(BroadcastTransactionModel Row, TxId? Replaces, bool ReleaseOnFailure);
}
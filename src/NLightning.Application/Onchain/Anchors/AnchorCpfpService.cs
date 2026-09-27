using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Anchors;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Constants;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
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
using Infrastructure.Bitcoin.Onchain.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Bitcoin.Wallet.Models;
using Resolvers.Local;

/// <summary>
/// The anchor CPFP (BOLT 5 plan O7-T2, B5-FAIL-06: "MUST spend <c>to_local_anchor</c> with enough fee to get the
/// commitment mined; SHOULD RBF that child if it is not enough"), for our commitment and, through our anchor on it, the
/// peer's (NL-381).
/// </summary>
/// <remarks>
/// <para>Rounds: one per new block over every loaded anchor channel (<c>ChannelParams.OptionAnchorOutputs</c>) that is
/// <c>Failed</c> or <c>OnchainResolving</c> or whose peer commitment was seen in the mempool
/// (<see cref="OnPeerCommitmentInMempool"/>), plus one for a channel right after <c>ChannelFailureService</c> published
/// its commitment (<see cref="ScheduleCommitmentRound"/>, in the background: the fail-the-channel path may run on the
/// peer's inbound loop and must not wait for a block round). Each round of a channel runs under the channel's lock and
/// reads its <see cref="BroadcastPurpose.LocalCommitment"/> row and its <see cref="BroadcastPurpose.AnchorCpfp"/> rows
/// (a child belongs to the commitment its anchor input spends: one of our rows, else the peer's); everything it decides
/// is saved before it is published (and the wallet reservation released) after the lock. Rounds never overlap; a round
/// missed while one runs is coalesced into the next block's.</para>
/// <para>Pending commitment: the deadline is the earliest <c>cltv_expiry</c> of the HTLCs that are untrimmed on the
/// channel's local commitment (a trimmed HTLC has no output to resolve; none:
/// <see cref="AnchorCpfpOptions.NoDeadlineConfTarget"/>), the stake is our <c>to_local</c> plus those HTLCs, and the
/// fee cap has its floor only with a deadline (<see cref="AnchorCpfpPolicy.GetFeeCap"/>); a commitment without a
/// deadline gets no child when nothing of ours is on it or the child's floor fee is above its share of the stake; the
/// estimate is the fee service's for that target (<c>FeeEstimates</c>, floored). Without a child: none while the
/// commitment alone pays the estimate, else a child from <see cref="AnchorCpfpPolicy.DecideChild"/>, its wallet inputs
/// reserved through <see cref="IAnchorFeeInputSource"/> for the channel. With a pending child: once
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
/// the pending children are abandoned (never rebroadcast). When the commitment is <c>Confirmed</c> a pending child can
/// still confirm (it spends a confirmed output): it stays pending (rebroadcast, never bumped) and the reservation is
/// kept, so no other spend (a funding transaction) conflicts with it, until the chain shows our anchor spent
/// (<see cref="IBitcoinChainService.GetConfirmedUnspentOutputAsync"/>; every child spends it, so none can confirm any
/// more) and the monitor has processed that block (a child still pending then lost), or until
/// <see cref="AnchorCpfpOptions.ConfirmedCommitmentChildWaitBlocks"/> passed. The reservation is shared by every child
/// of the channel (ours and the peer's commitment's; only one commitment can confirm) and released once neither has a
/// child that can still confirm. It must be durable (<see cref="IAnchorFeeInputSource"/>): the service never
/// re-reserves after a restart.</para>
/// <para>Anchor sweep: once the commitment has 16 confirmations (the next block can spend with <c>nSequence</c> 16)
/// and no child is pending, the anchors that are still unspent (checked with
/// <see cref="IBitcoinChainService.GetUnspentOutputAsync"/>, mempool included; without a chain service the peer's,
/// and ours only when no child was ever made) are swept to a wallet address with empty
/// signatures when <see cref="AnchorCpfpPolicy.DecideAnchorSweep"/> says it pays for itself, else skipped (logged).
/// The sweep is published once per commitment and process and never stored: anyone may take those outputs first, and a
/// refused send is not retried.</para>
/// <para>Package relay (NL-380): a commitment below bitcoind's mempool minimum fee (a fee spike after the last
/// <c>update_fee</c>) is refused alone, and its child as an orphan. When a new child is refused, and every round when the
/// newest pending child is not in bitcoind's mempool (<see cref="IBitcoinChainService.GetTransactionAsync"/>, which
/// finds mempool transactions without txindex), the commitment and that child go out together through
/// <see cref="IBitcoinChainService.SubmitPackageAsync"/> (Bitcoin Core 28+ 1p1c), judged at their package feerate. The
/// rows are the persisted <c>BroadcastTransactions</c>, so after a restart the first round sends the pair as a package
/// again (the monitor's own rebroadcast keeps sending them one by one). A package refused for its fee marks the child
/// (memory only), and the next round replaces it at once, even without a deadline and while the package pays the
/// estimate: the estimate is then below what the mempool takes, and each replacement adds at least the BIP 125
/// increment, up to the cap. Without <c>submitpackage</c> (older node, or no chain service) this is logged once and the
/// pair is only sent one by one.</para>
/// <para>The peer's commitment (NL-381): see <c>AnchorCpfpService.Peer.cs</c>.</para>
/// <para>Mempool minimum: the estimate a child targets is raised to bitcoind's current mempool minimum
/// (<see cref="IBitcoinChainService.GetMempoolMinFeeRatePerKwAsync"/>), so a commitment that pays the estimate but not
/// that minimum gets a child and is packaged too, and a replacement after a fee refusal targets at least what the
/// mempool takes (plus the BIP 125 increment).</para>
/// </remarks>
public sealed partial class AnchorCpfpService : IAnchorCpfpService, IDisposable
{
    private const int MaxSelectionRounds = 4;

    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IBitcoinChainService? _chainService;
    private readonly ICommitmentOutputMapper? _commitmentOutputMapper;
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
    private readonly HashSet<TxId> _feeRefusedChildren = [];
    private readonly Dictionary<TxId, uint> _anchorSpentSeenAtTip = [];
    private readonly ConcurrentDictionary<ChannelId, PeerCommitmentSeen> _peerCommitments = new();
    private readonly Dictionary<TxId, (uint Height, int Count)> _peerCommitmentMissing = [];
    private readonly ConcurrentDictionary<ChannelId, byte> _peerChildChannels = new();

    private CancellationTokenSource _stopping = new();
    private int _started;
    private int _pendingHeight = -1;
    private int _roundRunning;
    private int _peerChildChannelsLoaded;
    private int _scheduledRounds;

    public AnchorCpfpService(IBlockchainMonitor blockchainMonitor, IAnchorChildTransactionBuilder builder,
                             IChannelLockProvider channelLockProvider,
                             IChannelMemoryRepository channelMemoryRepository, IFeeService feeService,
                             ILightningSigner lightningSigner, ILogger<AnchorCpfpService> logger,
                             AnchorCpfpPolicy policy, IServiceScopeFactory serviceScopeFactory,
                             ISweepDestinationProvider sweepDestinationProvider,
                             IAnchorFeeInputSource? feeInputSource = null, AnchorCpfpOptions? options = null,
                             IBitcoinChainService? chainService = null,
                             ICommitmentOutputMapper? commitmentOutputMapper = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _chainService = chainService;
        _commitmentOutputMapper = commitmentOutputMapper;
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
                              || !IsRoundChannel(channel))
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
            await LoadPersistedPeerChildChannelsAsync();
            foreach (var channel in _channelMemoryRepository.FindChannels(IsRoundChannel))
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

    /// <summary>
    /// An anchor channel that is failed or resolving on chain, or whose peer commitment was seen in the mempool or has
    /// a pending child of ours (the channel may still be <c>Open</c> then: the peer force-closed).
    /// </summary>
    private bool IsRoundChannel(ChannelModel channel) =>
        channel.ChannelParams.OptionAnchorOutputs
     && (channel.State is ChannelState.Failed or ChannelState.OnchainResolving
      || ((_peerCommitments.ContainsKey(channel.ChannelId) || _peerChildChannels.ContainsKey(channel.ChannelId))
       && channel.State is not (ChannelState.Closed or ChannelState.Stale)));

    /// <summary>
    /// Once per process (retried at the next round when the database cannot be read): the channels with a pending
    /// child of the peer's commitment (its anchor input spends no <c>LocalCommitment</c> row of the channel). The
    /// mempool reactor's hand-over is memory only and the monitor does not report a mempool transaction again after a
    /// restart, so without this an <c>Open</c> channel whose peer force-closed would get no RBF, no abandonment and no
    /// release until something else fails it.
    /// </summary>
    private async Task LoadPersistedPeerChildChannelsAsync()
    {
        if (Volatile.Read(ref _peerChildChannelsLoaded) != 0)
            return;

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository;
            var channelIds = (await repository.GetPendingAsync())
                            .Where(b => b.Purpose == BroadcastPurpose.AnchorCpfp)
                            .Select(b => b.ChannelId)
                            .OfType<ChannelId>()
                            .Distinct()
                            .ToList();
            foreach (var channelId in channelIds)
            {
                var rows = await repository.GetByChannelIdAsync(channelId);
                var localTxIds = rows.Where(b => b.Purpose == BroadcastPurpose.LocalCommitment)
                                     .Select(b => b.TransactionId)
                                     .ToHashSet();
                if (rows.Any(b => b is { Purpose: BroadcastPurpose.AnchorCpfp, State: BroadcastState.Pending }
                               && ParentOf(b) is { } parent && !localTxIds.Contains(parent)))
                    _peerChildChannels.TryAdd(channelId, 0);
            }

            Volatile.Write(ref _peerChildChannelsLoaded, 1);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogOnce("peer-children:load", e, "Cannot read the pending anchor children of the peers' commitments; "
                                            + "retried at the next block");
        }
    }

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
        var broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(channel.ChannelId);

        // A child belongs to the commitment its anchor input spends: ours (a LocalCommitment row) or the peer's
        var localTxIds = broadcasts.Where(b => b.Purpose == BroadcastPurpose.LocalCommitment)
                                   .Select(b => b.TransactionId)
                                   .ToHashSet();
        var children = broadcasts.Where(b => b.Purpose == BroadcastPurpose.AnchorCpfp).ToList();
        var localChildren = children.Where(c => ParentOf(c) is not { } parent || localTxIds.Contains(parent))
                                    .ToList();
        var peerChildren = children.Except(localChildren).ToList();

        var peer = await RunPeerLockedAsync(channel, unitOfWork, peerChildren,
                                            localChildren.Any(c => c.State == BroadcastState.Pending), height, result,
                                            cancellationToken);
        var local = await RunLocalLockedAsync(channel, unitOfWork, broadcasts, localChildren,
                                              peerChildren.Any(c => c.State == BroadcastState.Pending), height,
                                              result, cancellationToken);

        // A peer child keeps an Open channel in the rounds (also after a restart) until none can confirm any more
        if (peer == PathState.Active
         && (result.PeerChild is not null || peerChildren.Any(c => c.State == BroadcastState.Pending)))
            _peerChildChannels.TryAdd(channel.ChannelId, 0);
        else if (peer != PathState.Active)
            _peerChildChannels.TryRemove(channel.ChannelId, out _);

        // The wallet inputs are shared by every child of the channel: they go back only when no child of either
        // commitment can confirm any more (once per process: also a reservation a crash left without its child row)
        result.Release = (local == PathState.Done || peer == PathState.Done)
                      && local != PathState.Active && peer != PathState.Active;
        return result;
    }

    /// <summary>Our own commitment's part of the round (see the class remarks).</summary>
    private async Task<PathState> RunLocalLockedAsync(ChannelModel channel, IUnitOfWork unitOfWork,
                                                      IReadOnlyList<BroadcastTransactionModel> broadcasts,
                                                      IReadOnlyList<BroadcastTransactionModel> children,
                                                      bool otherPending, uint height, RoundResult result,
                                                      CancellationToken cancellationToken)
    {
        var repository = unitOfWork.BroadcastTransactionDbRepository;
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
            return PathState.None;

        var pendingChildren = children.Where(b => b.State == BroadcastState.Pending).ToList();

        if (commitment.State != BroadcastState.Pending)
        {
            // A child of a confirmed commitment may still confirm: keep it (and its wallet inputs) until it cannot
            if (commitment.State == BroadcastState.Confirmed && pendingChildren.Count > 0
                                                             && !await ChildrenSettledAsync(
                                                                    channel.ChannelId, commitment.TransactionId,
                                                                    _builder.FindAnchorOutput(
                                                                        commitment.RawTransaction,
                                                                        channel.LocalFundingPubKey),
                                                                    commitment.ConfirmedHeight, height))
                return PathState.Active;

            // The commitment confirmed and no child can confirm any more, or the commitment can no longer confirm
            var staged = false;
            foreach (var child in pendingChildren)
                staged |= await repository.MarkAbandonedAsync(child.TransactionId);
            if (staged)
                await unitOfWork.SaveChangesAsync();

            if (commitment is { State: BroadcastState.Confirmed, ConfirmedHeight: { } confirmedHeight })
                result.Sweep = await PlanAnchorSweepAsync(channel, commitment.TransactionId,
                                                          commitment.RawTransaction, confirmedHeight,
                                                          children.Count > 0, height, cancellationToken);

            return PathState.Done;
        }

        // The peer's commitment holds the funding output in bitcoind's mempool: ours cannot get in (a parent is never
        // replaced through its package), so our children wait; the peer's is bumped through our anchor on it (NL-381)
        if (result.PeerCommitmentInMempool)
        {
            LogOnce($"{channel.ChannelId}:{commitment.TransactionId}:peer-in-mempool",
                    "Commitment {TxId} of channel {ChannelId} is not fee-bumped while the peer's commitment spends the "
                  + "funding output in the mempool", Display(commitment.TransactionId), channel.ChannelId);
            return PathState.Active;
        }

        // Whatever this round decides, the commitment and its newest child go together into a mempool (NL-380)
        result.PackageParent = commitment;

        var (deadline, stakeSat) = GetDeadlineAndStake(channel);
        var parent = new ParentCommitment(commitment.TransactionId, commitment.RawTransaction, deadline, stakeSat,
                                          false);
        var pending = await PlanChildAsync(channel, parent, pendingChildren, !otherPending, height,
                                           cancellationToken);
        if (pending is null)
        {
            result.CheckChild = LatestChild(pendingChildren);
            return PathState.Active;
        }

        await StoreChildAsync(channel.ChannelId, unitOfWork, pending);
        result.Publish = pending.Row;
        return PathState.Active;
    }

    /// <summary>
    /// Stages a planned child (and marks the row it replaces) and saves. A first child that was not stored is never
    /// published: its fresh reservation goes back at once (the exception ends the round, so RunChannelAsync never gets a
    /// result to complete).
    /// </summary>
    private async Task StoreChildAsync(ChannelId channelId, IUnitOfWork unitOfWork, PlannedChild pending)
    {
        var repository = unitOfWork.BroadcastTransactionDbRepository;
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
            try
            {
                await _feeInputSource.ReleaseAsync(channelId, CancellationToken.None);
            }
            catch (Exception releaseError)
            {
                _logger.LogError(releaseError, "Cannot release the anchor fee inputs of channel {ChannelId} after a "
                                             + "failed save", channelId);
            }

            throw;
        }
    }

    private async Task CompleteAsync(ChannelId channelId, RoundResult result, CancellationToken cancellationToken)
    {
        if (result.Publish is { } row)
        {
            // A refused child is an orphan when its commitment is not in the mempool (below its minimum fee): the pair
            // goes in as a package
            var accepted = await _blockchainMonitor.PublishAsync(row);
            if (!accepted && !(result.PackageParent is { } parent
                            && await TrySubmitPackageAsync(channelId, parent, row, cancellationToken)))
                _logger.LogWarning("Anchor child {TxId} of channel {ChannelId} was refused; it is sent again after "
                                 + "every block", Display(row.TransactionId), channelId);
        }
        else if (result is { PackageParent: { } parent, CheckChild: { } child })
        {
            await EnsureChildInMempoolAsync(channelId, parent, child, cancellationToken);
        }

        if (result.PeerChild is { } peerChild && !await _blockchainMonitor.PublishAsync(peerChild))
            _logger.LogWarning("Anchor child {TxId} of the peer's commitment (channel {ChannelId}) was refused; it is "
                             + "sent again after every block", Display(peerChild.TransactionId), channelId);

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
    /// A pending child that was not replaced this round: when bitcoind does not have it (<c>getrawtransaction</c> finds
    /// no mempool transaction), its commitment is not in the mempool either, or the child was evicted; the pair is sent
    /// again as a package. This is also the rebroadcast after a restart: the rows come back from the database and the
    /// first round sends them together (the monitor's own rebroadcast sends them one by one, which a commitment below
    /// the mempool minimum never passes).
    /// </summary>
    private async Task EnsureChildInMempoolAsync(ChannelId channelId, BroadcastTransactionModel commitment,
                                                 BroadcastTransactionModel child, CancellationToken cancellationToken)
    {
        if (_chainService is null)
            return;

        try
        {
            if (await _chainService.GetTransactionAsync(new uint256(child.TransactionId)) is not null)
            {
                lock (_feeRefusedChildren)
                    _feeRefusedChildren.Remove(child.TransactionId);
                return;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogOnce($"{channelId}:{child.TransactionId}:lookup", e,
                    "Cannot check whether bitcoind has anchor child {TxId} of channel {ChannelId}; it is sent as a "
                  + "package", Display(child.TransactionId), channelId);
        }

        await TrySubmitPackageAsync(channelId, commitment, child, cancellationToken);
    }

    /// <summary>
    /// Sends the commitment and its child with <see cref="IBitcoinChainService.SubmitPackageAsync"/> (Bitcoin Core 28+
    /// 1p1c package relay, NL-380). True when both are in bitcoind's mempool. A package refused for its fee marks the
    /// child so the next round replaces it without waiting for the RBF interval; a node without package relay is
    /// logged once (the monitor keeps sending both one by one).
    /// </summary>
    private async Task<bool> TrySubmitPackageAsync(ChannelId channelId, BroadcastTransactionModel commitment,
                                                   BroadcastTransactionModel child, CancellationToken cancellationToken)
    {
        if (_chainService is null)
        {
            LogOnce("package:nochain", "No chain service is registered: an anchor commitment below the mempool minimum "
                                     + "fee cannot be sent with its CPFP child as a package (channel {ChannelId})",
                    channelId);
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        PackageSubmitResult outcome;
        try
        {
            outcome = await _chainService.SubmitPackageAsync(Transaction.Load(commitment.RawTransaction, Network.Main),
                                                             Transaction.Load(child.RawTransaction, Network.Main));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            outcome = PackageSubmitResult.Failed(e.Message);
        }

        switch (outcome.Status)
        {
            case PackageSubmitStatus.Accepted:
                lock (_feeRefusedChildren)
                    _feeRefusedChildren.Remove(child.TransactionId);
                _logger.LogWarning("Commitment {CommitmentTxId} of channel {ChannelId} entered the mempool with its "
                                 + "anchor child {TxId} as a package (package feerate {Feerate} BTC/kvB)",
                                   Display(commitment.TransactionId), channelId, Display(child.TransactionId),
                                   outcome.PackageFeerateBtcPerKvb);
                return true;
            case PackageSubmitStatus.Unsupported:
                LogOnce("package:unsupported",
                        "bitcoind cannot take the anchor child {TxId} of channel {ChannelId} with its commitment as a "
                      + "package ({Reason}); both are sent one by one, so a commitment below the mempool minimum fee "
                      + "stays out (Bitcoin Core 28 or newer is needed)", Display(child.TransactionId), channelId,
                        outcome.Message);
                return false;
            case PackageSubmitStatus.Rejected:
                if (outcome.IsFeeRefusal)
                    lock (_feeRefusedChildren)
                        _feeRefusedChildren.Add(child.TransactionId);
                LogOnce($"{channelId}:{child.TransactionId}:package",
                        "bitcoind refused commitment {CommitmentTxId} of channel {ChannelId} with its anchor child "
                      + "{TxId} as a package ({Reason}){Next}", Display(commitment.TransactionId), channelId,
                        Display(child.TransactionId), outcome.Describe(),
                        outcome.IsFeeRefusal ? "; the child is replaced with a higher fee at the next block" : "");
                return false;
            default:
                LogOnce($"{channelId}:{child.TransactionId}:package-failed",
                        "Cannot send commitment {CommitmentTxId} of channel {ChannelId} with its anchor child {TxId} as "
                      + "a package ({Reason}); retried at the next block", Display(commitment.TransactionId),
                        channelId, Display(child.TransactionId), outcome.Message);
                return false;
        }
    }

    private bool IsFeeRefused(TxId childTxId)
    {
        lock (_feeRefusedChildren)
            return _feeRefusedChildren.Contains(childTxId);
    }

    private static BroadcastTransactionModel? LatestChild(IEnumerable<BroadcastTransactionModel> pendingChildren) =>
        pendingChildren.OrderByDescending(b => b.FirstBroadcastHeight)
                       .ThenByDescending(b => b.CreatedAt)
                       .FirstOrDefault();

    /// <summary>The commitment a child's anchor input (input 0) spends; null when the row does not parse.</summary>
    private static TxId? ParentOf(BroadcastTransactionModel child)
    {
        try
        {
            var tx = Transaction.Load(child.RawTransaction, Network.Main);
            if (tx.Inputs.Count == 0)
                return null;

            return new TxId(tx.Inputs[0].PrevOut.Hash.ToBytes());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The output index of the anchor the children spend (their input 0; every child of a commitment spends
    /// the same anchor).</summary>
    private static uint? AnchorVoutOf(IEnumerable<BroadcastTransactionModel> children)
    {
        foreach (var child in children)
        {
            try
            {
                var tx = Transaction.Load(child.RawTransaction, Network.Main);
                if (tx.Inputs.Count > 0)
                    return tx.Inputs[0].PrevOut.N;
            }
            catch (FormatException)
            {
                // Next row
            }
        }

        return null;
    }

    /// <summary>
    /// The first child or a replacement for a pending commitment (ours or the peer's), or null when none is due or
    /// possible. Called under the channel's lock; stages nothing. <paramref name="mayRelease"/> is false while a child of
    /// the channel's other commitment is pending: the reservation it spends is shared, so a failure here never
    /// releases it.
    /// </summary>
    private async Task<PlannedChild?> PlanChildAsync(ChannelModel channel, ParentCommitment parent,
                                                     IReadOnlyList<BroadcastTransactionModel> pendingChildren,
                                                     bool mayRelease, uint height,
                                                     CancellationToken cancellationToken)
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

        var fundingPubKey = channel.LocalFundingPubKey;
        var commitmentTx = Transaction.Load(parent.RawTransaction, Network.Main);
        if (_builder.FindAnchorOutput(parent.RawTransaction, fundingPubKey) is not { } anchorVout)
        {
            LogOnce($"{channelId}:{parent.TxId}:anchor",
                    "Commitment {TxId} of channel {ChannelId} has no anchor of ours; it cannot be fee-bumped",
                    Display(parent.TxId), channelId);
            return null;
        }

        var outputsSat = commitmentTx.Outputs.Aggregate(0UL, (sum, o) => sum + (ulong)o.Value.Satoshi);
        var fundingSat = (ulong)funding.Amount.Satoshi;
        if (outputsSat > fundingSat)
            return null;

        var commitmentFee = fundingSat - outputsSat;
        var commitmentWeight = GetWeight(commitmentTx);
        var deadline = parent.Deadline;
        var stakeSat = parent.StakeSat;
        if (deadline is null && (stakeSat == 0 || parent.IsPeers))
        {
            // The peer's commitment is its to pay for; we bump it only for HTLCs that must be resolved in time
            LogOnce($"{channelId}:{parent.TxId}:nostake",
                    "Commitment {TxId} of channel {ChannelId} carries {What} and no HTLC; it is not fee-bumped",
                    Display(parent.TxId), channelId, parent.IsPeers ? "the peer's balance" : "nothing of ours");
            return null;
        }

        var target = _policy.GetConfirmationTarget(height, deadline);
        var estimate = await FeeEstimates.GetForTargetAsync(_feeService, target, _logger, cancellationToken);
        estimate = await FloorAtMempoolMinimumAsync(estimate);
        var cap = _policy.GetFeeCap(stakeSat, deadline is not null);
        var anchor = new AnchorOutpoint(parent.TxId, anchorVout, fundingPubKey);

        var latest = LatestChild(pendingChildren);
        if (latest is null)
            return await PlanFirstChildAsync(channel, anchor, commitmentFee, commitmentWeight, estimate, cap, deadline,
                                             mayRelease, height, cancellationToken);

        // A package bitcoind refused for its fee is in no mempool: waiting for the RBF interval gains nothing, and the
        // estimate it paid is below what the mempool takes (NL-380)
        var refusedForFee = IsFeeRefused(latest.TransactionId);

        // Past the deadline the commitment still has to confirm (to_local, the HTLC transactions): keep bumping every
        // RbfIntervalBlocks, as just before it (SweepFeePolicy stops at a sweep's deadline)
        var bumpDeadline = deadline is { } d && height >= d ? height + 1 : deadline;
        if (!refusedForFee && !_policy.FeePolicy.ShouldBump(latest.FirstBroadcastHeight, height, bumpDeadline))
            return null;

        var oldTx = Transaction.Load(latest.RawTransaction, Network.Main);
        var oldWeight = GetWeight(oldTx);
        var oldFeeLower = (ulong)latest.FeeratePerKw * (ulong)oldWeight / 1000;
        // With a deadline (an untrimmed HTLC) a package still unconfirmed after RbfIntervalBlocks is bumped by at least
        // the BIP 125 minimum even when it pays the estimate, as SweepScheduler does for claims: the estimate lags when
        // blocks keep leaving the package out, and the cap bounds the escalation. Without one it is kept while it pays
        if (!refusedForFee && deadline is null
                           && _policy.PackagePays(commitmentFee, commitmentWeight, oldFeeLower, oldWeight, estimate))
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

        _logger.LogWarning("Anchor child {TxId} of {Whose} commitment {CommitmentTxId} (channel {ChannelId}) is "
                         + "unconfirmed since height {Since} (deadline {Deadline}); replacing it with {NewTxId}: fee "
                         + "{OldFee} -> {NewFee} sat, package {Package} sat/kw (target {Target} blocks{Capped})",
                           Display(latest.TransactionId), parent.IsPeers ? "the peer's" : "our",
                           Display(parent.TxId), channelId, latest.FirstBroadcastHeight, deadline,
                           Display(signed.Value.Transaction.TxId), oldFeeLower, decision.FeeSat,
                           decision.PackageFeeratePerKw, target, decision.Capped ? ", capped" : "");
        var row = new BroadcastTransactionModel(signed.Value.Transaction, BroadcastPurpose.AnchorCpfp, channelId,
                                                height, signed.Value.FeeratePerKw, latest.TransactionId);
        return new PlannedChild(row, latest.TransactionId, false);
    }

    /// <summary>
    /// The estimate raised to bitcoind's current mempool minimum: a commitment that pays the estimate but not
    /// that minimum is refused alone, so it needs a child (and a package) all the same, and a replacement after a fee
    /// refusal targets at least what the mempool takes. The estimate unchanged when bitcoind cannot tell.
    /// </summary>
    private async Task<uint> FloorAtMempoolMinimumAsync(uint estimate)
    {
        if (_chainService is null)
            return estimate;

        uint? minimum;
        try
        {
            minimum = await _chainService.GetMempoolMinFeeRatePerKwAsync();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return estimate;
        }

        return minimum is { } floor && floor > estimate ? floor : estimate;
    }

    private async Task<PlannedChild?> PlanFirstChildAsync(ChannelModel channel, AnchorOutpoint anchor,
                                                          ulong commitmentFee, long commitmentWeight, uint estimate,
                                                          ulong cap, uint? deadline, bool mayRelease, uint height,
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
            if (mayRelease)
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
            if (mayRelease)
                await _feeInputSource.ReleaseAsync(channelId, cancellationToken);
            return null;
        }

        var signed = SignChild(channelId, anchor, walletInputs, changeScript, decision.FeeSat);
        if (signed is null)
        {
            if (mayRelease)
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
        return new PlannedChild(row, null, mayRelease);
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
            // The anchor's prevout, which a P2TR wallet input's BIP 341 signature commits to
            _lightningSigner.SignWalletTransaction(
                walletSigned,
                [
                    new SpentOutput(anchor.TxId, anchor.OutputIndex, TransactionConstants.AnchorOutputAmount,
                                    _builder.GetAnchorScriptPubKey(anchor.FundingPubKey))
                ]);
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

    /// <summary>
    /// The anchor sweep of a confirmed commitment (ours or the peer's), once due and economical (and only once per
    /// process). <paramref name="anyChild"/>: a child of ours ever spent our anchor on it.
    /// </summary>
    private async Task<SignedTransaction?> PlanAnchorSweepAsync(ChannelModel channel, TxId commitmentTxId,
                                                                byte[] commitmentTransaction, uint confirmedHeight,
                                                                bool anyChild, uint height,
                                                                CancellationToken cancellationToken)
    {
        if (!IsSweepDue(confirmedHeight, height))
            return null;

        lock (_sweptCommitments)
        {
            if (!_sweptCommitments.Add(commitmentTxId))
                return null;
        }

        var anchors = new List<AnchorOutpoint>();
        var ours = channel.LocalFundingPubKey;
        if (_builder.FindAnchorOutput(commitmentTransaction, ours) is { } ourVout)
            anchors.Add(new AnchorOutpoint(commitmentTxId, ourVout, ours));
        if (channel.RemoteFundingPubKey is { } theirs
         && _builder.FindAnchorOutput(commitmentTransaction, theirs) is { } theirVout)
            anchors.Add(new AnchorOutpoint(commitmentTxId, theirVout, theirs));

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
                    _sweptCommitments.Remove(commitmentTxId);
                _logger.LogInformation("Cannot check the anchors of commitment {TxId} (channel {ChannelId}): {Reason}",
                                       Display(commitmentTxId), channel.ChannelId, e.Message);
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
                                   Display(commitmentTxId), channel.ChannelId, decision.FeeSat,
                                   decision.FeeratePerKw, AnchorCpfpPolicy.AnchorSat * (ulong)anchors.Count);
            return null;
        }

        return _builder.BuildAnchorSweep(anchors, destination, decision.FeeSat);
    }

    /// <summary>The next block can spend the anchors of a commitment confirmed at <paramref name="confirmedHeight"/>
    /// with <c>nSequence</c> 16, and sweeping is on.</summary>
    private bool IsSweepDue(uint confirmedHeight, uint height) =>
        _options.SweepAnchors && height + 1 >= confirmedHeight + AnchorChildTransactionBuilder.AnchorCsvSequence;

    private bool IsSwept(TxId commitmentTxId)
    {
        lock (_sweptCommitments)
            return _sweptCommitments.Contains(commitmentTxId);
    }

    /// <summary>
    /// Whether the pending children of a confirmed commitment can no longer confirm, so their wallet inputs may go back:
    /// the wait (<see cref="AnchorCpfpOptions.ConfirmedCommitmentChildWaitBlocks"/>) passed, or our anchor at
    /// <paramref name="anchorVout"/>, which every child spends, is spent in the active chain and the monitor has
    /// processed the block that spent it (seen in an earlier round at a bitcoind tip at or below this round's height;
    /// this round's rows were read after that processing, so a child that won is already <c>Confirmed</c>).
    /// </summary>
    private async Task<bool> ChildrenSettledAsync(ChannelId channelId, TxId commitmentTxId, uint? anchorVout,
                                                  uint? confirmedHeight, uint height)
    {
        if (confirmedHeight is { } confirmed
         && height >= (ulong)confirmed + _options.ConfirmedCommitmentChildWaitBlocks)
        {
            _logger.LogWarning("Commitment {TxId} of channel {ChannelId} confirmed at {Height} but its anchor child is "
                             + "still unconfirmed; it is abandoned and its wallet inputs released",
                               Display(commitmentTxId), channelId, confirmed);
            return true;
        }

        if (_chainService is null || anchorVout is not { } vout)
            return false;

        lock (_anchorSpentSeenAtTip)
        {
            if (_anchorSpentSeenAtTip.TryGetValue(commitmentTxId, out var seenAtTip) && height >= seenAtTip)
            {
                _anchorSpentSeenAtTip.Remove(commitmentTxId);
                return true;
            }
        }

        try
        {
            var outpoint = ToOutPoint(commitmentTxId, vout);
            if (await _chainService.GetConfirmedUnspentOutputAsync(outpoint) is not null)
            {
                lock (_anchorSpentSeenAtTip)
                    _anchorSpentSeenAtTip.Remove(commitmentTxId);
                return false;
            }

            var tip = await _chainService.GetCurrentBlockHeightAsync();
            lock (_anchorSpentSeenAtTip)
                _anchorSpentSeenAtTip.TryAdd(commitmentTxId, tip);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogOnce($"{channelId}:{commitmentTxId}:anchor-spent-check", e,
                    "Cannot check the anchor of confirmed commitment {TxId} (channel {ChannelId}); its child keeps its "
                  + "wallet inputs", Display(commitmentTxId), channelId);
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

    /// <summary>What one commitment's part of a round leaves: nothing to do with the reservation, a child that may still
    /// confirm (keep it), or no child that can confirm any more (it may go back).</summary>
    private enum PathState
    {
        None,
        Active,
        Done
    }

    private sealed class RoundResult
    {
        public BroadcastTransactionModel? Publish { get; set; }

        /// <summary>The pending commitment a child is sent with as a package when bitcoind refuses the child.</summary>
        public BroadcastTransactionModel? PackageParent { get; set; }

        /// <summary>The newest pending child, not replaced this round, to check in bitcoind's mempool.</summary>
        public BroadcastTransactionModel? CheckChild { get; set; }

        /// <summary>A new child (or replacement) of the peer's commitment (NL-381).</summary>
        public BroadcastTransactionModel? PeerChild { get; set; }

        /// <summary>The peer's commitment spends the funding output in bitcoind's mempool this round.</summary>
        public bool PeerCommitmentInMempool { get; set; }

        public bool Release { get; set; }
        public SignedTransaction? Sweep { get; set; }
    }

    /// <summary>The commitment a child pays for: its raw bytes, the deadline and our stake on it.</summary>
    private sealed record ParentCommitment(TxId TxId, byte[] RawTransaction, uint? Deadline, ulong StakeSat,
                                           bool IsPeers);

    private sealed record PlannedChild(BroadcastTransactionModel Row, TxId? Replaces, bool ReleaseOnFailure);
}
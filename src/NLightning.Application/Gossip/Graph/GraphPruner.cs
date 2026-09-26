using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Graph;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Graph;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Domain.Onchain.Events;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// Removes what the graph no longer needs (plan BOLT7 G2-T5, B7-PR-01, B7-PR-02, B7-CA-05), driven by the chain
/// monitor's blocks.
/// </summary>
/// <remarks>
/// <para>
/// Spends (plan D4): every processed block's spent outpoints (<see cref="IBlockchainMonitor.OnBlockInputs"/>) are
/// checked against the funding outpoints of the graph (<see cref="IGraphStore.TryGetChannelByFundingOutpoint"/>, no
/// bitcoind call, no watch row). A spent channel gets <see cref="GraphChannel.SpentAtHeight"/> and is removed once
/// the block at that height plus <see cref="GossipGraphOptions.SpentChannelRetentionBlocks"/> (BOLT 7: 72) is
/// processed. A reorg (<see cref="Domain.Onchain.Interfaces.IOutpointWatcher.OnBlockDisconnected"/>) clears the
/// spends above the fork point; the new branch marks them again if it spends the output too. Our own channels follow
/// the same rule: a spent funding output means the channel is closed. The spends of the last
/// <see cref="RecentSpendBlocks"/> blocks are checked again at every block, so a channel stored just after the block
/// that spent it (its chain check ran at the previous tip) is marked too. A channel whose funding block a reorg
/// disconnected is looked up again at the next blocks and marked spent when its output is no longer there (BOLT 7:
/// "spent or reorganized out").
/// </para>
/// <para>
/// Funding txids are not persisted, so after a restart the pruner looks up the channels without one
/// (<see cref="IFundingOutputLookup.LookupAsync"/>): found records the txid, an output spent while we were down is
/// marked spent at the last processed block, a transient answer is retried at the next block, and an answer that
/// cannot change (pruned block, index out of range) is not asked again. Such a channel is left to the stale rule.
/// The lookups wait for the first block when the monitor has no height yet (it loads it only when it starts).
/// </para>
/// <para>
/// Stale (B7-PR-02, a local policy): a channel whose newest update of each direction (its announcement's arrival
/// when it has none) is older than <see cref="GossipGraphOptions.DeleteStaleAfter"/> (by the
/// <see cref="TimeProvider"/>, checked at every block) is removed, except our own channels (announced by us, or with
/// our node as an end). Routing already skips a channel stale for <see cref="GossipGraphOptions.StaleAfter"/>
/// (<see cref="GraphChannel.IsStale"/>).
/// </para>
/// <para>
/// Nodes: a node announcement whose node is left without a channel is removed (B7-PR-01 MAY), except ours.
/// </para>
/// <para>
/// The monitor's handlers only enqueue; one loop applies the blocks in order and writes the store's pending changes
/// after each block that changed it. <see cref="Start"/> (the host, before the monitor starts) and
/// <see cref="StopAsync"/>. Nothing runs while the graph is disabled (<see cref="GossipGraphOptions.IsEnabledFor"/>).
/// </para>
/// </remarks>
public sealed class GraphPruner : IAsyncDisposable, IDisposable
{
    private readonly IGraphStore _store;
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IFundingOutputLookup _fundingOutputLookup;
    private readonly ILogger<GraphPruner> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly GossipGraphOptions _options;
    private readonly NodeOptions _nodeOptions;
    private readonly CompactPubKey? _ourNodeId;

    private readonly Channel<PrunerWork> _queue = Channel.CreateUnbounded<PrunerWork>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    private readonly HashSet<ShortChannelId> _unresolvable = [];
    private readonly HashSet<ShortChannelId> _reorgRecheck = [];
    private readonly LinkedList<(uint Height, IReadOnlyList<(TxId TransactionId, uint OutputIndex)> Spent)>
        _recentSpends = new();
    private readonly Lock _startLock = new();
    private readonly CancellationTokenSource _stopCts = new();

    private Task? _loop;
    private int _pending;
    private bool _stopped;

    public GraphPruner(IGraphStore store, IBlockchainMonitor blockchainMonitor,
                       IFundingOutputLookup fundingOutputLookup, IOptions<GossipGraphOptions> options,
                       IOptions<NodeOptions> nodeOptions, ILogger<GraphPruner> logger,
                       TimeProvider? timeProvider = null, ISecureKeyManager? secureKeyManager = null)
    {
        _store = store;
        _blockchainMonitor = blockchainMonitor;
        _fundingOutputLookup = fundingOutputLookup;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options.Value;
        _nodeOptions = nodeOptions.Value;
        _ourNodeId = secureKeyManager?.GetNodePubKey();
    }

    /// <summary>The effective <c>Gossip:Enabled</c> switch.</summary>
    public bool IsEnabled => _options.IsEnabledFor(_nodeOptions.BitcoinNetwork);

    /// <summary>
    /// Blocks whose spent outpoints are checked again at every block: a channel whose chain check saw its funding
    /// output unspent at the tip, but that was stored only after the pruner applied the next block (which spent it),
    /// is still marked spent at that block.
    /// </summary>
    internal const int RecentSpendBlocks = 6;

    /// <summary>The highest block height applied.</summary>
    public uint LastHeight { get; private set; }

    /// <summary>
    /// Subscribes to the monitor and starts the loop, which loads the graph and looks up the missing funding txids
    /// first (once; nothing when the graph is disabled). Call it before the monitor starts, so no block is missed.
    /// </summary>
    public void Start()
    {
        lock (_startLock)
        {
            if (_loop is not null || _stopped || !IsEnabled)
                return;

            _blockchainMonitor.OnBlockInputs += HandleBlockInputs;
            _blockchainMonitor.OnBlockDisconnected += HandleBlockDisconnected;
            Interlocked.Increment(ref _pending);
            _loop = Task.Run(() => RunAsync(_stopCts.Token));
        }
    }

    /// <summary>Unsubscribes and stops the loop (blocks still queued are dropped; the next start catches up).</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_startLock)
        {
            _stopped = true;
            loop = _loop;
            if (loop is null)
                return;

            _blockchainMonitor.OnBlockInputs -= HandleBlockInputs;
            _blockchainMonitor.OnBlockDisconnected -= HandleBlockDisconnected;
            _queue.Writer.TryComplete();
            if (!_stopCts.IsCancellationRequested)
                _stopCts.Cancel();
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopCts.Dispose();
    }

    /// <summary>Unsubscribes and cancels the loop without waiting for it (a container disposed synchronously).</summary>
    public void Dispose()
    {
        lock (_startLock)
        {
            _stopped = true;
            if (_loop is not null)
            {
                _blockchainMonitor.OnBlockInputs -= HandleBlockInputs;
                _blockchainMonitor.OnBlockDisconnected -= HandleBlockDisconnected;
                _queue.Writer.TryComplete();
            }

            if (!_stopCts.IsCancellationRequested)
                _stopCts.Cancel();
        }
    }

    /// <summary>Completes once the startup work and every queued block are applied (tests).</summary>
    public async Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (Volatile.Read(ref _pending) > 0)
            await Task.Delay(10, cancellationToken);
    }

    /// <summary>
    /// Applies one block: marks the graph channels whose funding output it spends, then removes what expired at
    /// <paramref name="height"/> (spent 72 blocks ago, stale, nodes left without channels). Idempotent.
    /// </summary>
    /// <returns>The number of changes (marks and removals).</returns>
    internal int ApplyBlock(uint height, IReadOnlyList<(TxId TransactionId, uint OutputIndex)> spentOutpoints)
    {
        ArgumentNullException.ThrowIfNull(spentOutpoints);
        if (height > LastHeight)
            LastHeight = height;

        // A replayed block replaces its earlier entry; only the last few blocks are kept
        for (var node = _recentSpends.First; node is not null; node = node.Next)
        {
            if (node.Value.Height != height)
                continue;

            _recentSpends.Remove(node);
            break;
        }

        _recentSpends.AddLast((height, spentOutpoints));
        while (_recentSpends.Count > RecentSpendBlocks)
            _recentSpends.RemoveFirst();

        // Every recent block (this one included), so a channel stored after its spending block was applied is caught
        var changes = 0;
        foreach (var (spentHeight, spent) in _recentSpends)
            changes += MarkSpentOutpoints(spentHeight, spent);

        return changes + Prune(height);
    }

    private int MarkSpentOutpoints(uint height, IReadOnlyList<(TxId TransactionId, uint OutputIndex)> spentOutpoints)
    {
        var changes = 0;
        foreach (var (transactionId, outputIndex) in spentOutpoints)
        {
            if (!_store.TryGetChannelByFundingOutpoint(transactionId, outputIndex, out var shortChannelId)
             || !_store.TryGetChannel(shortChannelId, out var channel) || channel.SpentAtHeight is not null)
                continue;

            _store.MarkSpent(shortChannelId, height);
            changes++;
            _logger.LogInformation("Graph channel {ShortChannelId} closed: its funding output was spent at block {Height}",
                                   shortChannelId, height);
        }

        return changes;
    }

    /// <summary>
    /// A reorg to <paramref name="forkHeight"/>: the spends above it are cleared, and every channel whose funding block
    /// is above it is checked again at the next blocks (<see cref="RecheckReorgedFundingAsync"/>).
    /// </summary>
    internal int ApplyDisconnect(uint forkHeight)
    {
        if (LastHeight > forkHeight)
            LastHeight = forkHeight;

        for (var node = _recentSpends.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.Height > forkHeight)
                _recentSpends.Remove(node);
            node = next;
        }

        // BOLT 7: a channel whose funding output was reorganized out is forgotten after 72 blocks like a spent one
        foreach (var channel in _store.GetSnapshot().Channels)
        {
            if (channel.ShortChannelId.BlockHeight > forkHeight && channel.SpentAtHeight is null)
                _reorgRecheck.Add(channel.ShortChannelId);
        }

        var cleared = _store.ClearSpentAbove(forkHeight);
        if (cleared > 0)
            _logger.LogInformation("Reorg to block {Height}: {Count} graph channels are unspent again", forkHeight,
                                   cleared);
        return cleared;
    }

    /// <summary>
    /// Looks up the funding txid of every channel that lacks one (see the remarks); a spent output is marked spent at
    /// <paramref name="height"/>.
    /// </summary>
    /// <returns>The number of channels still unresolved for a transient reason.</returns>
    internal async Task<int> ResolveFundingTxIdsAsync(uint height, CancellationToken cancellationToken)
    {
        var transient = 0;
        foreach (var shortChannelId in _store.GetChannelsWithoutFundingTxId())
        {
            if (_unresolvable.Contains(shortChannelId)
             || !_store.TryGetChannel(shortChannelId, out var channel) || channel.SpentAtHeight is not null)
                continue;

            var result = await _fundingOutputLookup.LookupAsync(shortChannelId, cancellationToken);
            if (result is { IsFound: true, TransactionId: { } transactionId })
            {
                _store.TrySetFundingTxId(shortChannelId, transactionId);
            }
            else if (result.Status == FundingOutputStatus.OutputSpentOrMissing)
            {
                _store.MarkSpent(shortChannelId, height);
                _logger.LogInformation(
                    "Graph channel {ShortChannelId} closed while we were not following the chain; marked spent at block {Height}",
                    shortChannelId, height);
            }
            else if (result.IsTransient)
            {
                transient++;
            }
            else
            {
                _unresolvable.Add(shortChannelId);
                _logger.LogDebug("Graph channel {ShortChannelId}: funding output not found ({Status}); left to the stale rule",
                                 shortChannelId, result.Status);
            }
        }

        return transient;
    }

    /// <summary>
    /// Looks up again the funding output of every channel whose funding block a reorg disconnected: still at its short
    /// channel id with the same txid → kept; gone (another transaction or no output there, or spent) → marked spent at
    /// <paramref name="height"/>, so it is forgotten 72 blocks later (BOLT 7 "spent or reorganized out"); a transient
    /// answer (the new branch is not that high yet, bitcoind down) → asked again at the next block.
    /// </summary>
    /// <returns>The number of channels marked spent.</returns>
    internal async Task<int> RecheckReorgedFundingAsync(uint height, CancellationToken cancellationToken)
    {
        var marked = 0;
        foreach (var shortChannelId in _reorgRecheck.ToList())
        {
            if (!_store.TryGetChannel(shortChannelId, out var channel) || channel.SpentAtHeight is not null)
            {
                _reorgRecheck.Remove(shortChannelId);
                continue;
            }

            var result = await _fundingOutputLookup.LookupAsync(shortChannelId, cancellationToken);
            if (result.IsTransient)
                continue;

            _reorgRecheck.Remove(shortChannelId);
            var gone = result.Status switch
            {
                FundingOutputStatus.Found => _store.TryGetFundingTxId(shortChannelId, out var known)
                                          && result.TransactionId is { } found && found != known,
                FundingOutputStatus.OutputSpentOrMissing or FundingOutputStatus.TransactionIndexOutOfRange => true,
                _ => false
            };

            if (!gone)
            {
                if (result is { IsFound: true, TransactionId: { } transactionId })
                    _store.TrySetFundingTxId(shortChannelId, transactionId);
                continue;
            }

            _store.MarkSpent(shortChannelId, height);
            marked++;
            _logger.LogInformation(
                "Graph channel {ShortChannelId}: its funding output left the chain in a reorg ({Status}); marked spent at block {Height}",
                shortChannelId, result.Status, height);
        }

        return marked;
    }

    private int Prune(uint height)
    {
        var now = _timeProvider.GetUtcNow();
        var nowSeconds = (ulong)Math.Max(0, now.ToUnixTimeSeconds());
        var staleSeconds = (ulong)_options.DeleteStaleAfter.TotalSeconds;
        var snapshot = _store.GetSnapshot();
        var removed = 0;

        foreach (var channel in snapshot.Channels)
        {
            string? reason = null;
            if (channel.SpentAtHeight is { } spentAt
             && height >= (ulong)spentAt + _options.SpentChannelRetentionBlocks)
                reason = $"spent at block {spentAt}";
            else if (!IsOurs(channel) && LastActivity(channel) is { } last && last + staleSeconds < nowSeconds)
                reason = "stale";

            if (reason is null || !_store.RemoveChannel(channel.ShortChannelId))
                continue;

            _unresolvable.Remove(channel.ShortChannelId);
            removed++;
            _logger.LogDebug("Removed graph channel {ShortChannelId} ({Reason})", channel.ShortChannelId, reason);
        }

        foreach (var node in snapshot.Nodes)
        {
            if (node.NodeId == _ourNodeId || _store.NodeHasChannels(node.NodeId) || !_store.RemoveNode(node.NodeId))
                continue;

            removed++;
            _logger.LogDebug("Removed graph node {NodeId}: it has no channel left", node.NodeId);
        }

        if (removed > 0)
            _logger.LogInformation("Graph pruned at block {Height}: {Count} channels and nodes removed", height,
                                   removed);
        return removed;
    }

    private bool IsOurs(GraphChannel channel) =>
        channel.Verification == GraphChannelVerification.Own
     || (_ourNodeId is { } ours && (channel.NodeId1 == ours || channel.NodeId2 == ours));

    /// <summary>The newest update of either direction, else when the announcement arrived (unix seconds).</summary>
    private ulong? LastActivity(GraphChannel channel)
    {
        uint? newest = (channel.Policy1, channel.Policy2) switch
        {
            (null, null) => null,
            ({ } p1, null) => p1.Timestamp,
            (null, { } p2) => p2.Timestamp,
            ({ } p1, { } p2) => Math.Max(p1.Timestamp, p2.Timestamp)
        };
        if (newest is { } timestamp)
            return timestamp;

        return _store.TryGetChannelReceivedAt(channel.ShortChannelId, out var receivedAt)
                   ? (ulong)Math.Max(0, receivedAt.ToUnixTimeSeconds())
                   : null;
    }

    private void HandleBlockInputs(object? sender, BlockInputsEventArgs args) =>
        Enqueue(new PrunerWork(args.Height, args.SpentOutpoints, null));

    private void HandleBlockDisconnected(object? sender, BlockDisconnectedEventArgs args) =>
        Enqueue(new PrunerWork(args.Height, [], args.ForkHeight));

    private void Enqueue(PrunerWork work)
    {
        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(work))
            Interlocked.Decrement(ref _pending);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var retryResolution = false;
        try
        {
            try
            {
                await _store.LoadAsync(cancellationToken);
                LastHeight = Math.Max(LastHeight, _blockchainMonitor.LastProcessedBlockHeight);

                // The monitor loads its height only when it starts (after us): without a real height a spend found
                // now would be pinned at block 0 and removed at once, so the lookups wait for the first block
                if (LastHeight > 0)
                {
                    retryResolution = await ResolveFundingTxIdsAsync(LastHeight, cancellationToken) > 0;
                    await FlushIfChangedAsync(cancellationToken);
                }
                else
                {
                    retryResolution = true;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                retryResolution = true;
                _logger.LogWarning(e, "The graph pruner could not load the graph or look up its funding outputs");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }

            await foreach (var work in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    if (work.ForkHeight is { } forkHeight)
                    {
                        ApplyDisconnect(forkHeight);
                    }
                    else
                    {
                        if (retryResolution)
                            retryResolution = await ResolveFundingTxIdsAsync(work.Height, cancellationToken) > 0;
                        if (_reorgRecheck.Count > 0)
                            await RecheckReorgedFundingAsync(work.Height, cancellationToken);
                        ApplyBlock(work.Height, work.SpentOutpoints);

                        // Channels added since (our own before its txid was known, a restart) get their txid next
                        retryResolution |= _store.GetChannelsWithoutFundingTxId().Any(s => !_unresolvable.Contains(s));
                    }

                    await FlushIfChangedAsync(cancellationToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _logger.LogError(e, "The graph pruner failed on block {Height}", work.Height);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping
        }
    }

    private async Task FlushIfChangedAsync(CancellationToken cancellationToken)
    {
        if (_store.PendingChanges > 0)
            await _store.FlushAsync(cancellationToken);
    }

    private sealed record PrunerWork(
        uint Height,
        IReadOnlyList<(TxId TransactionId, uint OutputIndex)> SpentOutpoints,
        uint? ForkHeight);
}
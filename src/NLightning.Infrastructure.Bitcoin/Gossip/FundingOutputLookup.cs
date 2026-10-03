using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Gossip;

using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Models;
using Domain.Money;
using Domain.Onchain.Events;
using Domain.Onchain.Interfaces;
using Wallet.Interfaces;

/// <summary>
/// SCID → funding output over bitcoind (BOLT 7 plan §3.4, D3, G2-T2): <c>getblockhash(height)</c>, the block's txid
/// list (<c>getblock &lt;hash&gt; 1</c>, kept in an LRU cache by height), the txid at the SCID's index, then
/// <c>gettxout(txid, vout)</c> with the mempool (unspent, script, amount in one call). No txindex needed.
/// </summary>
/// <remarks>
/// <para>
/// Bounded by <see cref="FundingOutputLookupOptions.ChainLookupConcurrency"/> lookups at once,
/// <see cref="FundingOutputLookupOptions.ChainLookupsPerSecond"/> started per second and
/// <see cref="FundingOutputLookupOptions.ChainRpcsPerSecond"/> bitcoind RPCs per second (one lookup costs 3-5 RPCs,
/// NL-346). A cached txid list is used only
/// while <c>getblockhash</c> still names the same block, and every cached height at or above a disconnected block is
/// dropped on <see cref="IOutpointWatcher.OnBlockDisconnected"/>, so a reorg never serves a stale list.
/// </para>
/// <para>
/// When <c>gettxout</c> reports the output at another height than the SCID's (a reorg between the two calls, or the
/// tip moving between <c>gettxout</c> and <c>getblockcount</c>), the lookup drops the cached height and tries once
/// more, then answers <see cref="FundingOutputStatus.ChainMoved"/>. The same goes when <c>getblockhash</c> names
/// another block after <c>gettxout</c> than the one whose txid list was read (a reorg that could have mined the
/// funding tx at the same height but another index).
/// </para>
/// <para>
/// An output spent only by a mempool transaction is <see cref="FundingOutputStatus.OutputSpentInMempool"/> (a second
/// <c>gettxout</c> without the mempool), which is transient like <see cref="FundingOutputStatus.BlockNotFound"/>. That
/// answer can only change with a new block (the close confirming, or leaving the mempool), so it is kept per short
/// channel id and given again without asking bitcoind until a block comes (NL-414): with an
/// <see cref="IBlockchainMonitor"/> as the outpoint watcher, until its next <c>OnNewBlockDetected</c>; without one,
/// while <c>getblockcount</c> still names the tip it was read at (one RPC instead of four). Either way at most
/// <see cref="FundingOutputLookupOptions.MempoolSpentRecheckInterval"/>.
/// </para>
/// <para>
/// <see cref="IGossipPendingChannels"/> (NL-415): a short channel id is pending while a lookup of it waits or runs,
/// and while its kept mempool answer holds, so the gossip sync does not download its announcement again meanwhile.
/// </para>
/// <para>
/// With an <see cref="IFundingTxIdSource"/> (<c>Gossip:FundingTxIdSource = Esplora</c>, D12 for pruned nodes) the txid
/// at the position comes from that source, proven against our node's header, instead of bitcoind's txid list; the
/// output is still read from our node's <c>gettxout</c>. Without one, a block bitcoind cannot serve (pruned) logs a
/// one-time hint to configure it.
/// </para>
/// </remarks>
public sealed class FundingOutputLookup : IFundingOutputLookup, IGossipPendingChannels, IDisposable
{
    private readonly IBitcoinChainService _chain;
    private readonly ILogger<FundingOutputLookup> _logger;
    private readonly IOutpointWatcher? _outpointWatcher;
    private readonly SemaphoreSlim _concurrency;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly TokenBucketRateLimiter _rpcLimiter;
    private readonly int _cacheCapacity;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _mempoolSpentRecheckInterval;
    private readonly IBlockchainMonitor? _blockMonitor;
    private readonly ConcurrentDictionary<ShortChannelId, int> _inFlight = new();
    private readonly ConcurrentDictionary<ShortChannelId, MempoolSpentAnswer> _mempoolSpent = new();
    private long _lastKnownTip = -1;
    private readonly IFundingTxIdSource? _txIdSource;
    private int _prunedHintLogged;

    private readonly Lock _cacheGate = new();
    private readonly Dictionary<uint, LinkedListNode<CachedBlock>> _cache = new();
    private readonly LinkedList<CachedBlock> _lru = new();

    public FundingOutputLookup(IBitcoinChainService chain, ILogger<FundingOutputLookup> logger,
                               IOptions<FundingOutputLookupOptions>? options = null,
                               TimeProvider? timeProvider = null, IOutpointWatcher? outpointWatcher = null,
                               IFundingTxIdSource? txIdSource = null)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(logger);

        var settings = options?.Value ?? new FundingOutputLookupOptions();
        var errors = settings.GetValidationErrors();
        if (errors.Count > 0)
            throw new ArgumentException($"Invalid funding output lookup options: {string.Join("; ", errors)}",
                                        nameof(options));

        _chain = chain;
        _logger = logger;
        _concurrency = new SemaphoreSlim(settings.ChainLookupConcurrency, settings.ChainLookupConcurrency);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rateLimiter = new TokenBucketRateLimiter(settings.ChainLookupsPerSecond, _timeProvider);
        _rpcLimiter = new TokenBucketRateLimiter(settings.ChainRpcsPerSecond, _timeProvider);
        _cacheCapacity = settings.ChainLookupCacheHeights;
        _mempoolSpentRecheckInterval = settings.MempoolSpentRecheckInterval;
        _txIdSource = txIdSource;

        _outpointWatcher = outpointWatcher;
        _outpointWatcher?.OnBlockDisconnected += HandleBlockDisconnected;

        // NL-414: the monitor's blocks end the kept mempool answers without a getblockcount per lookup
        _blockMonitor = outpointWatcher as IBlockchainMonitor;
        _blockMonitor?.OnNewBlockDetected += HandleNewBlock;
    }

    /// <summary>The short channel ids whose "only spent in the mempool" answer is kept (tests).</summary>
    internal int MempoolSpentCount => _mempoolSpent.Count;

    /// <inheritdoc />
    public bool IsPending(ShortChannelId shortChannelId) =>
        _inFlight.ContainsKey(shortChannelId) || TryGetMempoolSpent(shortChannelId, out _);

    /// <summary>
    /// Forgets the kept mempool answers read at a tip below <paramref name="height"/>: a block came since (NL-414).
    /// The monitor's <c>OnNewBlockDetected</c> calls it; tests call it directly.
    /// </summary>
    internal void OnNewBlock(uint height)
    {
        ObserveTip(height);
        foreach (var (shortChannelId, answer) in _mempoolSpent)
        {
            if (answer.Tip < height)
                _mempoolSpent.TryRemove(new KeyValuePair<ShortChannelId, MempoolSpentAnswer>(shortChannelId, answer));
        }
    }

    /// <summary>The heights whose txid list is cached, most recently used first (tests).</summary>
    internal IReadOnlyList<uint> CachedHeights
    {
        get
        {
            lock (_cacheGate)
                return _lru.Select(b => b.Height).ToList();
        }
    }

    /// <inheritdoc />
    public async Task<FundingOutputLookupResult> LookupAsync(ShortChannelId shortChannelId,
                                                             CancellationToken cancellationToken = default)
    {
        _inFlight.AddOrUpdate(shortChannelId, 1, (_, count) => count + 1);
        try
        {
            if (await TryReuseMempoolSpentAsync(shortChannelId) is { } kept)
                return kept;

            return await LookupFromChainAsync(shortChannelId, cancellationToken);
        }
        catch (EsploraUnavailableException ex)
        {
            // The index could not give a proven txid: transient, never held against the peer
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Funding txid of {ShortChannelId} unavailable from the index: {Reason}",
                                 shortChannelId, ex.Message);
            return FundingOutputLookupResult.Failed(FundingOutputStatus.ChainUnavailable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(ex, "Funding output lookup of {ShortChannelId} failed", shortChannelId);
            return FundingOutputLookupResult.Failed(FundingOutputStatus.ChainUnavailable);
        }
        finally
        {
            ReleaseInFlight(shortChannelId);
        }
    }

    /// <inheritdoc />
    public async Task<FundingOutputLookupResult> VerifyAsync(ShortChannelId shortChannelId,
                                                             CompactPubKey bitcoinKey1, CompactPubKey bitcoinKey2,
                                                             LightningMoney? expectedAmount = null,
                                                             CancellationToken cancellationToken = default)
    {
        var result = await LookupAsync(shortChannelId, cancellationToken);
        if (!result.IsFound)
            return result;

        var status = FundingOutputStatus.Found;
        var expectedScript = TryCreateFundingScriptPubKey(bitcoinKey1, bitcoinKey2);
        if (expectedScript is null || !expectedScript.AsSpan().SequenceEqual(result.ScriptPubKey))
            status = FundingOutputStatus.ScriptMismatch;
        else if (result.Amount!.IsZero
              || (expectedAmount is not null && result.Amount.MilliSatoshi != expectedAmount.MilliSatoshi))
            status = FundingOutputStatus.AmountMismatch;

        return status == FundingOutputStatus.Found
                   ? result
                   : FundingOutputLookupResult.WithOutput(status, result.TransactionId!.Value, result.Amount!,
                                                          result.ScriptPubKey!, result.Confirmations);
    }

    /// <inheritdoc />
    public void InvalidateFrom(uint height)
    {
        lock (_cacheGate)
        {
            var node = _lru.First;
            while (node is not null)
            {
                var next = node.Next;
                if (node.Value.Height >= height)
                {
                    _cache.Remove(node.Value.Height);
                    _lru.Remove(node);
                }

                node = next;
            }
        }
    }

    public void Dispose()
    {
        _outpointWatcher?.OnBlockDisconnected -= HandleBlockDisconnected;
        _blockMonitor?.OnNewBlockDetected -= HandleNewBlock;
        _concurrency.Dispose();
    }

    /// <summary>
    /// The P2WSH scriptPubKey of <c>2 &lt;key1&gt; &lt;key2&gt; 2 OP_CHECKMULTISIG</c> with the keys in BOLT 3 order
    /// (lexicographic compressed bytes); null when a key is not a valid point.
    /// </summary>
    internal static byte[]? TryCreateFundingScriptPubKey(CompactPubKey key1, CompactPubKey key2)
    {
        var key1Bytes = (byte[]?)key1;
        var key2Bytes = (byte[]?)key2;
        if (key1Bytes is null || key2Bytes is null
         || !PubKey.TryCreatePubKey(key1Bytes, out var pubKey1) || pubKey1 is null
         || !PubKey.TryCreatePubKey(key2Bytes, out var pubKey2) || pubKey2 is null)
            return null;

        var orderedKeys = new[] { pubKey1, pubKey2 }.OrderBy(k => k, PubKeyComparer.Instance).ToArray();
        return PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, orderedKeys).WitHash.ScriptPubKey.ToBytes();
    }

    private async Task<FundingOutputLookupResult> LookupFromChainAsync(ShortChannelId shortChannelId,
                                                                       CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            await _rateLimiter.WaitAsync(cancellationToken);

            var (result, tip) = await LookupOnceAsync(shortChannelId, cancellationToken);
            if (result.Status == FundingOutputStatus.ChainMoved)
            {
                // The output was reported at another height: drop the cached list and ask once more
                InvalidateFrom(shortChannelId.BlockHeight);
                (result, tip) = await LookupOnceAsync(shortChannelId, cancellationToken);
            }

            if (result.Status == FundingOutputStatus.OutputSpentInMempool)
                RememberMempoolSpent(shortChannelId, tip);

            return result;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// Runs one bitcoind RPC through the per-RPC budget (NL-346): the per-lookup rate bounds how many lookups start,
    /// this one how many RPCs they cost together (3-5 each).
    /// </summary>
    private async Task<T> RpcAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        await _rpcLimiter.WaitAsync(cancellationToken);
        return await call();
    }

    /// <summary>
    /// NL-414: the kept "only spent in the mempool" answer while no block came since it was read (see the remarks of
    /// this class); null when there is none or it no longer holds (then it is forgotten).
    /// </summary>
    private async Task<FundingOutputLookupResult?> TryReuseMempoolSpentAsync(ShortChannelId shortChannelId)
    {
        if (!TryGetMempoolSpent(shortChannelId, out var answer))
            return null;

        if (_blockMonitor is null)
        {
            // No block events: one getblockcount tells whether a block came
            var tip = await RpcAsync(() => _chain.GetCurrentBlockHeightAsync(), CancellationToken.None);
            ObserveTip(tip);
            if (tip != answer.Tip)
            {
                _mempoolSpent.TryRemove(new KeyValuePair<ShortChannelId, MempoolSpentAnswer>(shortChannelId, answer));
                return null;
            }
        }

        if (_logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("Funding output of {ShortChannelId} still spent in the mempool at tip {Tip}; not asking "
                           + "bitcoind again before the next block", shortChannelId, answer.Tip);
        return FundingOutputLookupResult.KeptMempoolSpent();
    }

    private bool TryGetMempoolSpent(ShortChannelId shortChannelId, out MempoolSpentAnswer answer)
    {
        if (!_mempoolSpent.TryGetValue(shortChannelId, out answer))
            return false;

        // A block the monitor reported, or a later tip another lookup read, ends it; so does the interval
        if (_timeProvider.GetElapsedTime(answer.ReadAt) < _mempoolSpentRecheckInterval
         && answer.Tip >= Interlocked.Read(ref _lastKnownTip))
            return true;

        _mempoolSpent.TryRemove(new KeyValuePair<ShortChannelId, MempoolSpentAnswer>(shortChannelId, answer));
        return false;
    }

    private void RememberMempoolSpent(ShortChannelId shortChannelId, uint tip)
    {
        if (_mempoolSpentRecheckInterval <= TimeSpan.Zero)
            return;

        // Bounded by what is spent in the mempool now; the stale ones are dropped on the way
        if (_mempoolSpent.Count >= MaxMempoolSpentAnswers)
            foreach (var key in _mempoolSpent.Keys)
                TryGetMempoolSpent(key, out _);
        if (_mempoolSpent.Count >= MaxMempoolSpentAnswers)
            return;

        _mempoolSpent[shortChannelId] = new MempoolSpentAnswer(tip, _timeProvider.GetTimestamp());
    }

    private void ReleaseInFlight(ShortChannelId shortChannelId)
    {
        while (_inFlight.TryGetValue(shortChannelId, out var count))
        {
            if (count <= 1
                    ? _inFlight.TryRemove(new KeyValuePair<ShortChannelId, int>(shortChannelId, count))
                    : _inFlight.TryUpdate(shortChannelId, count - 1, count))
                return;
        }
    }

    private void ObserveTip(uint tip)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _lastKnownTip);
            if (tip <= current)
                return;
        } while (Interlocked.CompareExchange(ref _lastKnownTip, tip, current) != current);
    }

    private async Task<(FundingOutputLookupResult Result, uint Tip)> LookupOnceAsync(
        ShortChannelId shortChannelId, CancellationToken cancellationToken)
    {
        var height = shortChannelId.BlockHeight;
        var tip = await RpcAsync(() => _chain.GetCurrentBlockHeightAsync(), cancellationToken);
        ObserveTip(tip);
        if (height > tip)
            return (FundingOutputLookupResult.Failed(FundingOutputStatus.BlockNotFound), tip);

        var position = await GetFundingTxIdAsync(shortChannelId, cancellationToken);
        switch (position.Status)
        {
            case FundingTxIdStatus.BlockUnavailable:
                return (FundingOutputLookupResult.Failed(FundingOutputStatus.BlockUnavailable), tip);
            case FundingTxIdStatus.IndexOutOfRange:
                return (FundingOutputLookupResult.Failed(FundingOutputStatus.TransactionIndexOutOfRange), tip);
        }

        var txId = position.TxId!;
        var outPoint = new OutPoint(txId, shortChannelId.OutputIndex);
        var unspent = await RpcAsync(() => _chain.GetUnspentOutputAsync(outPoint), cancellationToken);
        if (unspent is not { } found)
        {
            // Spent in a block, missing, or only spent by a mempool transaction (a close not mined yet: transient)
            var confirmed = await RpcAsync(() => _chain.GetConfirmedUnspentOutputAsync(outPoint), cancellationToken);
            return (FundingOutputLookupResult.Failed(confirmed is { } c && c.Height == height
                                                        ? FundingOutputStatus.OutputSpentInMempool
                                                        : FundingOutputStatus.OutputSpentOrMissing), tip);
        }

        if (found.Height != height)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Funding output of {ShortChannelId} reported at height {Height}, not {ScidHeight}",
                                 shortChannelId, found.Height, height);
            return (FundingOutputLookupResult.Failed(FundingOutputStatus.ChainMoved), tip);
        }

        // A reorg between reading the txid list and gettxout can mine the same tx at the same height at another index:
        // the list must still be the active chain's block at that height
        var blockHashNow = await RpcAsync(() => _chain.GetBlockHashAsync(height), cancellationToken);
        if (blockHashNow != position.BlockHash)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Block {Height} changed during the funding output lookup of {ShortChannelId}", height,
                                 shortChannelId);
            return (FundingOutputLookupResult.Failed(FundingOutputStatus.ChainMoved), tip);
        }

        var confirmations = tip >= height ? tip - height + 1 : 1;
        return (FundingOutputLookupResult.WithOutput(FundingOutputStatus.Found, new TxId(txId.ToBytes()),
                                                    LightningMoney.Satoshis(found.Output.Value.Satoshi),
                                                    found.Output.ScriptPubKey.ToBytes(), confirmations), tip);
    }

    // Funding txid source seam (D12, pruned nodes): the injected IFundingTxIdSource, else bitcoind's txid list
    private async Task<FundingTxIdAtPosition> GetFundingTxIdAsync(ShortChannelId shortChannelId,
                                                                  CancellationToken cancellationToken)
    {
        if (_txIdSource is not null)
            return await _txIdSource.GetTxIdAsync(shortChannelId.BlockHeight, shortChannelId.TransactionIndex,
                                                  cancellationToken);

        var block = await GetTxIdsAsync(shortChannelId.BlockHeight, cancellationToken);
        if (block is not { } list)
        {
            LogPrunedHintOnce(shortChannelId.BlockHeight);
            return FundingTxIdAtPosition.BlockUnavailable;
        }

        return shortChannelId.TransactionIndex >= list.TxIds.Count
                   ? FundingTxIdAtPosition.IndexOutOfRange
                   : FundingTxIdAtPosition.Found(list.BlockHash, list.TxIds[(int)shortChannelId.TransactionIndex]);
    }

    private void LogPrunedHintOnce(uint height)
    {
        if (Interlocked.Exchange(ref _prunedHintLogged, 1) != 0 || !_logger.IsEnabled(LogLevel.Warning))
            return;

        _logger.LogWarning(
            "bitcoind cannot serve block {Height} (pruned?), so gossip channel announcements from pruned heights cannot "
          + "be verified. Set Gossip:FundingTxIdSource=Esplora with Gossip:EsploraUrl (e.g. https://mempool.space/api "
          + "or a self-hosted esplora/electrs) to take the funding txid from an index, proven against our node's "
          + "headers, or Gossip:FundingValidation=SkipUnavailable to keep such channels unverified", height);
    }

    private async Task<(uint256 BlockHash, IReadOnlyList<uint256> TxIds)?> GetTxIdsAsync(
        uint height, CancellationToken cancellationToken)
    {
        bool cached;
        lock (_cacheGate)
            cached = _cache.ContainsKey(height);

        // A cached list is used only while getblockhash still names its block; a height not cached needs no separate
        // getblockhash, since GetBlockTxIdsAsync reads the hash it lists (one RPC less per miss, NL-411)
        if (cached)
        {
            var blockHash = await RpcAsync(() => _chain.GetBlockHashAsync(height), cancellationToken);
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(height, out var node))
                {
                    if (node.Value.BlockHash == blockHash)
                    {
                        _lru.Remove(node);
                        _lru.AddFirst(node);
                        return (node.Value.BlockHash, node.Value.TxIds);
                    }

                    // Another block at this height now: a reorg the monitor has not reported yet
                    _cache.Remove(height);
                    _lru.Remove(node);
                }
            }
        }

        var block = await RpcAsync(() => _chain.GetBlockTxIdsAsync(height), cancellationToken);
        if (block is not { } fetched)
            return null;

        Store(new CachedBlock(height, fetched.BlockHash, fetched.TxIds));
        return fetched;
    }

    private void Store(CachedBlock block)
    {
        lock (_cacheGate)
        {
            if (_cache.Remove(block.Height, out var existing))
                _lru.Remove(existing);

            _cache[block.Height] = _lru.AddFirst(block);
            while (_cache.Count > _cacheCapacity)
            {
                var oldest = _lru.Last!;
                _lru.RemoveLast();
                _cache.Remove(oldest.Value.Height);
            }
        }
    }

    private void HandleBlockDisconnected(object? sender, BlockDisconnectedEventArgs e) => InvalidateFrom(e.Height);

    private void HandleNewBlock(object? sender, NewBlockEventArgs e) => OnNewBlock(e.Height);

    /// <summary>At most this many kept mempool answers (a mempool full of closes is not worth more memory).</summary>
    private const int MaxMempoolSpentAnswers = 10_000;

    /// <summary>An "only spent in the mempool" answer read at tip <c>Tip</c>, at timestamp <c>ReadAt</c>.</summary>
    private readonly record struct MempoolSpentAnswer(uint Tip, long ReadAt);

    private sealed record CachedBlock(uint Height, uint256 BlockHash, IReadOnlyList<uint256> TxIds);
}
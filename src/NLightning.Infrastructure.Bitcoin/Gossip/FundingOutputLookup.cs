using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Gossip;

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
/// Bounded by <see cref="FundingOutputLookupOptions.ChainLookupConcurrency"/> lookups at once and
/// <see cref="FundingOutputLookupOptions.ChainLookupsPerSecond"/> started per second. A cached txid list is used only
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
/// <c>gettxout</c> without the mempool), which is transient like <see cref="FundingOutputStatus.BlockNotFound"/>.
/// </para>
/// </remarks>
public sealed class FundingOutputLookup : IFundingOutputLookup, IDisposable
{
    private readonly IBitcoinChainService _chain;
    private readonly ILogger<FundingOutputLookup> _logger;
    private readonly IOutpointWatcher? _outpointWatcher;
    private readonly SemaphoreSlim _concurrency;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly int _cacheCapacity;

    private readonly Lock _cacheGate = new();
    private readonly Dictionary<uint, LinkedListNode<CachedBlock>> _cache = new();
    private readonly LinkedList<CachedBlock> _lru = new();

    public FundingOutputLookup(IBitcoinChainService chain, ILogger<FundingOutputLookup> logger,
                               IOptions<FundingOutputLookupOptions>? options = null,
                               TimeProvider? timeProvider = null, IOutpointWatcher? outpointWatcher = null)
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
        _rateLimiter = new TokenBucketRateLimiter(settings.ChainLookupsPerSecond, timeProvider ?? TimeProvider.System);
        _cacheCapacity = settings.ChainLookupCacheHeights;

        _outpointWatcher = outpointWatcher;
        if (_outpointWatcher is not null)
            _outpointWatcher.OnBlockDisconnected += HandleBlockDisconnected;
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
        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            await _rateLimiter.WaitAsync(cancellationToken);

            var result = await LookupOnceAsync(shortChannelId);
            if (result.Status != FundingOutputStatus.ChainMoved)
                return result;

            // The output was reported at another height: drop the cached list and ask once more
            InvalidateFrom(shortChannelId.BlockHeight);
            return await LookupOnceAsync(shortChannelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(ex, "Funding output lookup of {ShortChannelId} failed", shortChannelId);
            return FundingOutputLookupResult.Failed(FundingOutputStatus.ChainUnavailable);
        }
        finally
        {
            _concurrency.Release();
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
        if (_outpointWatcher is not null)
            _outpointWatcher.OnBlockDisconnected -= HandleBlockDisconnected;
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

    private async Task<FundingOutputLookupResult> LookupOnceAsync(ShortChannelId shortChannelId)
    {
        var height = shortChannelId.BlockHeight;
        var tip = await _chain.GetCurrentBlockHeightAsync();
        if (height > tip)
            return FundingOutputLookupResult.Failed(FundingOutputStatus.BlockNotFound);

        var block = await GetTxIdsAsync(height);
        if (block is not { } list)
            return FundingOutputLookupResult.Failed(FundingOutputStatus.BlockUnavailable);

        var txIds = list.TxIds;

        if (shortChannelId.TransactionIndex >= txIds.Count)
            return FundingOutputLookupResult.Failed(FundingOutputStatus.TransactionIndexOutOfRange);

        var txId = txIds[(int)shortChannelId.TransactionIndex];
        var outPoint = new OutPoint(txId, shortChannelId.OutputIndex);
        var unspent = await _chain.GetUnspentOutputAsync(outPoint);
        if (unspent is not { } found)
        {
            // Spent in a block, missing, or only spent by a mempool transaction (a close not mined yet: transient)
            var confirmed = await _chain.GetConfirmedUnspentOutputAsync(outPoint);
            return FundingOutputLookupResult.Failed(confirmed is { } c && c.Height == height
                                                        ? FundingOutputStatus.OutputSpentInMempool
                                                        : FundingOutputStatus.OutputSpentOrMissing);
        }

        if (found.Height != height)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Funding output of {ShortChannelId} reported at height {Height}, not {ScidHeight}",
                                 shortChannelId, found.Height, height);
            return FundingOutputLookupResult.Failed(FundingOutputStatus.ChainMoved);
        }

        // A reorg between reading the txid list and gettxout can mine the same tx at the same height at another index:
        // the list must still be the active chain's block at that height
        var blockHashNow = await _chain.GetBlockHashAsync(height);
        if (blockHashNow != list.BlockHash)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Block {Height} changed during the funding output lookup of {ShortChannelId}", height,
                                 shortChannelId);
            return FundingOutputLookupResult.Failed(FundingOutputStatus.ChainMoved);
        }

        var confirmations = tip >= height ? tip - height + 1 : 1;
        return FundingOutputLookupResult.WithOutput(FundingOutputStatus.Found, new TxId(txId.ToBytes()),
                                                    LightningMoney.Satoshis(found.Output.Value.Satoshi),
                                                    found.Output.ScriptPubKey.ToBytes(), confirmations);
    }

    private async Task<(uint256 BlockHash, IReadOnlyList<uint256> TxIds)?> GetTxIdsAsync(uint height)
    {
        var blockHash = await _chain.GetBlockHashAsync(height);
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

        var block = await _chain.GetBlockTxIdsAsync(height);
        if (block is not { } fetched)
            return null;

        // Cache only the list of the block getblockhash named; a list of a newer block is still the chain's answer
        if (fetched.BlockHash == blockHash)
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

    private sealed record CachedBlock(uint Height, uint256 BlockHash, IReadOnlyList<uint256> TxIds);
}
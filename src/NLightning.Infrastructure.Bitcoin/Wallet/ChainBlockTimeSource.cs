using System.Collections.Concurrent;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Interfaces;
using Interfaces;

/// <summary>
/// <see cref="IBlockTimeSource"/> over bitcoind (<see cref="IBitcoinChainService.GetBlockTimeAsync"/>), for the
/// accounting channel report's open time of a channel funded before the feed began (NL-623). The answers are kept (at
/// most <see cref="MaxCachedBlocks"/>): the report asks for the same funding blocks every time, and those are deep.
/// </summary>
public sealed class ChainBlockTimeSource : IBlockTimeSource
{
    /// <summary>The most block times kept; the cache starts over when it is full.</summary>
    internal const int MaxCachedBlocks = 4_096;

    private readonly IBitcoinChainService _chain;
    private readonly ConcurrentDictionary<uint, DateTimeOffset> _cache = new();

    public ChainBlockTimeSource(IBitcoinChainService chain)
    {
        _chain = chain;
    }

    /// <inheritdoc/>
    public async Task<DateTimeOffset?> GetBlockTimeAsync(uint height, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(height, out var cached))
            return cached;

        cancellationToken.ThrowIfCancellationRequested();
        if (await _chain.GetBlockTimeAsync(height) is not { } time)
            return null;

        if (_cache.Count >= MaxCachedBlocks)
            _cache.Clear();
        _cache[height] = time;
        return time;
    }
}
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Splicing;

using Domain.Bitcoin.Events;
using Domain.Channels.Enums;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <inheritdoc cref="IRetiredScidMap"/>
/// <remarks>
/// <para>In memory only (splicing plan D12, SP2-B-T2): <see cref="SpliceService"/> retires the replaced funding's short
/// channel id after the lock's save, and <see cref="LoadAsync"/> rebuilds the map at startup from the
/// <c>ChannelFundings</c> rows: every <see cref="ChannelFundingStatus.Replaced"/> funding with a short channel id is
/// retired at the confirmation height of the funding that replaced it (the next locked one in creation order).</para>
/// <para>With an <see cref="IBlockchainMonitor"/> the map prunes itself on every new block, and
/// <see cref="TryResolve"/> also refuses an entry that expired at the monitor's last processed height, so a forward to
/// a retired short channel id fails <c>unknown_next_peer</c> from its expiry height on even before the prune ran.</para>
/// </remarks>
public sealed class RetiredScidMap : IRetiredScidMap, IDisposable
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly Dictionary<ShortChannelId, RetiredShortChannelId> _entries = [];
    private readonly ILogger<RetiredScidMap> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly Lock _sync = new();

    public RetiredScidMap(IServiceProvider serviceProvider, ILogger<RetiredScidMap> logger,
                          IBlockchainMonitor? blockchainMonitor = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _blockchainMonitor = blockchainMonitor;
        _blockchainMonitor?.OnNewBlockDetected += OnNewBlockDetected;
    }

    /// <inheritdoc />
    public void Retire(RetiredShortChannelId retired)
    {
        ArgumentNullException.ThrowIfNull(retired);
        lock (_sync)
        {
            if (_entries.TryGetValue(retired.ShortChannelId, out var known)
             && known.ExpiresAtHeight >= retired.ExpiresAtHeight)
                return;

            _entries[retired.ShortChannelId] = retired;
        }

        _logger.LogInformation("Short channel id {ShortChannelId} of channel {ChannelId} retired at {Height}; it keeps "
                             + "resolving until block {ExpiresAt}", retired.ShortChannelId, retired.ChannelId,
                               retired.RetiredAtHeight, retired.ExpiresAtHeight);
    }

    /// <inheritdoc />
    public bool TryResolve(ShortChannelId shortChannelId, out ChannelId channelId)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(shortChannelId, out var entry) && !IsExpired(entry, CurrentHeight))
            {
                channelId = entry.ChannelId;
                return true;
            }
        }

        channelId = default;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<RetiredShortChannelId> GetByChannel(ChannelId channelId)
    {
        lock (_sync)
            return _entries.Values.Where(e => e.ChannelId == channelId)
                           .OrderBy(e => e.RetiredAtHeight)
                           .ThenBy(e => e.ShortChannelId.BlockHeight)
                           .ToList();
    }

    /// <inheritdoc />
    public int PruneExpired(uint height)
    {
        List<RetiredShortChannelId> expired;
        lock (_sync)
        {
            expired = _entries.Values.Where(e => e.ExpiresAtHeight <= height).ToList();
            foreach (var entry in expired)
                _entries.Remove(entry.ShortChannelId);
        }

        foreach (var entry in expired)
            _logger.LogInformation("Retired short channel id {ShortChannelId} of channel {ChannelId} expired at {Height}",
                                   entry.ShortChannelId, entry.ChannelId, height);
        return expired.Count;
    }

    private async Task<uint> GetStoredHeightAsync(IUnitOfWork unitOfWork)
    {
        try
        {
            return unitOfWork.BlockchainStateDbRepository is { } states
                && await states.GetStateAsync() is { } state
                       ? state.LastProcessedHeight
                       : 0;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            _logger.LogDebug(e, "Could not read the last processed block height; loading at height 0");
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task LoadAsync(uint currentHeight, CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // The host loads the map before the chain monitor starts, whose height is 0 until then: use the height the
        // monitor stored, so expired entries are dropped now and the log names the real height
        if (currentHeight == 0)
            currentHeight = await GetStoredHeightAsync(unitOfWork);

        var channels = await unitOfWork.ChannelDbRepository.GetReadyChannelsAsync();

        var rebuilt = new List<RetiredShortChannelId>();
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // An alias-only channel (option_scid_alias Compulsory) never resolves by a real short channel id (NL-348)
            if (channel.State is ChannelState.Closed or ChannelState.Stale
             || channel.ChannelParams.UseScidAlias == FeatureSupport.Compulsory)
                continue;

            var fundings = await unitOfWork.ChannelFundingDbRepository.GetByChannelIdAsync(channel.ChannelId);
            rebuilt.AddRange(FromFundings(channel.ChannelId, fundings)
                                .Where(e => !IsExpired(e, currentHeight)));
        }

        lock (_sync)
        {
            _entries.Clear();
            foreach (var entry in rebuilt)
                if (!_entries.TryGetValue(entry.ShortChannelId, out var known)
                 || known.ExpiresAtHeight < entry.ExpiresAtHeight)
                    _entries[entry.ShortChannelId] = entry;
        }

        _logger.LogInformation("Loaded {Count} retired short channel id(s) at height {Height}", rebuilt.Count,
                               currentHeight);
    }

    /// <summary>
    /// The retired short channel ids a channel's fundings (in creation order) describe: each
    /// <see cref="ChannelFundingStatus.Replaced"/> funding with a short channel id, retired at the confirmation height of
    /// the next locked funding (<see cref="ChannelFundingStatus.Replaced"/> or <see cref="ChannelFundingStatus.Current"/>).
    /// A funding replaced by no locked funding with a known height is skipped.
    /// </summary>
    public static IReadOnlyList<RetiredShortChannelId> FromFundings(ChannelId channelId,
                                                                    IReadOnlyList<ChannelFunding> fundings)
    {
        ArgumentNullException.ThrowIfNull(fundings);
        var locked = fundings.Where(f => f.Status is ChannelFundingStatus.Replaced or ChannelFundingStatus.Current)
                             .ToList();
        var result = new List<RetiredShortChannelId>();
        for (var i = 0; i < locked.Count - 1; i++)
        {
            if (locked[i] is not { Status: ChannelFundingStatus.Replaced, ShortChannelId: { } retired })
                continue;

            var successor = locked[i + 1];
            if ((successor.ConfirmedHeight ?? successor.ShortChannelId?.BlockHeight) is not { } height)
                continue;

            result.Add(Create(retired, channelId, height));
        }

        return result;
    }

    /// <summary>A retired short channel id resolving for <see cref="RetiredShortChannelId.RetentionBlocks"/> blocks
    /// from <paramref name="retiredAtHeight"/>.</summary>
    public static RetiredShortChannelId Create(ShortChannelId shortChannelId, ChannelId channelId,
                                               uint retiredAtHeight) =>
        new(shortChannelId, channelId, retiredAtHeight, retiredAtHeight + RetiredShortChannelId.RetentionBlocks);

    /// <inheritdoc />
    public void Dispose()
    {
        _blockchainMonitor?.OnNewBlockDetected -= OnNewBlockDetected;
    }

    private uint? CurrentHeight => _blockchainMonitor?.LastProcessedBlockHeight;

    private static bool IsExpired(RetiredShortChannelId entry, uint? height) =>
        height is { } h && h >= entry.ExpiresAtHeight;

    private void OnNewBlockDetected(object? sender, NewBlockEventArgs args)
    {
        try
        {
            PruneExpired(args.Height);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not prune the retired short channel ids at {Height}", args.Height);
        }
    }
}
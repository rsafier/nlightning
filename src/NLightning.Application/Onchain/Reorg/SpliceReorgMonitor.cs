using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Reorg;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// A locked splice reorged out (splicing plan §3.6 "Reorgs", SP2-C-T4): after the lock the channel runs on the splice
/// funding, and the funding it replaced stays watched. When that funding's spend (the splice) is no longer recorded in
/// the active chain (the chain monitor clears a watched spend whose block was disconnected), the old funding output is
/// unspent again: the channel keeps operating on the locked funding (the splice transaction is still valid and is
/// rebroadcast by the chain monitor until it confirms), and the operator is told CRITICAL, once until the splice is seen
/// again. A different transaction spending the old funding output reaches the on-chain watcher like any funding spend
/// and is resolved with that funding's revocation data (SP-I5).
/// </summary>
/// <remarks>Run by the resolution executor's block round. Replaced fundings are read once per channel and current
/// funding (a lock changes the current funding, so it reloads them); each round then reads one watch per replaced
/// funding.</remarks>
internal sealed class SpliceReorgMonitor
{
    private readonly ConcurrentDictionary<ChannelId, (TxId Current, IReadOnlyList<ChannelFunding> Replaced)> _replaced =
        new();

    private readonly ConcurrentDictionary<(ChannelId, TxId), uint> _unspentSince = new();
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public SpliceReorgMonitor(IChannelMemoryRepository channelMemoryRepository, ILogger logger,
                              IServiceScopeFactory serviceScopeFactory)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <summary>True when the splice that replaced <paramref name="replacedFundingTxId"/> is not in the active chain
    /// (tests, diagnostics).</summary>
    public bool IsSpliceReorgedOut(ChannelId channelId, TxId replacedFundingTxId) =>
        _unspentSince.ContainsKey((channelId, replacedFundingTxId));

    /// <summary>Checks every live channel with a replaced funding at <paramref name="height"/>.</summary>
    public async Task CheckAsync(uint height, CancellationToken cancellationToken)
    {
        var channels = _channelMemoryRepository.FindChannels(
            c => c.State is ChannelState.Open or ChannelState.ShuttingDown or ChannelState.Negotiating
                     or ChannelState.Closing or ChannelState.Failed
              && c.FundingOutput?.TransactionId is not null);
        if (channels is not { Count: > 0 })
            return;

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = channel.FundingOutput!.TransactionId!.Value;
            if (!_replaced.TryGetValue(channel.ChannelId, out var known) || known.Current != current)
            {
                var fundings = await OnchainFundings.GetAllAsync(unitOfWork, channel, _logger);
                known = (current, fundings.Where(f => f.Status == ChannelFundingStatus.Replaced).ToList());
                _replaced[channel.ChannelId] = known;
            }

            foreach (var replaced in known.Replaced)
            {
                var watch = await unitOfWork.WatchedOutpointDbRepository.GetAsync(replaced.FundingTxId,
                                                                                  replaced.OutputIndex);
                if (watch is null)
                    continue;

                var key = (channel.ChannelId, replaced.FundingTxId);
                if (watch.IsSpent)
                {
                    if (_unspentSince.TryRemove(key, out _))
                        _logger.LogWarning("Channel {ChannelId}: funding {FundingTxId} is spent again by {TxId} at "
                                         + "height {Height}", channel.ChannelId, Display(replaced.FundingTxId),
                                           watch.SpentByTransactionId is { } by ? Display(by) : "?",
                                           watch.SpentAtHeight);
                    continue;
                }

                if (_unspentSince.TryAdd(key, height))
                    _logger.LogCritical("[SP2-C-T4] Channel {ChannelId}: the locked splice that spent funding "
                                      + "{FundingTxId} is no longer in the active chain (reorg at or below height "
                                      + "{Height}); the channel keeps operating on funding {Current}, which needs the "
                                      + "splice to confirm again (it is rebroadcast). A commitment on the old funding "
                                      + "is resolved with its own revocation data.", channel.ChannelId,
                                        Display(replaced.FundingTxId), height, Display(current));
            }
        }
    }

    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();
}
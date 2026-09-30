using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Reorg;

using Channels.Safety.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// NL-329 (BOLT 5 plan §3.8, O6-T3): a reorg took a funding transaction's confirmation out of the chain and the
/// transaction never confirmed again (evicted, or an input was double-spent on the new branch). The chain monitor
/// rewound its watch and rebroadcasts it every block, but a transaction that cannot confirm stays pending for good, so
/// the channel keeps running Open on a funding output that is not in the active chain: it keeps its pre-reorg short
/// channel id (the move of <see cref="FundingReconfirmationHandler"/> needs a reconfirmation), routes under it and
/// puts it into new invoices' route hints. After <c>Node:Onchain:FundingReconfirmGraceBlocks</c> (default 12, above
/// the funding depth) blocks without the funding being seen again, the channel is failed through
/// <see cref="IChannelFailureService"/> — without broadcasting our commitment, which spends the funding output and
/// therefore cannot confirm while the funding is not in the chain. The persisted funding watch and the error stay:
/// a funding that reconfirms late, or the peer's commitment on it, still reaches the on-chain watcher, and the
/// stored error is sent again on reconnection.
/// </summary>
/// <remarks>
/// Run by the resolution executor's block round, outside every channel lock (the failure service takes it). Memory
/// only: the count of blocks a channel's funding watch has been pending again starts over at a restart. A watch that
/// is seen again in a block clears it — the depth then completes and <see cref="FundingReconfirmationHandler"/> moves
/// the short channel id. Channels without a short channel id (the funding never confirmed) are not watched: their
/// confirmation is the channel manager's. Invoices issued before a reorg keep the old short channel id in their route
/// hints until they expire; the channel's failure keeps new ones from hinting at it.
/// </remarks>
internal sealed class FundingReconfirmGraceMonitor
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ConcurrentDictionary<ChannelId, uint> _pendingSince = new();
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly uint _graceBlocks;

    public FundingReconfirmGraceMonitor(IChannelMemoryRepository channelMemoryRepository, ILogger logger,
                                        IServiceScopeFactory serviceScopeFactory, uint graceBlocks)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
        _graceBlocks = graceBlocks;
    }

    /// <summary>True while the channel's funding confirmation is rolled back and counted (tests, diagnostics).</summary>
    public bool IsPendingReconfirm(ChannelId channelId) => _pendingSince.ContainsKey(channelId);

    /// <summary>The reconfirm check of every block round: fail the channels whose funding never came back.</summary>
    public async Task CheckAsync(uint height, CancellationToken cancellationToken)
    {
        var channels = _channelMemoryRepository.FindChannels(
            c => c.State == ChannelState.Open && c.ShortChannelId.BlockHeight != 0
              && c.FundingOutput?.TransactionId is not null);
        if (channels is not { Count: > 0 })
            return;

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var watch = await unitOfWork.WatchedTransactionDbRepository
                                      .GetByTransactionIdAsync(channel.FundingOutput!.TransactionId!.Value);
            if (watch is null || watch.FirstSeenAtHeight is not null)
            {
                // No row we know of is not ours to judge (its funding confirmation is the channel manager's); seen
                // again, the depth completes and the confirmation handler moves the short channel id
                _pendingSince.TryRemove(channel.ChannelId, out _);
                continue;
            }

            if (_pendingSince.TryGetValue(channel.ChannelId, out var since))
            {
                // The chain can move backwards at a rewind: never count the wrapped difference as waited blocks
                var waited = height >= since ? height - since : 0;
                if (waited < _graceBlocks)
                    continue;
            }
            else
            {
                _pendingSince[channel.ChannelId] = height;
                _logger.LogWarning("Channel {ChannelId}: its funding transaction {TxId} lost its confirmation in a "
                                 + "reorg and is not in the active chain; if it does not confirm again within {Grace} "
                                 + "blocks the channel is failed (NL-329)", channel.ChannelId,
                                   Display(channel.FundingOutput.TransactionId.Value), _graceBlocks);
                continue;
            }

            await FailAsync(scope, channel, height);
            _pendingSince.TryRemove(channel.ChannelId, out _);
        }
    }

    private async Task FailAsync(IServiceScope scope, ChannelModel channel, uint height)
    {
        if (scope.ServiceProvider.GetService<IChannelFailureService>() is not { } failureService)
        {
            _logger.LogError("Channel {ChannelId}: its funding transaction left the active chain and the grace is "
                           + "over, but no channel failure service is registered; the channel keeps its stale short "
                           + "channel id", channel.ChannelId);
            return;
        }

        var reason = $"the funding transaction left the active chain in a reorg and did not confirm again within "
                   + $"{_graceBlocks} blocks (seen pending again at {height})";
        try
        {
            // Without a broadcast: our commitment spends the funding output, which is not in the chain, so it cannot
            // confirm until the funding does — publishing it would only be retried every block for nothing
            var outcome = await failureService.FailChannelAsync(channel.ChannelId,
                             new ChannelFailureRequest(reason, "funding transaction not confirmed", Broadcast: false));
            _logger.LogCritical("Channel {ChannelId}: its funding transaction {TxId} did not confirm again within "
                              + "{Grace} blocks of losing its confirmation (reorg); the channel is failed ({Status}) "
                              + "and keeps its pre-reorg short channel id {Scid} (NL-329)", channel.ChannelId,
                                Display(channel.FundingOutput!.TransactionId!.Value), _graceBlocks,
                                outcome.Status, channel.ShortChannelId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Failing channel {ChannelId} over its funding transaction that never confirmed again "
                              + "failed; the next block retries it", channel.ChannelId);
        }
    }

    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();
}
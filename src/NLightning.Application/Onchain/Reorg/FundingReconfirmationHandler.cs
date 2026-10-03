using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Reorg;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Persistence.Interfaces;
using Gossip.Announcements.Interfaces;
using Gossip.Interfaces;

/// <summary>
/// NL-292 (BOLT 5 plan O6-T3): a funding transaction whose confirming block was disconnected is watched again by the
/// chain monitor, which raises its confirmation again from its position on the new branch. The channel manager handles
/// only the first confirmation of a channel, so this moves a channel's short channel id (and funding height) to the new
/// position: under the channel's lock, staged on the database's copy and saved, then applied to the shared model. After
/// the lock is released a new <c>channel_update</c> (it carries the short channel id) is sent to the peer through
/// <see cref="IChannelUpdateService"/>, when one is registered.
/// </summary>
/// <remarks>
/// NL-350 (BOLT 7 plan G1-T4): the move also forgets the channel's <c>announcement_signatures</c> (the peer's half and
/// our sent time, in the same save), tells <see cref="IChannelAnnouncementService"/> (the announcement handed on and
/// the per-connection record are void) and registers the channel again with a signer that has no
/// <see cref="IChannelSigningInfoSource"/>. The channel is private again (<c>dont_forward</c>, so the new update is not
/// relayed) until both halves for the new short channel id are exchanged: ours goes out in the block round once the new
/// funding block has the announcement depth.
/// </remarks>
/// <remarks>
/// NL-362: our announcement and policies under the old short channel id are also dropped from our own graph (through
/// the <see cref="IOwnGossipSink"/>, when the graph is on): until then we would route and serve a channel whose
/// funding no longer sits at that position, and relay it to peers.
/// </remarks>
/// <remarks>
/// The first confirmation (no short channel id yet) is the channel manager's; a confirmation at the recorded position
/// changes nothing. A channel that is not loaded is left alone (its funding confirmation is replayed at startup). The
/// caches of <see cref="IChannelUpdateService"/> and the invoice route hints are keyed by channel id and read the short
/// channel id from the channel, so they follow the move; invoices issued before it keep the old one. A funding
/// transaction that never confirms again (double-spent after the reorg) leaves the channel with its old short channel
/// id (NL-292 partial).
/// </remarks>
public sealed class FundingReconfirmationHandler
{
    private readonly IChannelLockProvider _channelLockProvider;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public FundingReconfirmationHandler(IChannelLockProvider channelLockProvider,
                                        IChannelMemoryRepository channelMemoryRepository, ILogger logger,
                                        IServiceScopeFactory serviceScopeFactory)
    {
        _channelLockProvider = channelLockProvider;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <summary>
    /// Moves the short channel id of the channel whose funding transaction <paramref name="watch"/> confirmed at another
    /// position than recorded. Returns true when it changed it.
    /// </summary>
    public async Task<bool> HandleAsync(WatchedTransactionModel watch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watch);
        if (watch is not { FirstSeenAtHeight: { } height, TransactionIndex: { } index })
            return false;

        bool moved;
        try
        {
            moved = await MoveAsync(watch, height, index, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Moving the short channel id of channel {ChannelId} after a reorg failed",
                             watch.ChannelId);
            return false;
        }

        if (!moved)
            return false;

        // The peer keeps our policy under the old short channel id until it gets a new update (sent without a lock)
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            if (scope.ServiceProvider.GetService<IChannelUpdateService>() is { } channelUpdateService)
                await channelUpdateService.SendChannelUpdateAsync(watch.ChannelId, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Sending the channel_update of channel {ChannelId} after its short channel id moved "
                              + "failed", watch.ChannelId);
        }

        return true;
    }

    /// <summary>
    /// A signer without an <see cref="IChannelSigningInfoSource"/> only knows the short channel id it was registered
    /// with and refuses to sign the announcement of another one (NL-343), so it is registered again with the new one.
    /// A signer with a source reads the persisted one.
    /// </summary>
    private void RefreshSourcelessSigner(IServiceProvider serviceProvider, ChannelModel channel)
    {
        if (serviceProvider.GetService<IChannelSigningInfoSource>() is not null
         || serviceProvider.GetService<ILightningSigner>() is not { } signer)
            return;

        try
        {
            signer.RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Registering channel {ChannelId} with the signer after its short channel id moved "
                              + "failed", channel.ChannelId);
        }
    }

    /// <summary>
    /// The accounting events of the funding (NL-607): the <see cref="AccountingEventKind.ChannelFunded"/> and push of the
    /// open, or the <see cref="AccountingEventKind.SpliceLocked"/> of a spliced channel's current funding, and the
    /// liquidity fee of a purchase made in it (NL-771), recorded at the old block, are reversed and recorded again at
    /// <paramref name="height"/> with the new short channel id under their next confirmation key
    /// (<see cref="AccountingConfirmations.NextConfirmationKey"/>), in the move's save. A memo event of the backfill
    /// posts nothing and is left alone. Never throws.
    /// </summary>
    private async Task StageFundingEventsMovedAsync(IUnitOfWork unitOfWork, ChannelId channelId, TxId fundingTxId,
                                                    uint height, ShortChannelId moved,
                                                    IServiceProvider serviceProvider,
                                                    CancellationToken cancellationToken)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } accounting)
                return;

            var now = (serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
            string[] baseKeys =
            [
                AccountingEventKeys.ChannelFunded(channelId, fundingTxId), AccountingEventKeys.Push(channelId),
                AccountingEventKeys.SpliceLocked(channelId, fundingTxId),
                // NL-771: a liquidity purchase booked with the confirmation moves with it
                AccountingEventKeys.LiquidityFee(channelId, fundingTxId)
            ];
            foreach (var baseKey in baseKeys)
            {
                var existing = (await accounting.GetByKeyPrefixAsync(baseKey, cancellationToken)).ToList();
                var standing = AccountingConfirmations.FindStanding(baseKey, existing);
                // Same block and position: nothing moved. The same height at another index (a stale short channel
                // id) moves the events too (NL-738)
                if (standing is null || standing.BlockHeight is not { } recordedHeight
                 || (recordedHeight == height && Equals(standing.ShortChannelId, moved))
                 || standing.TxId != fundingTxId
                 || standing.Details.ContainsKey(AccountingDetailKeys.Memo))
                    continue;

                var reversal = AccountingConfirmations.CreateReversal(standing, now,
                                                                      Math.Min(recordedHeight, height) - 1);
                accounting.Add(reversal);
                existing.Add(reversal);
                if (AccountingConfirmations.NextConfirmationKey(baseKey, existing) is not { } key)
                    continue;

                accounting.Add(AccountingConfirmations.CreateReconfirmation(standing, key, height, moved, now));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "The accounting events of channel {ChannelId}'s funding could not be moved to block "
                              + "{Height}", channelId, height);
        }
    }

    private async Task<bool> MoveAsync(WatchedTransactionModel watch, uint height, uint index,
                                       CancellationToken cancellationToken)
    {
        using (await _channelLockProvider.AcquireAsync(watch.ChannelId, cancellationToken))
        {
            if (!_channelMemoryRepository.TryGetChannel(watch.ChannelId, out var channel)
             || channel.FundingOutput is not { TransactionId: { } fundingTxId, Index: { } outputIndex }
             || fundingTxId != watch.TransactionId
             || channel.ShortChannelId.BlockHeight == 0)
                return false;

            var moved = new ShortChannelId(height, index, outputIndex);
            var previous = channel.ShortChannelId;
            if (previous.Equals(moved))
                return false;

            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var stored = await unitOfWork.ChannelDbRepository.GetByIdAsync(watch.ChannelId);
            if (stored is null)
                return false;

            var wasAnnounced = channel.RemoteAnnouncementSignatures is not null
                            || channel.LocalAnnouncementSignaturesSentAt is not null;

            // NL-350: the announcement_signatures of the old short channel id sign a void announcement, so both
            // halves are forgotten in the same save (the channel is no longer public until they are exchanged again)
            stored.ShortChannelId = moved;
            stored.FundingCreatedAtBlockHeight = height;
            stored.ResetAnnouncementSignatures();
            await unitOfWork.ChannelDbRepository.UpdateAsync(stored);

            // NL-607: the funding's accounting events move to the new block in the same save
            await StageFundingEventsMovedAsync(unitOfWork, watch.ChannelId, fundingTxId, height, moved,
                                               scope.ServiceProvider, cancellationToken);
            await unitOfWork.SaveChangesAsync();

            channel.ShortChannelId = moved;
            channel.FundingCreatedAtBlockHeight = height;
            channel.ResetAnnouncementSignatures();

            // NL-138: the channel update service learns of the new short channel id (and switches its channel_update)
            // and the backup monitor refreshes its SCB entry only through OnChannelUpdated; the announcement service
            // and the graph sink below cover the announcement half alone
            _channelMemoryRepository.UpdateChannel(channel);

            _logger.LogWarning("The funding transaction {TxId} of channel {ChannelId} confirmed again at {Scid} "
                             + "after a reorg (was {Previous}); short channel id updated{Announcement}",
                               new uint256(fundingTxId), watch.ChannelId, moved, previous,
                               wasAnnounced ? ", its announcement_signatures are exchanged again" : "");

            // Ours is due again at the new depth, also on the current connection
            scope.ServiceProvider.GetService<IChannelAnnouncementService>()?.OnShortChannelIdChanged(channel.ChannelId);

            // NL-362: our announcement under the old short channel id is void — forget it in the graph
            scope.ServiceProvider.GetService<IOwnGossipSink>()?.ForgetOwnChannel(previous);
            RefreshSourcelessSigner(scope.ServiceProvider, channel);
            return true;
        }
    }
}
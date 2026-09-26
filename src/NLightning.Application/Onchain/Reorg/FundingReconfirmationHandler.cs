using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Onchain.Reorg;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
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
            await unitOfWork.SaveChangesAsync();

            channel.ShortChannelId = moved;
            channel.FundingCreatedAtBlockHeight = height;
            channel.ResetAnnouncementSignatures();
            _logger.LogWarning("The funding transaction {TxId} of channel {ChannelId} confirmed again at {Scid} "
                             + "after a reorg (was {Previous}); short channel id updated{Announcement}",
                               new uint256(fundingTxId), watch.ChannelId, moved, previous,
                               wasAnnounced ? ", its announcement_signatures are exchanged again" : "");

            // Ours is due again at the new depth, also on the current connection
            scope.ServiceProvider.GetService<IChannelAnnouncementService>()?.OnShortChannelIdChanged(channel.ChannelId);
            RefreshSourcelessSigner(scope.ServiceProvider, channel);
            return true;
        }
    }
}
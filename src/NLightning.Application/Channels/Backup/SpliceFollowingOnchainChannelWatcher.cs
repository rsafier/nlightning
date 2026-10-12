using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Channels.Backup;

using Domain.Bitcoin.Events;
using Interfaces;
using Onchain.Interfaces;

/// <summary>
/// The <see cref="IOnchainChannelWatcher"/> the channel manager hands funding spends to, over the node's own watcher
/// (NL-478 review): a spend of a recovery channel's funding output that is no commitment (a splice that confirmed after
/// the restore) goes to <see cref="IChannelRestoreService.TryHandleRecoveryFundingSpendAsync"/> first, which moves the
/// channel to the splice instead of letting the watcher record it as a close (<c>Unknown</c>, which would end the
/// recovery: the peer's commitment on the splice output would never be watched). Every other spend goes to
/// <see cref="Inner"/> unchanged.
/// </summary>
/// <remarks>
/// Registered by <see cref="ChannelBackupServiceCollectionExtensions.AddChannelBackupServices"/> in place of the
/// registered watcher. The restore service itself uses <see cref="Inner"/>, so its own hand-overs never come back here.
/// Both collaborators are resolved lazily: the restore service depends on the watcher.
/// </remarks>
public sealed class SpliceFollowingOnchainChannelWatcher : IOnchainChannelWatcher
{
    private readonly Lazy<IOnchainChannelWatcher?> _inner;
    private readonly Lazy<IChannelRestoreService?> _restoreService;
    private readonly ILogger<SpliceFollowingOnchainChannelWatcher> _logger;

    public SpliceFollowingOnchainChannelWatcher(Func<IOnchainChannelWatcher?> inner,
                                                Func<IChannelRestoreService?> restoreService,
                                                ILogger<SpliceFollowingOnchainChannelWatcher>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(restoreService);
        _inner = new Lazy<IOnchainChannelWatcher?>(inner);
        _restoreService = new Lazy<IChannelRestoreService?>(restoreService);
        _logger = logger ?? NullLogger<SpliceFollowingOnchainChannelWatcher>.Instance;
    }

    /// <summary>The node's own on-chain watcher (null when none is registered).</summary>
    public IOnchainChannelWatcher? Inner => _inner.Value;

    /// <inheritdoc />
    public async Task<FundingSpendOutcome?> HandleFundingSpentAsync(OutpointSpentEventArgs args,
                                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        try
        {
            if (_restoreService.Value is { } restore
             && await restore.TryHandleRecoveryFundingSpendAsync(args, cancellationToken))
                return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Only a recovery channel's spend that is no commitment gets this far: recording it as a close would end
            // the recovery for good, while leaving it lets the next start (or restorechanbackup again) follow it
            _logger.LogCritical(e, "Could not follow the spend {TxId} of the funding output of recovery channel "
                                 + "{ChannelId}; restart the node or run restorechanbackup again to follow it",
                                args.SpendingTransaction.TxId, args.ChannelId);
            return null;
        }

        if (Inner is { } inner)
            return await inner.HandleFundingSpentAsync(args, cancellationToken);

        _logger.LogCritical("The funding output of channel {ChannelId} was spent by {TxId}, and no on-chain watcher is "
                          + "registered", args.ChannelId, args.SpendingTransaction.TxId);
        return null;
    }
}
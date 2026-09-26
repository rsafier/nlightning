using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Protocol.Onion;

using Domain.Channels.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;

/// <summary>
/// The database-backed <see cref="IOnionReplayStore"/> (NL-078): one <c>OnionReplayEntries</c> row per packet HMAC,
/// owned by the incoming HTLC that recorded it and deleted once the chain passes that HTLC's <c>cltv_expiry</c>. It
/// survives restarts, so a replayed onion is detected whenever it arrives within its HTLC's lifetime.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryAddAsync"/> commits the new row in its own unit of work before it returns, so the HMAC is durable
/// before the caller forwards or fulfills the HTLC (and before its onion shared secret is stored). Calls are serialized
/// in this process, which makes the check and the insert one step: two HTLCs with the same onion can never both be
/// accepted. The store assumes it is the only writer of the table (one node process per database).
/// </para>
/// <para>
/// Pruning: <see cref="PruneAsync"/> deletes the entries the chain passed. <see cref="TryAddAsync"/> also prunes, at
/// most once per new height, using the chain monitor's last processed height (<c>BlockchainStates</c>), so the table
/// stays bounded without a block subscription of its own.
/// </para>
/// </remarks>
public sealed class PersistentOnionReplayStore : IOnionReplayStore, IDisposable
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PersistentOnionReplayStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private uint _prunedBelowHeight;

    public PersistentOnionReplayStore(IServiceScopeFactory serviceScopeFactory,
                                      ILogger<PersistentOnionReplayStore> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> TryAddAsync(ReadOnlyMemory<byte> hmac, ChannelId channelId, ulong htlcId,
                                        uint expiryHeight, CancellationToken cancellationToken = default)
    {
        if (hmac.Length != OnionConstants.HmacLength)
            throw new ArgumentException($"Onion HMAC must be {OnionConstants.HmacLength} bytes.", nameof(hmac));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await PruneToChainTipAsync(unitOfWork);

            var existing = await unitOfWork.OnionReplayDbRepository.GetByHmacAsync(hmac);
            if (existing is not null)
            {
                var owned = existing.IsOwnedBy(channelId, htlcId);
                if (!owned && _logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(
                        "Onion HMAC {Hmac} of HTLC {HtlcId} on channel {ChannelId} was recorded by HTLC {OwnerHtlcId} "
                      + "on channel {OwnerChannelId}: replay", Convert.ToHexString(hmac.Span), htlcId, channelId,
                        existing.HtlcId, existing.ChannelId);

                return owned;
            }

            unitOfWork.OnionReplayDbRepository.Add(new OnionReplayEntry(hmac.Span, channelId, htlcId, expiryHeight));
            await unitOfWork.SaveChangesAsync();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> PruneAsync(uint blockHeight, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            return await PruneBelowAsync(unitOfWork, blockHeight);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task PruneToChainTipAsync(IUnitOfWork unitOfWork)
    {
        var state = await unitOfWork.BlockchainStateDbRepository.GetStateAsync();
        if (state is null || state.LastProcessedHeight <= _prunedBelowHeight)
            return;

        await PruneBelowAsync(unitOfWork, state.LastProcessedHeight);
    }

    private async Task<int> PruneBelowAsync(IUnitOfWork unitOfWork, uint blockHeight)
    {
        var removed = await unitOfWork.OnionReplayDbRepository.DeleteExpiredAsync(blockHeight);
        if (blockHeight > _prunedBelowHeight)
            _prunedBelowHeight = blockHeight;

        if (removed > 0 && _logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Pruned {Count} onion replay entries that expired below height {Height}", removed,
                             blockHeight);

        return removed;
    }
}
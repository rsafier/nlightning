using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Persistence.Contexts;
using Persistence.Entities.Channel;

/// <summary>
/// The local signer's durable guard rows (NL-1345, table <c>ChannelSignerGuards</c>).
/// </summary>
/// <remarks>
/// A raise is one conditional <c>UPDATE</c> (higher numbers, a lower broadcast mark, a set data-loss flag), atomic per
/// row in every provider, so two processes writing at once never lower each other's values; the first raise of a
/// channel inserts its row and falls back to the update when a concurrent insert won. It commits on its own: use it
/// from a scope that stages nothing else (<c>ChannelSignerGuardStore</c> opens one per call).
/// </remarks>
public class ChannelSignerGuardDbRepository : IChannelSignerGuardDbRepository
{
    private readonly NLightningDbContext _context;

    public ChannelSignerGuardDbRepository(NLightningDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<ChannelSignerGuard?> GetAsync(ChannelId channelId)
    {
        var entity = await _context.ChannelSignerGuards.AsNoTracking()
                                   .SingleOrDefaultAsync(g => g.ChannelId == channelId);
        return entity is null ? null : MapToDomain(entity);
    }

    /// <inheritdoc />
    public async Task RaiseAsync(ChannelId channelId, ChannelSignerGuard guard)
    {
        if (await UpdateAsync(channelId, guard) > 0)
            return;

        var entity = MapToEntity(channelId, guard);
        _context.ChannelSignerGuards.Add(entity);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Another writer inserted the row first: raise it instead
            _context.Entry(entity).State = EntityState.Detached;
            if (await UpdateAsync(channelId, guard) == 0)
                throw;
        }
        finally
        {
            if (_context.Entry(entity).State != EntityState.Detached)
                _context.Entry(entity).State = EntityState.Detached;
        }
    }

    private Task<int> UpdateAsync(ChannelId channelId, ChannelSignerGuard guard)
    {
        var local = checked((long)guard.LocalCommitmentNumber);
        var revoked = ToLong(guard.RevokedCommitmentNumber);
        var remote = ToLong(guard.RemoteSignedCommitmentNumber);
        var broadcast = ToLong(guard.BroadcastSignedCommitmentNumber);

        return _context.ChannelSignerGuards
                       .Where(g => g.ChannelId == channelId)
                       .ExecuteUpdateAsync(s =>
                        {
                            s.SetProperty(g => g.LocalCommitmentNumber,
                                          g => g.LocalCommitmentNumber < local ? local : g.LocalCommitmentNumber);
                            if (revoked is { } r)
                                s.SetProperty(g => g.RevokedCommitmentNumber,
                                              g => g.RevokedCommitmentNumber == null || g.RevokedCommitmentNumber < r
                                                       ? r
                                                       : g.RevokedCommitmentNumber);
                            if (remote is { } m)
                                s.SetProperty(g => g.RemoteSignedCommitmentNumber,
                                              g => g.RemoteSignedCommitmentNumber == null
                                                || g.RemoteSignedCommitmentNumber < m
                                                       ? m
                                                       : g.RemoteSignedCommitmentNumber);
                            if (broadcast is { } b)
                                s.SetProperty(g => g.BroadcastSignedCommitmentNumber,
                                              g => g.BroadcastSignedCommitmentNumber == null
                                                || g.BroadcastSignedCommitmentNumber > b
                                                       ? b
                                                       : g.BroadcastSignedCommitmentNumber);
                            if (guard.DataLossDetected)
                                s.SetProperty(g => g.DataLossDetected, true);
                        });
    }

    private static ChannelSignerGuard MapToDomain(ChannelSignerGuardEntity entity) =>
        new((ulong)entity.LocalCommitmentNumber, (ulong?)entity.RevokedCommitmentNumber,
            (ulong?)entity.RemoteSignedCommitmentNumber, (ulong?)entity.BroadcastSignedCommitmentNumber,
            entity.DataLossDetected);

    private static ChannelSignerGuardEntity MapToEntity(ChannelId channelId, ChannelSignerGuard guard) =>
        new()
        {
            ChannelId = channelId,
            LocalCommitmentNumber = checked((long)guard.LocalCommitmentNumber),
            RevokedCommitmentNumber = ToLong(guard.RevokedCommitmentNumber),
            RemoteSignedCommitmentNumber = ToLong(guard.RemoteSignedCommitmentNumber),
            BroadcastSignedCommitmentNumber = ToLong(guard.BroadcastSignedCommitmentNumber),
            DataLossDetected = guard.DataLossDetected
        };

    private static long? ToLong(ulong? value) => value is { } v ? checked((long)v) : null;
}
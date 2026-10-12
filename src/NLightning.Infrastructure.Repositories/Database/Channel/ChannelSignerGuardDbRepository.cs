using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Fencing;
using Helpers;
using Persistence.Contexts;
using Persistence.Entities.Channel;

/// <summary>
/// The local signer's durable guard rows (NL-1345, table <c>ChannelSignerGuards</c>) and the broadcast sessions of its
/// taproot verification nonces (NL-1376, table <c>ChannelSignerBroadcastSessions</c>).
/// </summary>
/// <remarks>
/// A raise is one conditional <c>UPDATE</c> (higher numbers, a lower broadcast mark, a set data-loss flag), atomic per
/// row in every provider, so two processes writing at once never lower each other's values; the first raise of a
/// channel inserts its row and falls back to the update when a concurrent insert won. It commits on its own: use it
/// from a scope that stages nothing else (<c>ChannelSignerGuardStore</c> opens one per call).
/// <para>With a node write fence (NL-1342) a raise runs in one transaction checked by the fence before the commit
/// (<see cref="FencedWrite"/>): a fenced instance cannot raise the guard, so the signer refuses the operation and no
/// secret or signature leaves it. A raise only ever tightens the guard, but a fenced instance's raise could still
/// block the authorized instance (a broadcast mark or a data-loss flag of a process that must no longer act), and
/// refusing it costs nothing the sign-time fence check does not already refuse.</para>
/// </remarks>
public class ChannelSignerGuardDbRepository : IChannelSignerGuardDbRepository
{
    private readonly NLightningDbContext _context;
    private readonly INodeWriteFence? _writeFence;

    /// <param name="context">The context of the guard store's own scope.</param>
    /// <param name="writeFence">The node write fence every raise is checked against (NL-1342); null checks
    /// nothing.</param>
    public ChannelSignerGuardDbRepository(NLightningDbContext context, INodeWriteFence? writeFence = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _writeFence = writeFence;
    }

    /// <inheritdoc />
    public async Task<ChannelSignerGuard?> GetAsync(ChannelId channelId)
    {
        var entity = await _context.ChannelSignerGuards.AsNoTracking()
                                   .SingleOrDefaultAsync(g => g.ChannelId == channelId);
        return entity is null ? null : MapToDomain(entity);
    }

    /// <inheritdoc />
    public Task RaiseAsync(ChannelId channelId, ChannelSignerGuard guard) =>
        FencedWrite.RunAsync(_context, _writeFence, () => RaiseCoreAsync(channelId, guard));

    /// <remarks>Inside a transaction (with a fence) the insert's save runs under a savepoint EF creates, so a lost insert
    /// race rolls back to it and the update that follows still runs in the transaction.</remarks>
    private async Task RaiseCoreAsync(ChannelId channelId, ChannelSignerGuard guard)
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

    /// <inheritdoc />
    /// <remarks>One insert keyed by the nonce (channel, context, number); a key conflict means another session's claim
    /// (or this one's) was committed first, and the stored digest decides. Under the fence like a raise.</remarks>
    public Task<bool> ClaimBroadcastSessionAsync(ChannelId channelId, ChannelBroadcastSession session) =>
        FencedWrite.RunAsync(_context, _writeFence, () => ClaimBroadcastSessionCoreAsync(channelId, session));

    private async Task<bool> ClaimBroadcastSessionCoreAsync(ChannelId channelId, ChannelBroadcastSession session)
    {
        var nonceFundingTxId = session.NonceFundingTxId ?? TxId.Zero;
        var number = checked((long)session.CommitmentNumber);
        if (await GetBroadcastSessionDigestAsync(channelId, nonceFundingTxId, number) is { } stored)
            return stored == session.SessionDigest;

        var entity = new ChannelSignerBroadcastSessionEntity
        {
            ChannelId = channelId,
            NonceFundingTxId = nonceFundingTxId,
            CommitmentNumber = number,
            SessionDigest = session.SessionDigest
        };
        _context.ChannelSignerBroadcastSessions.Add(entity);
        try
        {
            await _context.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            // Another writer claimed the nonce first: its session decides
            _context.Entry(entity).State = EntityState.Detached;
            return await GetBroadcastSessionDigestAsync(channelId, nonceFundingTxId, number) is { } winner
                       ? winner == session.SessionDigest
                       : throw new InvalidOperationException("The broadcast session claim failed without a winner");
        }
        finally
        {
            if (_context.Entry(entity).State != EntityState.Detached)
                _context.Entry(entity).State = EntityState.Detached;
        }
    }

    private async Task<Hash?> GetBroadcastSessionDigestAsync(ChannelId channelId, TxId nonceFundingTxId, long number)
    {
        var row = await _context.ChannelSignerBroadcastSessions.AsNoTracking()
                                .SingleOrDefaultAsync(s => s.ChannelId == channelId
                                                        && s.NonceFundingTxId == nonceFundingTxId
                                                        && s.CommitmentNumber == number);
        return row?.SessionDigest;
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
using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Persistence.Contexts;
using Persistence.Entities.Channel;

/// <summary>
/// The revocation log (BOLT 5 plan O1-T1, table <c>RevokedCommitments</c>). Rows are staged by
/// <see cref="ChannelStateDbRepository.ApplyAsync"/> through <see cref="StageAsync"/>, in the save of the
/// <c>revoke_and_ack</c> transition.
/// </summary>
public class RevokedCommitmentDbRepository : BaseDbRepository<RevokedCommitmentEntity>, IRevokedCommitmentDbRepository
{
    private readonly NLightningDbContext _context;

    public RevokedCommitmentDbRepository(NLightningDbContext context) : base(context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<RevokedCommitmentModel?> GetAsync(ChannelId channelId, ulong number)
    {
        var entity = await DbSet.FindAsync(channelId, number);
        return entity is null || _context.Entry(entity).State == EntityState.Deleted
                   ? null
                   : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RevokedCommitmentModel>> GetByChannelIdAsync(ChannelId channelId)
    {
        var entities = await DbSet.AsNoTracking().Where(r => r.ChannelId == channelId).ToListAsync();
        return entities.OrderBy(r => r.Number).Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<ulong> GetLogStartAsync(ChannelId channelId)
    {
        var channel = await _context.Channels.AsNoTracking()
                                    .Where(c => c.ChannelId == channelId)
                                    .Select(c => new { c.RevocationLogFromNumber })
                                    .FirstOrDefaultAsync()
                   ?? throw new InvalidOperationException($"Channel {channelId} does not exist");
        return channel.RevocationLogFromNumber ?? 0;
    }

    /// <inheritdoc />
    public async Task DeleteByChannelIdAsync(ChannelId channelId)
    {
        var entities = await DbSet.Where(r => r.ChannelId == channelId).ToListAsync();
        DeleteRange(entities);
    }

    /// <summary>
    /// Stages the log row of <paramref name="revoked"/> when it has HTLCs (D2); does nothing otherwise. A row that
    /// exists already is rewritten with the same content (a replayed transition).
    /// </summary>
    /// <returns>True when a row was staged.</returns>
    internal async Task<bool> StageAsync(ChannelId channelId, RemoteCommit revoked)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        if (revoked.Spec.Htlcs.Count == 0)
            return false;

        var spec = revoked.Spec;
        var entity = await DbSet.FindAsync(channelId, revoked.Number);
        if (entity is null)
        {
            Insert(new RevokedCommitmentEntity
            {
                ChannelId = channelId,
                Number = revoked.Number,
                FeeratePerKw = spec.FeeratePerKw,
                LocalMsat = spec.LocalMsat,
                RemoteMsat = spec.RemoteMsat,
                Htlcs = CommitmentStateEncoding.EncodeSpecHtlcs(spec.Htlcs)
            });
            return true;
        }

        entity.FeeratePerKw = spec.FeeratePerKw;
        entity.LocalMsat = spec.LocalMsat;
        entity.RemoteMsat = spec.RemoteMsat;
        entity.Htlcs = CommitmentStateEncoding.EncodeSpecHtlcs(spec.Htlcs);
        var entry = _context.Entry(entity);
        if (entry.State == EntityState.Deleted)
            entry.State = EntityState.Modified;
        return true;
    }

    private static RevokedCommitmentModel MapEntityToDomain(RevokedCommitmentEntity entity) =>
        new(entity.ChannelId, entity.Number,
            new CommitmentSpec(CommitmentSide.Remote, entity.FeeratePerKw, entity.LocalMsat, entity.RemoteMsat,
                               CommitmentStateEncoding.DecodeSpecHtlcs(entity.Htlcs)));
}
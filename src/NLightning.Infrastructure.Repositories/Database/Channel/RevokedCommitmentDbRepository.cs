using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.ValueObjects;
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
    /// <remarks>Since migration <c>AddSpliceFundings</c> a number may be logged once per funding (SP-I5): this returns
    /// the row of the channel's current funding when there is one, else the one of the funding created first (lowest
    /// <c>ChannelFundings.Sequence</c>; rows of a funding without a <c>ChannelFundings</c> row come last, by txid). Use
    /// <see cref="GetAsync(ChannelId, TxId, ulong)"/> when the funding the commitment spends is known.</remarks>
    public async Task<RevokedCommitmentModel?> GetAsync(ChannelId channelId, ulong number)
    {
        var channel = await _context.Channels.FindAsync(channelId);
        if (channel is not null)
        {
            var current = await DbSet.FindAsync(channelId, number, channel.FundingTxId);
            if (current is not null && _context.Entry(current).State != EntityState.Deleted)
                return MapEntityToDomain(current);
        }

        var others = (await DbSet.Where(r => r.ChannelId == channelId && r.Number == number).ToListAsync())
                     .Where(r => _context.Entry(r).State != EntityState.Deleted)
                     .ToList();
        if (others.Count <= 1)
            return others.Count == 0 ? null : MapEntityToDomain(others[0]);

        var sequences = await _context.ChannelFundings.AsNoTracking()
                                      .Where(f => f.ChannelId == channelId)
                                      .Select(f => new { f.FundingTxId, f.Sequence })
                                      .ToListAsync();
        var first = others.OrderBy(r => sequences.FirstOrDefault(f => f.FundingTxId == r.FundingTxId)?.Sequence
                                     ?? int.MaxValue)
                          .ThenBy(r => r.FundingTxId.ToInternalHex(), StringComparer.Ordinal)
                          .First();
        return MapEntityToDomain(first);
    }

    /// <inheritdoc />
    public async Task<RevokedCommitmentModel?> GetAsync(ChannelId channelId, TxId fundingTxId, ulong number)
    {
        var entity = await DbSet.FindAsync(channelId, number, fundingTxId);
        return entity is null || _context.Entry(entity).State == EntityState.Deleted
                   ? null
                   : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RevokedCommitmentModel>> GetByChannelIdAsync(ChannelId channelId)
    {
        var entities = await DbSet.AsNoTracking().Where(r => r.ChannelId == channelId).ToListAsync();
        return entities.OrderBy(r => r.Number).ThenBy(r => r.FundingTxId.ToInternalHex(), StringComparer.Ordinal)
                       .Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    /// <remarks>Saved rows and the rows this unit of work staged (not the ones it deleted), by number.</remarks>
    public async Task<IReadOnlyList<RevokedCommitmentModel>> GetByFundingAsync(ChannelId channelId, TxId fundingTxId)
    {
        var rows = await DbSet.Where(r => r.ChannelId == channelId && r.FundingTxId == fundingTxId).ToListAsync();
        var known = new HashSet<RevokedCommitmentEntity>(rows, ReferenceEqualityComparer.Instance);
        rows.AddRange(DbSet.Local.Where(r => r.ChannelId == channelId && r.FundingTxId == fundingTxId
                                          && known.Add(r)));
        return rows.Where(r => _context.Entry(r).State != EntityState.Deleted)
                   .OrderBy(r => r.Number)
                   .Select(MapEntityToDomain)
                   .ToList();
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
    /// Stages the log row of <paramref name="revoked"/> on the funding <paramref name="fundingTxId"/> when it has HTLCs
    /// (D2); does nothing otherwise. A row that exists already is rewritten with the same content (a replayed
    /// transition).
    /// </summary>
    /// <returns>True when a row was staged.</returns>
    internal async Task<bool> StageAsync(ChannelId channelId, RemoteCommit revoked, TxId fundingTxId)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        if (revoked.Spec.Htlcs.Count == 0)
            return false;

        var spec = revoked.Spec;
        var entity = await DbSet.FindAsync(channelId, revoked.Number, fundingTxId);
        if (entity is null)
        {
            Insert(new RevokedCommitmentEntity
            {
                ChannelId = channelId,
                Number = revoked.Number,
                FundingTxId = fundingTxId,
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
                               CommitmentStateEncoding.DecodeSpecHtlcs(entity.Htlcs)))
        {
            FundingTxId = entity.FundingTxId
        };
}
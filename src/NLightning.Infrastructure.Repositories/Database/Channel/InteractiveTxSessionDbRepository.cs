using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Persistence.Contexts;
using Persistence.Entities.Channel;

/// <summary>
/// The persisted interactive-tx negotiations (table <c>InteractiveTxSessions</c>, migration
/// <c>AddInteractiveTxSessions</c>, splicing plan IT3-T2). Writes are staged and committed by the unit of work's save.
/// </summary>
/// <remarks>
/// <see cref="GetByIdAsync(ChannelId, Guid)"/>, <see cref="UpdateAsync"/> and <see cref="DeleteAsync"/> go through the
/// change tracker (they see what this unit of work staged); the list reads read what is saved.
/// </remarks>
public class InteractiveTxSessionDbRepository : BaseDbRepository<InteractiveTxSessionEntity>,
                                                IInteractiveTxSessionDbRepository
{
    private readonly NLightningDbContext _context;

    public InteractiveTxSessionDbRepository(NLightningDbContext context) : base(context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public void Add(InteractiveTxSessionModel session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var entity = new InteractiveTxSessionEntity
        {
            ChannelId = session.ChannelId,
            SessionId = session.SessionId,
            Purpose = (byte)session.Purpose,
            IsInitiator = session.IsInitiator,
            FeeratePerKw = session.FeeratePerKw,
            Locktime = session.Locktime,
            Inputs = [],
            Outputs = [],
            LocalContribution = [],
            CommitmentSignedSent = session.CommitmentSignedSent,
            CommitmentSignedReceived = session.CommitmentSignedReceived,
            TxSignaturesSent = session.TxSignaturesSent,
            TxSignaturesReceived = session.TxSignaturesReceived,
            State = (byte)session.State,
            CreatedAt = session.CreatedAt
        };
        CopyToEntity(session, entity);

        Insert(entity);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(InteractiveTxSessionModel session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var entity = await DbSet.FindAsync(session.ChannelId, session.SessionId);
        if (entity is null || _context.Entry(entity).State == EntityState.Deleted)
            throw new KeyNotFoundException(
                $"No interactive-tx session {session.SessionId} for channel {session.ChannelId}");

        CopyToEntity(session, entity);
    }

    /// <inheritdoc />
    public async Task<InteractiveTxSessionModel?> GetByIdAsync(ChannelId channelId, Guid sessionId)
    {
        // Through the change tracker, so a row this unit of work staged (or deleted) reads as it will be saved
        var entity = await DbSet.FindAsync(channelId, sessionId);
        if (entity is null || _context.Entry(entity).State == EntityState.Deleted)
            return null;

        return Map(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InteractiveTxSessionModel>> GetByChannelIdAsync(ChannelId channelId)
    {
        var entities = await DbSet.AsNoTracking().Where(e => e.ChannelId == channelId).ToListAsync();
        return OldestFirst(entities);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InteractiveTxSessionModel>> GetUnresolvedAsync()
    {
        const byte aborted = (byte)InteractiveTxSessionState.Aborted;
        var entities = await DbSet.AsNoTracking()
                                  .Where(e => e.ResolvedAt == null && e.State != aborted)
                                  .ToListAsync();
        return OldestFirst(entities);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(ChannelId channelId, Guid sessionId)
    {
        var entity = await DbSet.FindAsync(channelId, sessionId);
        if (entity is null || _context.Entry(entity).State == EntityState.Deleted)
            return false;

        Delete(entity);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> DeleteByChannelIdAsync(ChannelId channelId)
    {
        // Load the saved rows into the tracker, then take every tracked row of the channel (staged adds included)
        await DbSet.Where(e => e.ChannelId == channelId).LoadAsync();
        var entities = DbSet.Local
                            .Where(e => e.ChannelId == channelId
                                        && _context.Entry(e).State != EntityState.Deleted)
                            .ToList();
        foreach (var entity in entities)
            Delete(entity);

        return entities.Count;
    }

    // EF SQLite cannot order DateTimeOffset columns; the rows are few, so they are ordered in memory
    private static List<InteractiveTxSessionModel> OldestFirst(IEnumerable<InteractiveTxSessionEntity> entities)
    {
        return entities.OrderBy(e => e.CreatedAt)
                       .ThenBy(e => e.SessionId)
                       .Select(Map)
                       .ToList();
    }

    /// <summary>Every column except the key and <c>CreatedAt</c>, which never change.</summary>
    private static void CopyToEntity(InteractiveTxSessionModel session, InteractiveTxSessionEntity entity)
    {
        ArgumentNullException.ThrowIfNull(session.Inputs);
        ArgumentNullException.ThrowIfNull(session.Outputs);
        ArgumentNullException.ThrowIfNull(session.LocalContribution);

        entity.Purpose = (byte)session.Purpose;
        entity.IsInitiator = session.IsInitiator;
        entity.FeeratePerKw = session.FeeratePerKw;
        entity.Locktime = session.Locktime;
        entity.Inputs = InteractiveTxSessionEncoding.EncodeInputs(session.Inputs);
        entity.Outputs = InteractiveTxSessionEncoding.EncodeOutputs(session.Outputs);
        entity.LocalContribution = InteractiveTxSessionEncoding.EncodeContribution(session.LocalContribution);
        entity.LocalReservationId = session.LocalContribution.ReservationId;
        entity.ConstructedTx = session.ConstructedTx is null
                                   ? null
                                   : InteractiveTxSessionEncoding.EncodeConstructedTx(session.ConstructedTx);
        entity.OurWitnesses = session.OurWitnesses is null
                                  ? null
                                  : InteractiveTxSessionEncoding.EncodeWitnesses(session.OurWitnesses);
        entity.TheirWitnesses = session.TheirWitnesses is null
                                    ? null
                                    : InteractiveTxSessionEncoding.EncodeWitnesses(session.TheirWitnesses);
        entity.OurSharedInputSignature = session.OurSharedInputSignature?.Value.ToArray();
        entity.TheirSharedInputSignature = session.TheirSharedInputSignature?.Value.ToArray();
        entity.LocalFundingSatoshis = session.LocalFundingSatoshis;
        entity.TheirCommitmentSignature = session.TheirCommitmentSignature?.Value.ToArray();
        entity.TheirCommitmentPartialSignature = session.TheirCommitmentPartialSignature?.ToBytes();
        entity.CommitmentSignedSent = session.CommitmentSignedSent;
        entity.CommitmentSignedReceived = session.CommitmentSignedReceived;
        entity.TxSignaturesSent = session.TxSignaturesSent;
        entity.TxSignaturesReceived = session.TxSignaturesReceived;
        entity.State = (byte)session.State;
        entity.ResolvedAt = session.ResolvedAt;
    }

    private static InteractiveTxSessionModel Map(InteractiveTxSessionEntity entity)
    {
        var purpose = (InteractiveTxPurpose)entity.Purpose;
        if (!Enum.IsDefined(purpose))
            throw new InvalidOperationException(
                $"Interactive-tx session {entity.SessionId} has an unknown purpose {entity.Purpose}");

        var state = (InteractiveTxSessionState)entity.State;
        if (!Enum.IsDefined(state))
            throw new InvalidOperationException(
                $"Interactive-tx session {entity.SessionId} has an unknown state {entity.State}");

        return new InteractiveTxSessionModel
        {
            ChannelId = entity.ChannelId,
            SessionId = entity.SessionId,
            Purpose = purpose,
            IsInitiator = entity.IsInitiator,
            FeeratePerKw = entity.FeeratePerKw,
            Locktime = entity.Locktime,
            Inputs = InteractiveTxSessionEncoding.DecodeInputs(entity.Inputs),
            Outputs = InteractiveTxSessionEncoding.DecodeOutputs(entity.Outputs),
            LocalContribution =
                InteractiveTxSessionEncoding.DecodeContribution(entity.LocalContribution, entity.LocalReservationId),
            ConstructedTx = entity.ConstructedTx is null
                                ? null
                                : InteractiveTxSessionEncoding.DecodeConstructedTx(entity.ConstructedTx),
            OurWitnesses = entity.OurWitnesses is null
                               ? null
                               : InteractiveTxSessionEncoding.DecodeWitnesses(entity.OurWitnesses),
            TheirWitnesses = entity.TheirWitnesses is null
                                 ? null
                                 : InteractiveTxSessionEncoding.DecodeWitnesses(entity.TheirWitnesses),
            OurSharedInputSignature = entity.OurSharedInputSignature is null
                                          ? null
                                          : new CompactSignature(entity.OurSharedInputSignature),
            TheirSharedInputSignature = entity.TheirSharedInputSignature is null
                                            ? null
                                            : new CompactSignature(entity.TheirSharedInputSignature),
            LocalFundingSatoshis = entity.LocalFundingSatoshis,
            TheirCommitmentSignature = entity.TheirCommitmentSignature is null
                                           ? null
                                           : new CompactSignature(entity.TheirCommitmentSignature),
            TheirCommitmentPartialSignature = entity.TheirCommitmentPartialSignature is null
                                                  ? null
                                                  : new MusigPartialSignatureWithNonce(
                                                      entity.TheirCommitmentPartialSignature),
            CommitmentSignedSent = entity.CommitmentSignedSent,
            CommitmentSignedReceived = entity.CommitmentSignedReceived,
            TxSignaturesSent = entity.TxSignaturesSent,
            TxSignaturesReceived = entity.TxSignaturesReceived,
            State = state,
            CreatedAt = entity.CreatedAt,
            ResolvedAt = entity.ResolvedAt
        };
    }
}
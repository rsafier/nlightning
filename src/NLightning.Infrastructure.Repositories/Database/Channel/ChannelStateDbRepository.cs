using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Payments.Enums;
using Domain.Payments.ValueObjects;
using Persistence.Contexts;
using Persistence.Entities.Channel;

/// <summary>
/// Writes and reloads the commitment state machine of a channel (plan N5-T2), one row at a time by primary key.
/// </summary>
/// <remarks>
/// Nothing here saves: the caller commits a whole transition with one <c>SaveChangesAsync</c>, which EF runs in one
/// database transaction, so a crash leaves either the old or the new state (plan N5-T3).
/// </remarks>
public class ChannelStateDbRepository : IChannelStateDbRepository
{
    private readonly NLightningDbContext _context;
    private readonly RemoteShachainDbRepository _remoteShachainDbRepository;
    private readonly RevokedCommitmentDbRepository _revokedCommitmentDbRepository;

    public ChannelStateDbRepository(NLightningDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _remoteShachainDbRepository = new RemoteShachainDbRepository(context);
        _revokedCommitmentDbRepository = new RevokedCommitmentDbRepository(context);
    }

    /// <inheritdoc />
    public Task InitializeAsync(ChannelCommitments snapshot, ChannelStateExtras? extras = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var everything = new ChannelTransition(snapshot.Htlcs.Values.ToList(), [], [], FeeUpdatesChanged: true,
                                               LocalCommitChanged: true, RemoteCommitChanged: true,
                                               ScalarsChanged: true);
        return ApplyAsync(snapshot, everything, extras);
    }

    /// <inheritdoc />
    public async Task ApplyAsync(ChannelCommitments next, ChannelTransition transition,
                                 ChannelStateExtras? extras = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(transition);

        var channelId = next.ChannelId;
        var channel = await _context.Channels.FindAsync(channelId)
                   ?? throw new InvalidOperationException($"Channel {channelId} does not exist");
        WriteScalars(channel, next, extras);

        // Settled HTLCs keep their final state as an archive: the events of this transition must be re-derivable
        // after a crash (invariant I8). They are removed by PruneSettledHtlcsAsync.
        foreach (var htlc in transition.UpsertedHtlcs.Concat(transition.SettledHtlcs))
            await UpsertHtlcAsync(channelId, htlc);

        foreach (var htlc in transition.DroppedHtlcs)
        {
            var entity = await FindHtlcAsync(channelId, htlc.Key);
            if (entity is not null)
                _context.Htlcs.Remove(entity);
        }

        if (transition.FeeUpdatesChanged)
            await SyncFeeUpdatesAsync(channelId, next.FeeUpdates);

        if (transition.LocalCommitChanged)
        {
            var local = next.LocalCommit;
            await UpsertCommitmentAsync(channelId, CommitmentEntity.LocalCurrentSlot, local.Number, local.Spec, null,
                                        local.RemoteSignatures);
        }

        if (transition.RemoteCommitChanged)
        {
            var remote = next.RemoteCommit;
            await UpsertCommitmentAsync(channelId, CommitmentEntity.RemoteCurrentSlot, remote.Number, remote.Spec,
                                        remote.PerCommitmentPoint, null);

            if (next.RemoteNextCommit is { } pending)
            {
                await UpsertCommitmentAsync(channelId, CommitmentEntity.RemoteNextSlot, pending.Commit.Number,
                                            pending.Commit.Spec, pending.Commit.PerCommitmentPoint,
                                            pending.SentSignatures);
            }
            else
            {
                var stale = await FindCommitmentAsync(channelId, CommitmentEntity.RemoteNextSlot);
                if (stale is not null)
                    _context.Commitments.Remove(stale);
            }
        }

        // The revocation log (BOLT 5 plan O1-T1): the commitment the peer just revoked, in the same save as the
        // revoke_and_ack and its shachain entry, so a breach of it can be rebuilt output by output
        if (transition.RevokedRemoteCommit is { } revoked)
            await _revokedCommitmentDbRepository.StageAsync(channelId, revoked);

        if (extras?.RemoteShachain is { } shachain)
            await _remoteShachainDbRepository.SaveAsync(channelId, shachain);
    }

    /// <inheritdoc />
    public async Task<PersistedChannelState?> LoadAsync(ChannelId channelId, CommitmentParams @params)
    {
        ArgumentNullException.ThrowIfNull(@params);

        // Checked first so that a channel with legacy HTLC rows is refused even when it has no snapshot (NL-025)
        var htlcs = await _context.Htlcs.AsNoTracking().Where(h => h.ChannelId == channelId).ToListAsync();
        ThrowIfLegacyHtlcs(channelId, htlcs);

        var commitments = await _context.Commitments.AsNoTracking()
                                        .Where(c => c.ChannelId == channelId)
                                        .ToListAsync();
        if (commitments.Count == 0)
            return null;

        var channel = await _context.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.ChannelId == channelId)
                   ?? throw new InvalidOperationException($"Channel {channelId} does not exist");
        var feeUpdates = await _context.FeeUpdates.AsNoTracking().Where(f => f.ChannelId == channelId).ToListAsync();
        var shachain = await _remoteShachainDbRepository.GetByChannelIdAsync(channelId);

        return MapToDomain(channel, commitments, htlcs, feeUpdates, shachain, @params);
    }

    /// <inheritdoc />
    public async Task SetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc, Secret sharedSecret)
    {
        var entity = await FindHtlcAsync(channelId, htlc)
                  ?? throw new InvalidOperationException($"HTLC {htlc} of channel {channelId} does not exist");
        entity.OnionSharedSecret = ((byte[])sharedSecret).ToArray();
    }

    /// <inheritdoc />
    public async Task<Secret?> GetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc)
    {
        var direction = (byte)htlc.Direction;
        var secret = await _context.Htlcs.AsNoTracking()
                                   .Where(h => h.ChannelId == channelId && h.HtlcId == htlc.Id
                                            && h.Direction == direction)
                                   .Select(h => h.OnionSharedSecret)
                                   .FirstOrDefaultAsync();

        return secret is null ? (Secret?)null : new Secret(secret);
    }

    /// <inheritdoc />
    public async Task SetHtlcOriginAsync(ChannelId channelId, HtlcKey htlc, HtlcOrigin origin)
    {
        if (!origin.IsValid)
            throw new ArgumentException("The HTLC origin is not valid", nameof(origin));

        var entity = await FindHtlcAsync(channelId, htlc)
                  ?? throw new InvalidOperationException($"HTLC {htlc} of channel {channelId} does not exist");
        entity.OriginKind = (byte)origin.Kind;
        entity.OriginPaymentHash = origin.PaymentHash is { } paymentHash ? ((byte[])paymentHash).ToArray() : null;
        entity.OriginIncomingChannelId = origin.IncomingChannelId;
        entity.OriginIncomingHtlcId = origin.IncomingHtlcId;
    }

    /// <inheritdoc />
    public async Task<HtlcOrigin?> GetHtlcOriginAsync(ChannelId channelId, HtlcKey htlc)
    {
        var direction = (byte)htlc.Direction;
        var row = await _context.Htlcs.AsNoTracking()
                                .Where(h => h.ChannelId == channelId && h.HtlcId == htlc.Id
                                         && h.Direction == direction)
                                .Select(h => new
                                {
                                    h.OriginKind,
                                    h.OriginPaymentHash,
                                    h.OriginIncomingChannelId,
                                    h.OriginIncomingHtlcId
                                })
                                .FirstOrDefaultAsync();

        return row?.OriginKind is null
                   ? null
                   : MapOrigin(channelId, htlc, row.OriginKind.Value, row.OriginPaymentHash,
                               row.OriginIncomingChannelId, row.OriginIncomingHtlcId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(ChannelId ChannelId, HtlcKey Htlc)>> FindHtlcsByOriginAsync(HtlcOrigin origin)
    {
        if (!origin.IsValid)
            throw new ArgumentException("The HTLC origin is not valid", nameof(origin));

        var kind = (byte)origin.Kind;
        var query = _context.Htlcs.AsNoTracking().Where(h => h.OriginKind == kind);
        if (origin.Kind == HtlcOriginKind.Local)
        {
            var paymentHash = ((byte[])origin.PaymentHash!.Value).ToArray();
            query = query.Where(h => h.OriginPaymentHash == paymentHash);
        }
        else
        {
            ChannelId? incomingChannelId = origin.IncomingChannelId!.Value;
            var incomingHtlcId = origin.IncomingHtlcId;
            query = query.Where(h => h.OriginIncomingChannelId == incomingChannelId
                                  && h.OriginIncomingHtlcId == incomingHtlcId);
        }

        var rows = await query.Select(h => new { h.ChannelId, h.HtlcId, h.Direction }).ToListAsync();
        return rows.Select(r => (r.ChannelId, new HtlcKey((HtlcDirection)r.Direction, r.HtlcId)))
                   .OrderBy(r => r.ChannelId.ToString(), StringComparer.Ordinal)
                   .ThenBy(r => r.Item2)
                   .ToList();
    }

    /// <inheritdoc />
    public async Task PruneSettledHtlcsAsync(ChannelId channelId, IEnumerable<HtlcKey> htlcs)
    {
        ArgumentNullException.ThrowIfNull(htlcs);

        foreach (var key in htlcs)
        {
            var entity = await FindHtlcAsync(channelId, key);
            if (entity is null || !HtlcStateTable.IsDefined((HtlcState)entity.State)
                               || !HtlcStateTable.IsFinal((HtlcState)entity.State))
                continue;

            _context.Htlcs.Remove(entity);
        }
    }

    /// <summary>
    /// Rebuilds the state of a channel from its rows (also used by <see cref="ChannelDbRepository"/> on reload).
    /// </summary>
    internal static PersistedChannelState MapToDomain(ChannelEntity channel, IReadOnlyCollection<CommitmentEntity> rows,
                                                      IEnumerable<HtlcEntity> htlcRows,
                                                      IEnumerable<FeeUpdateEntity> feeUpdateRows,
                                                      IReadOnlyList<Domain.Protocol.Models.ShachainEntry> shachain,
                                                      CommitmentParams @params)
    {
        var localRow = rows.SingleOrDefault(c => c.Slot == CommitmentEntity.LocalCurrentSlot)
                    ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no local commitment");
        var remoteRow = rows.SingleOrDefault(c => c.Slot == CommitmentEntity.RemoteCurrentSlot)
                     ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no remote commitment");
        var remoteNextRow = rows.SingleOrDefault(c => c.Slot == CommitmentEntity.RemoteNextSlot);

        var openHtlcs = new List<HtlcRecord>();
        var settledHtlcs = new List<HtlcRecord>();
        foreach (var row in htlcRows.OrderBy(h => h.Direction).ThenBy(h => h.HtlcId))
        {
            var state = (HtlcState)row.State;
            ThrowIfLegacyHtlcs(channel.ChannelId, [row]);

            var record = MapHtlcToDomain(row);
            if (HtlcStateTable.IsFinal(state))
                settledHtlcs.Add(record);
            else
                openHtlcs.Add(record);
        }

        var feeUpdates = feeUpdateRows.Select(f => new FeeUpdate(f.Sequence, f.FeeratePerKw, (HtlcState)f.State));
        var localCommit = new LocalCommit(localRow.Number, MapSpec(localRow, CommitmentSide.Local),
                                          MapSignatures(localRow));
        var remoteCommit = new RemoteCommit(remoteRow.Number, MapSpec(remoteRow, CommitmentSide.Remote),
                                            MapPoint(remoteRow));
        RemoteNextCommit? remoteNextCommit = null;
        if (remoteNextRow is not null)
            remoteNextCommit = new RemoteNextCommit(
                new RemoteCommit(remoteNextRow.Number, MapSpec(remoteNextRow, CommitmentSide.Remote),
                                 MapPoint(remoteNextRow)),
                MapSignatures(remoteNextRow)
             ?? throw new InvalidOperationException(
                    $"Channel {channel.ChannelId} has an unacked remote commitment without signatures"));

        ChannelCommitments commitments;
        try
        {
            commitments = ChannelCommitments.Restore(channel.ChannelId, @params,
                                                     checked((ulong)channel.LocalBalanceMsat),
                                                     checked((ulong)channel.RemoteBalanceMsat), openHtlcs, feeUpdates,
                                                     channel.LocalNextHtlcId, channel.RemoteNextHtlcId, localCommit,
                                                     remoteCommit, remoteNextCommit,
                                                     channel.RemoteNextPerCommitmentPoint);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException(
                $"The stored commitment state of channel {channel.ChannelId} is inconsistent: {e.Message}", e);
        }

        ReadOnlyMemory<byte>? sentCommitDiff = null;
        if (channel.SentCommitDiff is not null)
            sentCommitDiff = channel.SentCommitDiff;
        return new PersistedChannelState(commitments, settledHtlcs, sentCommitDiff,
                                         (LastSentCommitmentMessage)channel.LastSentOrder, shachain);
    }

    /// <summary>
    /// HTLC rows in a legacy state (0-3) were written before the commitment state machine existed and cannot be
    /// mapped to it: refuse the channel instead of guessing (NL-025).
    /// </summary>
    internal static void ThrowIfLegacyHtlcs(ChannelId channelId, IEnumerable<HtlcEntity> htlcRows)
    {
        var legacy = htlcRows.FirstOrDefault(h => !HtlcStateTable.IsDefined((HtlcState)h.State));
        if (legacy is not null)
            throw new LegacyHtlcStateException(channelId, legacy.HtlcId, legacy.State);
    }

    /// <summary>
    /// The commitment scalars of the channel row. Only this repository writes them once a channel has a snapshot.
    /// </summary>
    internal static void WriteScalars(ChannelEntity channel, ChannelCommitments next, ChannelStateExtras? extras)
    {
        channel.LocalBalanceMsat = checked((long)next.LocalBalanceMsat);
        channel.RemoteBalanceMsat = checked((long)next.RemoteBalanceMsat);
        channel.LocalNextHtlcId = next.LocalNextHtlcId;
        channel.RemoteNextHtlcId = next.RemoteNextHtlcId;
        channel.LocalCommitmentNumber = next.LocalCommit.Number;
        channel.RemoteCommitmentNumber = next.RemoteCommit.Number;

        // The engine revokes our previous commitment in the transition that accepts the new one, and the peer's
        // current commitment is the one after the last it revoked
        channel.LocalRevocationNumber = next.LocalCommit.Number;
        channel.RemoteRevocationNumber = next.RemoteCommit.Number;
        channel.RemoteNextPerCommitmentPoint = next.RemoteNextPerCommitmentPoint;
        channel.MaxDustHtlcExposureMsat = next.Params.MaxDustHtlcExposureMsat;

        if (extras?.SentCommitDiff is { } diff)
            channel.SentCommitDiff = diff.ToArray();
        if (next.RemoteNextCommit is null)
            channel.SentCommitDiff = null;
        if (extras?.LastSent is { } lastSent)
            channel.LastSentOrder = (byte)lastSent;
    }

    private static HtlcOrigin MapOrigin(ChannelId channelId, HtlcKey htlc, byte kind, byte[]? paymentHash,
                                        ChannelId? incomingChannelId, ulong? incomingHtlcId)
    {
        var origin = (HtlcOriginKind)kind switch
        {
            HtlcOriginKind.Local when paymentHash is not null => HtlcOrigin.Local(new Hash(paymentHash)),
            HtlcOriginKind.Forwarded when incomingChannelId is { } channel && incomingHtlcId is { } id =>
                HtlcOrigin.Forwarded(channel, id),
            _ => default
        };

        return origin.IsValid
                   ? origin
                   : throw new InvalidOperationException(
                         $"HTLC {htlc} of channel {channelId} has an inconsistent origin (kind {kind})");
    }

    private static HtlcRecord MapHtlcToDomain(HtlcEntity row)
    {
        HtlcRemoval? removal = null;
        if (row.RemovalKind is { } kind)
        {
            removal = (HtlcRemovalKind)kind switch
            {
                HtlcRemovalKind.Fulfill => HtlcRemoval.Fulfill(
                    new Secret(row.PaymentPreimage
                            ?? throw new InvalidOperationException($"Fulfilled HTLC {row.HtlcId} has no preimage"))),
                HtlcRemovalKind.Fail => HtlcRemoval.Fail(row.FailReason ?? []),
                HtlcRemovalKind.FailMalformed => HtlcRemoval.FailMalformed(row.FailureCode ?? 0,
                                                                           row.Sha256OfOnion ?? []),
                _ => throw new InvalidOperationException($"HTLC {row.HtlcId} has unknown removal kind {kind}")
            };
        }

        return new HtlcRecord((HtlcDirection)row.Direction, row.HtlcId, row.AmountMsat, new Hash(row.PaymentHash),
                              row.CltvExpiry, (HtlcState)row.State, removal, row.OnionRoutingPacket,
                              row.PathKey is null ? (CompactPubKey?)null : new CompactPubKey(row.PathKey),
                              row.KnownPreimage is null ? (Secret?)null : new Secret(row.KnownPreimage));
    }

    private static void MapHtlcToEntity(HtlcRecord htlc, HtlcEntity entity)
    {
        entity.AmountMsat = htlc.AmountMsat;
        entity.PaymentHash = ((byte[])htlc.PaymentHash).ToArray();
        entity.CltvExpiry = htlc.CltvExpiry;
        entity.State = (byte)htlc.State;
        entity.OnionRoutingPacket = htlc.OnionRoutingPacket.ToArray();
        entity.PathKey = htlc.PathKey is { } pathKey ? ((byte[])pathKey).ToArray() : null;
        entity.KnownPreimage = htlc.KnownPreimage is { } known ? ((byte[])known).ToArray() : null;

        var removal = htlc.Removal;
        entity.RemovalKind = removal is null ? null : (byte)removal.Kind;
        entity.PaymentPreimage = removal?.PaymentPreimage is { } preimage ? ((byte[])preimage).ToArray() : null;
        entity.FailReason = removal?.Kind == HtlcRemovalKind.Fail ? removal.Reason.ToArray() : null;
        entity.FailureCode = removal?.Kind == HtlcRemovalKind.FailMalformed ? removal.FailureCode : null;
        entity.Sha256OfOnion = removal?.Kind == HtlcRemovalKind.FailMalformed ? removal.Sha256OfOnion.ToArray() : null;
    }

    private static CommitmentSpec MapSpec(CommitmentEntity row, CommitmentSide holder) =>
        new(holder, row.FeeratePerKw, row.LocalMsat, row.RemoteMsat,
            CommitmentStateEncoding.DecodeSpecHtlcs(row.Htlcs));

    private static CommitmentSignatures? MapSignatures(CommitmentEntity row) =>
        row.Signature is null
            ? null
            : new CommitmentSignatures(new CompactSignature(row.Signature),
                                       CommitmentStateEncoding.DecodeSignatures(row.HtlcSignatures ?? []));

    private static CompactPubKey MapPoint(CommitmentEntity row) =>
        new(row.PerCommitmentPoint
         ?? throw new InvalidOperationException($"Remote commitment {row.Number} has no per-commitment point"));

    private async Task UpsertHtlcAsync(ChannelId channelId, HtlcRecord htlc)
    {
        var entity = await FindHtlcAsync(channelId, htlc.Key);
        if (entity is null)
        {
            entity = new HtlcEntity
            {
                ChannelId = channelId,
                HtlcId = htlc.Id,
                Direction = (byte)htlc.Direction,
                AmountMsat = htlc.AmountMsat,
                PaymentHash = [],
                CltvExpiry = htlc.CltvExpiry,
                State = (byte)htlc.State,
                OnionRoutingPacket = []
            };
            MapHtlcToEntity(htlc, entity);
            _context.Htlcs.Add(entity);
            return;
        }

        MapHtlcToEntity(htlc, entity);
        ReviveIfDeleted(entity);
    }

    private async Task SyncFeeUpdatesAsync(ChannelId channelId, IReadOnlyList<FeeUpdate> feeUpdates)
    {
        var existing = await _context.FeeUpdates.Where(f => f.ChannelId == channelId).ToListAsync();
        existing.AddRange(_context.FeeUpdates.Local.Where(f => f.ChannelId == channelId && !existing.Contains(f)));
        var bySequence = existing.GroupBy(f => f.Sequence).ToDictionary(g => g.Key, g => g.First());

        foreach (var feeUpdate in feeUpdates)
        {
            if (bySequence.Remove(feeUpdate.Sequence, out var entity))
            {
                entity.FeeratePerKw = feeUpdate.FeeratePerKw;
                entity.State = (byte)feeUpdate.State;
                ReviveIfDeleted(entity);
            }
            else
            {
                _context.FeeUpdates.Add(new FeeUpdateEntity
                {
                    ChannelId = channelId,
                    Sequence = feeUpdate.Sequence,
                    FeeratePerKw = feeUpdate.FeeratePerKw,
                    State = (byte)feeUpdate.State
                });
            }
        }

        foreach (var removed in bySequence.Values)
        {
            if (_context.Entry(removed).State != EntityState.Deleted)
                _context.FeeUpdates.Remove(removed);
        }
    }

    private async Task UpsertCommitmentAsync(ChannelId channelId, byte slot, ulong number, CommitmentSpec spec,
                                             CompactPubKey? point, CommitmentSignatures? signatures)
    {
        var entity = await FindCommitmentAsync(channelId, slot);
        var isNew = entity is null;
        entity ??= new CommitmentEntity
        {
            ChannelId = channelId,
            Slot = slot,
            Number = number,
            FeeratePerKw = spec.FeeratePerKw,
            LocalMsat = spec.LocalMsat,
            RemoteMsat = spec.RemoteMsat,
            Htlcs = []
        };

        entity.Number = number;
        entity.FeeratePerKw = spec.FeeratePerKw;
        entity.LocalMsat = spec.LocalMsat;
        entity.RemoteMsat = spec.RemoteMsat;
        entity.Htlcs = CommitmentStateEncoding.EncodeSpecHtlcs(spec.Htlcs);
        entity.PerCommitmentPoint = point is { } p ? ((byte[])p).ToArray() : null;
        entity.Signature = signatures?.Signature.Value.ToArray();
        entity.HtlcSignatures = signatures is null
                                    ? null
                                    : CommitmentStateEncoding.EncodeSignatures(signatures.HtlcSignatures);

        if (isNew)
            _context.Commitments.Add(entity);
        else
            ReviveIfDeleted(entity);
    }

    private async Task<HtlcEntity?> FindHtlcAsync(ChannelId channelId, HtlcKey key) =>
        await _context.Htlcs.FindAsync(channelId, key.Id, (byte)key.Direction);

    private async Task<CommitmentEntity?> FindCommitmentAsync(ChannelId channelId, byte slot) =>
        await _context.Commitments.FindAsync(channelId, slot);

    /// <summary>A row removed earlier in the same unit of work and written again is an update, not a delete.</summary>
    private void ReviveIfDeleted(object entity)
    {
        var entry = _context.Entry(entity);
        if (entry.State == EntityState.Deleted)
            entry.State = EntityState.Modified;
    }
}
using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Persistence.Contexts;
using Persistence.Entities.Channel;

/// <summary>
/// The fundings of a channel (table <c>ChannelFundings</c>), the commitment slots of its pending splice fundings, their
/// revocation-log rows and the channel row's dual-funding columns (migration <c>AddSpliceFundings</c>, splicing plan
/// SP1-C-T4). Everything is staged; key reads go through the change tracker.
/// </summary>
public class ChannelFundingDbRepository : IChannelFundingDbRepository
{
    private readonly NLightningDbContext _context;
    private readonly ChannelStateDbRepository _channelStateDbRepository;
    private readonly RevokedCommitmentDbRepository _revokedCommitmentDbRepository;

    public ChannelFundingDbRepository(NLightningDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _channelStateDbRepository = new ChannelStateDbRepository(context);
        _revokedCommitmentDbRepository = new RevokedCommitmentDbRepository(context);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChannelFunding>> GetByChannelIdAsync(ChannelId channelId) =>
        (await GetEntitiesAsync(_context, channelId)).Select(MapToDomain).ToList();

    /// <inheritdoc />
    public async Task<FundingSet?> GetFundingSetAsync(ChannelId channelId) =>
        ToFundingSet(await GetByChannelIdAsync(channelId));

    /// <inheritdoc />
    public async Task UpsertAsync(ChannelId channelId, ChannelFunding funding)
    {
        ArgumentNullException.ThrowIfNull(funding);
        var channel = await FindChannelAsync(channelId);
        if (funding.Status == ChannelFundingStatus.Current
         && (funding.FundingTxId != channel.FundingTxId || funding.OutputIndex != channel.FundingOutputIndex))
            throw new InvalidOperationException(
                $"Funding {funding.FundingTxId} is not the current funding of channel {channelId}; lock it instead");

        await StageAsync(channelId, funding);
    }

    /// <inheritdoc />
    public async Task ApplyLockAsync(ChannelId channelId, ChannelFunding newCurrent,
                                     IReadOnlyList<ChannelFunding> retired)
    {
        ArgumentNullException.ThrowIfNull(newCurrent);
        ArgumentNullException.ThrowIfNull(retired);
        if (newCurrent.Status != ChannelFundingStatus.Current)
            throw new ArgumentException($"The locked funding must be Current, not {newCurrent.Status}",
                                        nameof(newCurrent));
        if (retired.Any(f => f.Status is not (ChannelFundingStatus.Replaced or ChannelFundingStatus.Discarded)))
            throw new ArgumentException("Retired fundings must be Replaced or Discarded", nameof(retired));

        var channel = await FindChannelAsync(channelId);
        if (await FindEntityAsync(channelId, newCurrent.FundingTxId) is null)
            throw new InvalidOperationException(
                $"Funding {newCurrent.FundingTxId} of channel {channelId} is not stored; it cannot be locked");

        foreach (var funding in retired)
        {
            await StageAsync(channelId, funding);
            await DeleteCommitmentSlotsAsync(channelId, funding.FundingTxId);
        }

        // The channel row keeps the current funding (denormalized) so every single-funding reader follows the lock;
        // the commitment state machine's slots become the new funding's in this same save
        channel.FundingTxId = newCurrent.FundingTxId;
        channel.FundingOutputIndex = newCurrent.OutputIndex;
        channel.FundingAmountSatoshis = checked((long)newCurrent.CapacitySatoshis);
        if (newCurrent.ShortChannelId is { } shortChannelId)
            channel.ShortChannelId = shortChannelId;

        await StageAsync(channelId, newCurrent);
    }

    /// <inheritdoc />
    public async Task StageLocalCommitmentAsync(ChannelId channelId, TxId fundingTxId, LocalCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.RemoteSignatures is null)
            throw new InvalidOperationException(
                $"The local commitment {commit.Number} for funding {fundingTxId} carries no remote signatures");

        await EnsurePendingFundingAsync(channelId, fundingTxId);
        await _channelStateDbRepository.UpsertCommitmentAsync(channelId, CommitmentEntity.LocalCurrentSlot,
                                                              fundingTxId, commit.Number, commit.Spec, null,
                                                              commit.RemoteSignatures);
    }

    /// <inheritdoc />
    public async Task StageRemoteCommitmentAsync(ChannelId channelId, TxId fundingTxId, RemoteCommit commit,
                                                 CommitmentSignatures? sentSignatures)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await EnsurePendingFundingAsync(channelId, fundingTxId);
        await _channelStateDbRepository.UpsertCommitmentAsync(channelId, CommitmentEntity.RemoteCurrentSlot,
                                                              fundingTxId, commit.Number, commit.Spec,
                                                              commit.PerCommitmentPoint, sentSignatures);
    }

    /// <inheritdoc />
    public async Task StageRemoteNextCommitmentAsync(ChannelId channelId, TxId fundingTxId, RemoteNextCommit? next)
    {
        await EnsurePendingFundingAsync(channelId, fundingTxId);
        await _channelStateDbRepository.SyncRemoteNextAsync(channelId, fundingTxId, next);
    }

    /// <inheritdoc />
    public async Task<LocalCommit?> GetLocalCommitmentAsync(ChannelId channelId, TxId fundingTxId)
    {
        var row = await FindSlotAsync(channelId, CommitmentEntity.LocalCurrentSlot, fundingTxId);
        return row is null
                   ? null
                   : new LocalCommit(row.Number, ChannelStateDbRepository.MapSpec(row, CommitmentSide.Local),
                                     ChannelStateDbRepository.MapSignatures(row));
    }

    /// <inheritdoc />
    public async Task<(RemoteCommit Commit, CommitmentSignatures? SentSignatures)?> GetRemoteCommitmentAsync(
        ChannelId channelId, TxId fundingTxId)
    {
        var row = await FindSlotAsync(channelId, CommitmentEntity.RemoteCurrentSlot, fundingTxId);
        return row is null ? null : (MapRemote(row), ChannelStateDbRepository.MapSignatures(row));
    }

    /// <inheritdoc />
    public async Task<RemoteNextCommit?> GetRemoteNextCommitmentAsync(ChannelId channelId, TxId fundingTxId)
    {
        var row = await FindSlotAsync(channelId, CommitmentEntity.RemoteNextSlot, fundingTxId);
        if (row is null)
            return null;

        return new RemoteNextCommit(MapRemote(row),
                                    ChannelStateDbRepository.MapSignatures(row)
                                 ?? throw new InvalidOperationException(
                                        $"The unacked remote commitment of funding {fundingTxId} has no signatures"));
    }

    /// <inheritdoc />
    public async Task<bool> StageRevokedCommitmentAsync(ChannelId channelId, TxId fundingTxId, RemoteCommit revoked)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        _ = await FindChannelAsync(channelId);
        return await _revokedCommitmentDbRepository.StageAsync(channelId, revoked, fundingTxId);
    }

    /// <inheritdoc />
    public async Task SetDualFundedAsync(ChannelId channelId, LightningMoney localContribution,
                                         LightningMoney remoteContribution)
    {
        ArgumentNullException.ThrowIfNull(localContribution);
        ArgumentNullException.ThrowIfNull(remoteContribution);
        var channel = await FindChannelAsync(channelId);
        channel.IsDualFunded = true;
        channel.LocalFundingContributionSatoshis = checked((long)localContribution.Satoshi);
        channel.RemoteFundingContributionSatoshis = checked((long)remoteContribution.Satoshi);
    }

    /// <inheritdoc />
    public async Task<(LightningMoney Local, LightningMoney Remote)?> GetDualFundedContributionsAsync(
        ChannelId channelId)
    {
        var channel = await _context.Channels.FindAsync(channelId);
        if (channel is not { IsDualFunded: true })
            return null;

        return (LightningMoney.Satoshis(channel.LocalFundingContributionSatoshis ?? 0),
                LightningMoney.Satoshis(channel.RemoteFundingContributionSatoshis ?? 0));
    }

    /// <inheritdoc />
    public async Task SetPushAmountAsync(ChannelId channelId, LightningMoney pushAmount)
    {
        ArgumentNullException.ThrowIfNull(pushAmount);
        var channel = await FindChannelAsync(channelId);
        channel.PushAmountMsat = checked((long)pushAmount.MilliSatoshi);
    }

    /// <inheritdoc />
    public async Task<LightningMoney?> GetPushAmountAsync(ChannelId channelId)
    {
        // A row this unit of work tracks because ChannelDbRepository.UpdateAsync staged the model holds the model's
        // values, and the model never carries the push (written only by SetPushAmountAsync): the tracked value counts
        // only for a row added here or a push set here, else the stored one is read
        var tracked = _context.Channels.Local.FirstOrDefault(c => c.ChannelId == channelId);
        if (tracked is not null)
        {
            var entry = _context.Entry(tracked);
            if (entry.State == EntityState.Added || entry.Property(c => c.PushAmountMsat).IsModified)
                return ToMoney(tracked.PushAmountMsat);
        }

        var stored = await _context.Channels.AsNoTracking()
                                   .Where(c => c.ChannelId == channelId)
                                   .Select(c => c.PushAmountMsat)
                                   .FirstOrDefaultAsync();
        return ToMoney(stored);

        static LightningMoney? ToMoney(long? pushMsat) =>
            pushMsat is { } msat ? LightningMoney.MilliSatoshis(msat) : null;
    }

    /// <summary>
    /// The funding rows of a channel, saved and staged (not the ones this unit of work deleted), in creation order.
    /// </summary>
    internal static async Task<List<ChannelFundingEntity>> GetEntitiesAsync(NLightningDbContext context,
                                                                            ChannelId channelId)
    {
        // A tracking query returns the tracked instance of rows this context knows; added ones are only in Local
        var rows = await context.ChannelFundings.Where(f => f.ChannelId == channelId).ToListAsync();
        var known = new HashSet<ChannelFundingEntity>(rows, ReferenceEqualityComparer.Instance);
        rows.AddRange(context.ChannelFundings.Local.Where(f => f.ChannelId == channelId && known.Add(f)));

        return rows.Where(f => context.Entry(f).State != EntityState.Deleted)
                   .OrderBy(f => f.Sequence)
                   .ToList();
    }

    /// <summary>The <see cref="FundingSet"/> of stored fundings, or null without a current one.</summary>
    internal static FundingSet? ToFundingSet(IReadOnlyList<ChannelFunding> fundings)
    {
        var current = fundings.FirstOrDefault(f => f.Status == ChannelFundingStatus.Current);
        return current is null
                   ? null
                   : new FundingSet(current, fundings.Where(f => f.Status == ChannelFundingStatus.Pending).ToList());
    }

    internal static ChannelFunding MapToDomain(ChannelFundingEntity entity) =>
        new(entity.FundingTxId, entity.OutputIndex, checked((ulong)entity.CapacitySatoshis),
            entity.LocalFundingPubKey, entity.RemoteFundingPubKey, entity.LocalFundingKeyIndex,
            entity.LocalBalanceDeltaMsat, entity.RemoteBalanceDeltaMsat, ToKind(entity.Kind), ToStatus(entity.Status),
            entity.FeeratePerKw, entity.Locktime, entity.RbfOf, entity.ConfirmedHeight, entity.ShortChannelId,
            entity.SpliceLockedSent, entity.SpliceLockedReceived, entity.AnnouncementSignaturesReceived);

    /// <summary>
    /// A new <see cref="ChannelFundingEntity"/> from <paramref name="funding"/> (for a channel's initial funding written
    /// with the channel row).
    /// </summary>
    internal static ChannelFundingEntity CreateEntity(ChannelId channelId, ChannelFunding funding, int sequence)
    {
        var entity = new ChannelFundingEntity
        {
            ChannelId = channelId,
            FundingTxId = funding.FundingTxId,
            OutputIndex = funding.OutputIndex,
            CapacitySatoshis = 0,
            LocalFundingPubKey = funding.LocalFundingPubKey,
            RemoteFundingPubKey = funding.RemoteFundingPubKey,
            LocalFundingKeyIndex = funding.LocalFundingKeyIndex,
            LocalBalanceDeltaMsat = 0,
            RemoteBalanceDeltaMsat = 0,
            Kind = 0,
            Status = 0,
            Sequence = sequence
        };
        CopyFields(funding, entity);
        return entity;
    }

    internal static void CopyFields(ChannelFunding funding, ChannelFundingEntity entity)
    {
        entity.OutputIndex = funding.OutputIndex;
        entity.CapacitySatoshis = checked((long)funding.CapacitySatoshis);
        entity.LocalFundingPubKey = funding.LocalFundingPubKey;
        entity.RemoteFundingPubKey = funding.RemoteFundingPubKey;
        entity.LocalFundingKeyIndex = funding.LocalFundingKeyIndex;
        entity.LocalBalanceDeltaMsat = funding.LocalBalanceDeltaMsat;
        entity.RemoteBalanceDeltaMsat = funding.RemoteBalanceDeltaMsat;
        entity.Kind = (byte)funding.Kind;
        entity.Status = (byte)funding.Status;
        entity.FeeratePerKw = funding.FeeratePerKw;
        entity.Locktime = funding.Locktime;
        entity.RbfOf = funding.RbfOf;
        entity.ConfirmedHeight = funding.ConfirmedHeight;
        entity.ShortChannelId = funding.ShortChannelId;
        entity.SpliceLockedSent = funding.SpliceLockedSent;
        entity.SpliceLockedReceived = funding.SpliceLockedReceived;
        entity.AnnouncementSignaturesReceived = funding.AnnouncementSignaturesReceived;
    }

    private static ChannelFundingKind ToKind(byte kind) =>
        Enum.IsDefined((ChannelFundingKind)kind)
            ? (ChannelFundingKind)kind
            : throw new InvalidOperationException($"Unknown channel funding kind {kind}");

    private static ChannelFundingStatus ToStatus(byte status) =>
        Enum.IsDefined((ChannelFundingStatus)status)
            ? (ChannelFundingStatus)status
            : throw new InvalidOperationException($"Unknown channel funding status {status}");

    private static RemoteCommit MapRemote(CommitmentEntity row) =>
        new(row.Number, ChannelStateDbRepository.MapSpec(row, CommitmentSide.Remote),
            ChannelStateDbRepository.MapPoint(row));

    private async Task StageAsync(ChannelId channelId, ChannelFunding funding)
    {
        var entity = await FindEntityAsync(channelId, funding.FundingTxId);
        if (entity is null)
        {
            var existing = await GetEntitiesAsync(_context, channelId);
            var sequence = existing.Count == 0 ? 0 : existing.Max(f => f.Sequence) + 1;
            _context.ChannelFundings.Add(CreateEntity(channelId, funding, sequence));
            return;
        }

        CopyFields(funding, entity);
        var entry = _context.Entry(entity);
        if (entry.State == EntityState.Deleted)
            entry.State = EntityState.Modified;
    }

    private async Task<ChannelEntity> FindChannelAsync(ChannelId channelId) =>
        await _context.Channels.FindAsync(channelId)
     ?? throw new InvalidOperationException($"Channel {channelId} does not exist");

    private async Task<ChannelFundingEntity?> FindEntityAsync(ChannelId channelId, TxId fundingTxId)
    {
        var entity = await _context.ChannelFundings.FindAsync(channelId, fundingTxId);
        return entity is null || _context.Entry(entity).State == EntityState.Deleted ? null : entity;
    }

    /// <summary>The funding must be one of the channel's, and not its current one (the state machine's slots).</summary>
    private async Task EnsurePendingFundingAsync(ChannelId channelId, TxId fundingTxId)
    {
        var channel = await FindChannelAsync(channelId);
        if (channel.FundingTxId == fundingTxId)
            throw new InvalidOperationException(
                $"Funding {fundingTxId} is the current funding of channel {channelId}: its commitments are the state "
              + "machine's");

        if (await FindEntityAsync(channelId, fundingTxId) is null)
            throw new InvalidOperationException($"Funding {fundingTxId} is not a funding of channel {channelId}");
    }

    private async Task<CommitmentEntity?> FindSlotAsync(ChannelId channelId, byte slot, TxId fundingTxId)
    {
        var row = await _channelStateDbRepository.FindCommitmentAsync(channelId, slot, fundingTxId);
        return row is null || _context.Entry(row).State == EntityState.Deleted ? null : row;
    }

    private async Task DeleteCommitmentSlotsAsync(ChannelId channelId, TxId fundingTxId)
    {
        foreach (var slot in new[]
                 {
                     CommitmentEntity.LocalCurrentSlot, CommitmentEntity.RemoteCurrentSlot,
                     CommitmentEntity.RemoteNextSlot
                 })
        {
            var row = await FindSlotAsync(channelId, slot, fundingTxId);
            if (row is not null)
                _context.Commitments.Remove(row);
        }
    }
}
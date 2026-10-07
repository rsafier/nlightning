using System.Collections;
using System.Collections.Immutable;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Domain.Protocol.Models;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Bitcoin;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Persistence.Contexts;
using Persistence.Entities.Channel;

public class ChannelDbRepository : BaseDbRepository<ChannelEntity>, IChannelDbRepository
{
    /// <summary>
    /// Compares primary keys given as arrays of key values, element by element.
    /// </summary>
    private static readonly IEqualityComparer<object> s_keyComparer =
        EqualityComparer<object>.Create((x, y) => StructuralComparisons.StructuralEqualityComparer.Equals(x, y),
                                        x => StructuralComparisons.StructuralEqualityComparer.GetHashCode(x));

    /// <summary>
    /// Channel columns written only by <see cref="ChannelStateDbRepository"/> (plan N5-T2): <see cref="UpdateAsync"/>
    /// never touches them.
    /// </summary>
    private static readonly string[] s_stateOnlyColumns =
    [
        nameof(ChannelEntity.RemoteNextPerCommitmentPoint),
        nameof(ChannelEntity.RemoteNextNonces),
        nameof(ChannelEntity.SentCommitDiff),
        nameof(ChannelEntity.LastSentOrder),
        nameof(ChannelEntity.MaxDustHtlcExposureMsat),

        // Set by migration AddOnchainResolution only (BOLT 5 plan O1-T3)
        nameof(ChannelEntity.RevocationLogFromNumber),

        // Written only through ChannelFundingDbRepository (migration AddSpliceFundings, splicing plan wave DF)
        nameof(ChannelEntity.IsDualFunded),
        nameof(ChannelEntity.LocalFundingContributionSatoshis),
        nameof(ChannelEntity.RemoteFundingContributionSatoshis),

        // Written only through ChannelFundingDbRepository.SetPushAmountAsync (NL-605, migration AddAccountingEvents)
        nameof(ChannelEntity.PushAmountMsat),

        // The operator's label and tags (NL-602 A3-T1, migration AddAccountingFinancial): written by AddAsync only, so
        // a model that lost them (rebuilt by a flow that does not copy them) never clears them
        nameof(ChannelEntity.Label),
        nameof(ChannelEntity.Tags)
    ];

    /// <summary>
    /// The current funding columns: once a splice was locked they follow <c>ChannelFundingDbRepository.ApplyLockAsync</c>,
    /// and <see cref="UpdateAsync"/> leaves them alone, so a model that predates the lock never moves them back.
    /// </summary>
    private static readonly string[] s_currentFundingColumns =
    [
        nameof(ChannelEntity.FundingTxId),
        nameof(ChannelEntity.FundingOutputIndex),
        nameof(ChannelEntity.FundingAmountSatoshis),
        nameof(ChannelEntity.ShortChannelId)
    ];

    /// <summary>
    /// Channel columns that belong to the commitment state once the channel has a snapshot (stored, staged in this unit
    /// of work, or held by <see cref="ChannelModel.Commitments"/>): <see cref="UpdateAsync"/> then leaves them to
    /// <see cref="ChannelStateDbRepository"/>, so a stale model can never roll a saved transition back.
    /// </summary>
    private static readonly string[] s_snapshotColumns =
    [
        nameof(ChannelEntity.LocalBalanceMsat),
        nameof(ChannelEntity.RemoteBalanceMsat),
        nameof(ChannelEntity.LocalNextHtlcId),
        nameof(ChannelEntity.RemoteNextHtlcId),
        nameof(ChannelEntity.LocalCommitmentNumber),
        nameof(ChannelEntity.RemoteCommitmentNumber),
        nameof(ChannelEntity.LocalRevocationNumber),
        nameof(ChannelEntity.RemoteRevocationNumber)
    ];

    private readonly NLightningDbContext _context;
    private readonly ISha256 _sha256;
    private readonly ChannelStateDbRepository _channelStateDbRepository;
    private readonly ILogger _logger;

    /// <summary>
    /// The node's <c>Node:MaxDustHtlcExposureMsat</c>: the limit a commitment snapshot stored without one runs under
    /// while it is loaded (NL-290), until its next save stores the value. Null keeps the check off.
    /// </summary>
    private readonly ulong? _maxDustHtlcExposureMsat;

    public ChannelDbRepository(NLightningDbContext context, ISha256 sha256, ILogger? logger = null,
                               ulong? maxDustHtlcExposureMsat = null)
        : base(context)
    {
        _context = context;
        _sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
        _logger = logger ?? NullLogger.Instance;
        _maxDustHtlcExposureMsat = maxDustHtlcExposureMsat;
        _channelStateDbRepository = new ChannelStateDbRepository(context, logger: _logger);
    }

    /// <summary>
    /// Stages a new channel. When the model already holds a commitment snapshot it is staged too
    /// (<see cref="ChannelStateDbRepository.InitializeAsync"/>).
    /// </summary>
    public async Task AddAsync(ChannelModel channelModel)
    {
        var channelEntity = MapDomainToEntity(channelModel);

        Insert(channelEntity);
        SetChangeAddressForeignKey(channelEntity, channelModel.ChangeAddress);
        await EnsureInitialFundingAsync(channelModel);

        if (channelModel.Commitments is { } commitments)
            await _channelStateDbRepository.InitializeAsync(commitments, new ChannelStateExtras
            {
                SentCommitDiff = channelModel.SentCommitDiff,
                LastSent = channelModel.LastSentCommitmentMessage
            });
    }

    /// <summary>
    /// Stages the channel row and its config, key sets and local aliases. HTLCs, fee updates, commitments and the
    /// commitment scalars are left to <see cref="ChannelStateDbRepository"/> (plan N5-T2).
    /// </summary>
    public async Task UpdateAsync(ChannelModel channelModel)
    {
        var channelEntity = MapDomainToEntity(channelModel);

        // The children are synchronized one table at a time (NL-192). Pushing the whole graph through
        // DbSet.Update marks every child Modified, so a new child fails with a concurrency exception and a removed one
        // is never deleted; on an already tracked channel only the root values used to be copied.
        var config = channelEntity.Config;
        var keySets = channelEntity.KeySets;
        var localAliases = channelEntity.LocalAliases;
        channelEntity.Config = null;
        channelEntity.KeySets = null;
        channelEntity.LocalAliases = null;

        // The model may predate the channel's first snapshot, e.g. when InitializeAsync staged it earlier in this unit
        // of work (invariant I2 swaps the in-memory snapshot only after the save)
        var keptColumns = await HasSnapshotAsync(channelModel)
                                              ? s_stateOnlyColumns.Concat(s_snapshotColumns)
                                              : s_stateOnlyColumns;
        if (await HasLockedSpliceAsync(channelModel.ChannelId))
            keptColumns = keptColumns.Concat(s_currentFundingColumns);
        UpdateExcept(channelEntity, keptColumns.ToArray());

        // Update() may have copied the values onto an already tracked instance
        var trackedEntity = DbSet.Local.FirstOrDefault(c => c.ChannelId == channelEntity.ChannelId) ?? channelEntity;
        SetChangeAddressForeignKey(trackedEntity, channelModel.ChangeAddress);

        var channelId = channelModel.ChannelId;
        await SyncChildrenAsync<ChannelConfigEntity>(c => c.ChannelId == channelId, config is null ? [] : [config]);
        await SyncChildrenAsync<ChannelKeySetEntity>(k => k.ChannelId == channelId, keySets ?? []);
        var removedAliases =
            await SyncChildrenAsync<ChannelLocalAliasEntity>(a => a.ChannelId == channelId, localAliases ?? []);

        // A tracked channel still references the removed children, and change detection must not add them back
        foreach (var alias in removedAliases)
            trackedEntity.LocalAliases?.Remove(alias);

        await EnsureInitialFundingAsync(channelModel);
    }

    public async Task<IReadOnlyCollection<(ChannelId ChannelId, ShortChannelId Alias)>> GetLocalAliasesAsync()
    {
        var aliases = await _context.ChannelLocalAliases
                                    .AsNoTracking()
                                    .Select(a => new { a.ChannelId, a.Alias })
                                    .ToListAsync();

        return aliases.Select(a => (a.ChannelId, a.Alias)).ToList();
    }

    public async Task<ChannelModel?> GetByIdAsync(ChannelId channelId)
    {
        // A channel refused for legacy HTLC rows throws here (NL-025)
        return await LoadConsistentlyAsync(channelId, _ => true);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(ChannelId channelId) => DbSet.AsNoTracking().AnyAsync(c => c.ChannelId == channelId);

    /// <inheritdoc />
    public async Task<uint> GetHighestLocalKeyIndexAsync()
    {
        var indexes = await DbSet.AsNoTracking()
                                 .SelectMany(c => c.KeySets!)
                                 .Where(k => k.IsLocal)
                                 .Select(k => k.KeyIndex)
                                 .ToListAsync();

        return indexes.Count == 0 ? 0 : indexes.Max();
    }

    public async Task<IEnumerable<ChannelModel>> GetAllAsync()
    {
        return await LoadAllConsistentlyAsync(_ => true);
    }

    public async Task<IEnumerable<ChannelModel>> GetReadyChannelsAsync()
    {
        byte[] readyStateList =
        [
            (byte)ChannelState.V1FundingCreated,
            (byte)ChannelState.V1FundingSigned,
            (byte)ChannelState.ReadyForThem,
            (byte)ChannelState.ReadyForUs,
            (byte)ChannelState.Open,
            (byte)ChannelState.ShuttingDown,
            (byte)ChannelState.Negotiating,
            (byte)ChannelState.Closing
        ];

        return await LoadAllConsistentlyAsync(c => readyStateList.Contains(c.State));
    }

    public async Task<IEnumerable<ChannelModel?>> GetByPeerIdAsync(CompactPubKey peerNodeId)
    {
        return await LoadAllConsistentlyAsync(c => c.RemoteNodeId.Equals(peerNodeId));
    }

    /// <summary>
    /// Loads a channel matching <paramref name="filter"/>: its row, current funding and commitment state, all from one
    /// snapshot of the database (NL-810). Read query by query, a save that commits in between (a splice lock moves the
    /// funding columns and the capacity) gave a torn channel, or one the engine refused as inconsistent.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel has HTLC rows in a legacy state (NL-025).</exception>
    private Task<ChannelModel?> LoadConsistentlyAsync(ChannelId channelId,
                                                      Expression<Func<ChannelEntity, bool>> filter) =>
        _context.ReadConsistentlyAsync(async () =>
        {
            var channelEntity = await DbSet
                                     .AsNoTracking()
                                     .Include(c => c.Config)
                                     .Include(c => c.KeySets)
                                     .Include(c => c.ChangeAddress)
                                     .Include(c => c.LocalAliases)
                                     .Where(filter)
                                     .FirstOrDefaultAsync(c => c.ChannelId == channelId);

            return channelEntity is null ? null : await MapWithStateAsync(channelEntity);
        });

    /// <summary>
    /// Loads every channel matching <paramref name="filter"/>, each from one snapshot of the database
    /// (<see cref="LoadConsistentlyAsync"/>): one short read per channel, so a long list never keeps a SQLite writer
    /// waiting for all of it. A channel refused for legacy HTLC rows (NL-025) is logged and left out, so it does not
    /// keep the healthy channels (and the node) from loading; <see cref="GetByIdAsync"/> still throws for it.
    /// </summary>
    private async Task<List<ChannelModel>> LoadAllConsistentlyAsync(Expression<Func<ChannelEntity, bool>> filter)
    {
        var channelIds = await DbSet.AsNoTracking().Where(filter).Select(c => c.ChannelId).ToListAsync();

        var channelModels = new List<ChannelModel>();
        foreach (var channelId in channelIds)
        {
            try
            {
                // Null when the channel left the filter (or was removed) after the list was read
                if (await LoadConsistentlyAsync(channelId, filter) is { } channelModel)
                    channelModels.Add(channelModel);
            }
            catch (LegacyHtlcStateException e)
            {
                _logger.LogError(e, "Channel {ChannelId} was not loaded", e.ChannelId);
            }
        }

        return channelModels;
    }

    /// <summary>
    /// Maps a channel and attaches its commitment snapshot, if it has one. One query at a time: the context does not
    /// allow concurrent operations. Run it inside <see cref="LoadConsistentlyAsync"/>, which reads
    /// <paramref name="channelEntity"/> from the same snapshot.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel has HTLC rows in a legacy state (NL-025).</exception>
    private async Task<ChannelModel> MapWithStateAsync(ChannelEntity channelEntity)
    {
        // After a splice the current funding's keys are the funding row's (our key rotated, splicing plan D5)
        var currentFunding = await _context.ChannelFundings.AsNoTracking()
                                           .FirstOrDefaultAsync(f => f.ChannelId == channelEntity.ChannelId
                                                                  && f.FundingTxId == channelEntity.FundingTxId);
        var channelModel = MapEntityToDomain(channelEntity, _sha256, currentFunding);

        // The snapshot runs under the dust policy it was saved with (NL-242) and keeps the inferred-limits flag of a
        // channel migrated by SplitChannelParams, so its guessed limits are still never enforced after a restart.
        // A snapshot stored without a dust limit (the option is newer than it) runs under the node's configured one,
        // so the engine's send-side rules (B2-DUST-03/04) apply to it too; its next state save stores the value
        // (NL-290)
        var @params = CommitmentParams.FromChannel(channelModel,
                                                   channelEntity.MaxDustHtlcExposureMsat ?? _maxDustHtlcExposureMsat);

        // A locked splice is the engine's current funding as stored (its kind and rotated key index), not the initial
        // funding FromChannel builds from the funding output: the next lock retires this funding with the engine's
        // copy, and a copy with key index 0 and the rotated key fails the signer's registration at the next start
        // (NL-517)
        if (currentFunding is { Kind: not (byte)ChannelFundingKind.Initial })
            @params = @params with
            {
                Funding = ChannelFundingDbRepository.MapToDomain(currentFunding) with
                {
                    Status = ChannelFundingStatus.Current
                }
            };

        var state = await _channelStateDbRepository.LoadAsync(channelModel.ChannelId, @params);
        if (state is not null)
            channelModel.UpdateCommitments(state.Commitments, new ChannelStateExtras
            {
                SentCommitDiff = state.SentCommitDiff,
                LastSent = state.LastSent
            });

        return channelModel;
    }

    /// <summary>
    /// Whether the channel has a commitment snapshot: held by the model, staged in this unit of work or stored.
    /// </summary>
    private async Task<bool> HasSnapshotAsync(ChannelModel channelModel)
    {
        if (channelModel.Commitments is not null)
            return true;

        var channelId = channelModel.ChannelId;
        if (_context.ChangeTracker.Entries<CommitmentEntity>()
                    .Any(e => e.State != EntityState.Deleted && e.Entity.ChannelId == channelId))
            return true;

        return await _context.Commitments.AsNoTracking().AnyAsync(c => c.ChannelId == channelId);
    }

    /// <summary>
    /// Whether a splice of the channel was locked (staged in this unit of work or stored): its current funding is not
    /// the initial one, or a funding was replaced. A pending or discarded splice does not count: until a lock the channel
    /// row's funding columns still follow the model (a confirmation, a reorg).
    /// </summary>
    private async Task<bool> HasLockedSpliceAsync(ChannelId channelId)
    {
        var fundings = await ChannelFundingDbRepository.GetEntitiesAsync(_context, channelId);
        return fundings.Any(f => f.Status == (byte)ChannelFundingStatus.Replaced
                              || (f.Status == (byte)ChannelFundingStatus.Current
                               && f.Kind != (byte)ChannelFundingKind.Initial));
    }

    /// <summary>
    /// Stages the channel's <see cref="ChannelFundingKind.Initial"/>, <see cref="ChannelFundingStatus.Current"/>
    /// funding row (splicing plan §3.8) once its funding outpoint is known, and keeps it in step with the channel while
    /// it is still the current funding, whatever pending or discarded splices are stored beside it (the funding keys are
    /// the key sets', as the migration's data step writes them).
    /// </summary>
    private async Task EnsureInitialFundingAsync(ChannelModel channelModel)
    {
        var fundingOutput = channelModel.FundingOutput;
        if (fundingOutput?.TransactionId is not { IsZero: false } txId || fundingOutput.Index is not { } index
                                                                       || channelModel.RemoteKeySet is null)
            return;

        var initial = new ChannelFunding(txId, index, (ulong)fundingOutput.Amount.Satoshi,
                                         channelModel.LocalKeySet.FundingCompactPubKey,
                                         channelModel.RemoteKeySet.FundingCompactPubKey, 0, 0, 0,
                                         ChannelFundingKind.Initial, ChannelFundingStatus.Current,
                                         ShortChannelId: IsSet(channelModel.ShortChannelId)
                                                             ? channelModel.ShortChannelId
                                                             : (ShortChannelId?)null);

        var fundings = await ChannelFundingDbRepository.GetEntitiesAsync(_context, channelModel.ChannelId);
        if (fundings.Count == 0)
        {
            _context.ChannelFundings.Add(ChannelFundingDbRepository.CreateEntity(channelModel.ChannelId, initial, 0));
            return;
        }

        // No splice locked yet: the initial funding follows the channel (the confirmation, a reorg)
        var row = fundings.FirstOrDefault(f => f.Kind == (byte)ChannelFundingKind.Initial
                                             && f.Status == (byte)ChannelFundingStatus.Current);
        if (row is null)
            return;

        if (row.FundingTxId == txId)
        {
            ChannelFundingDbRepository.CopyFields(initial, row);
            return;
        }

        // An RBF of a dual-funded open (before channel_ready, so before any splice) moved the channel to another
        // funding transaction, whose capacity may differ (NL-521): the initial funding row follows it. The txid is part
        // of the row's key, so the row is replaced.
        if (fundings.Count == 1 && channelModel.Commitments is null)
        {
            _context.ChannelFundings.Remove(row);
            _context.ChannelFundings.Add(
                ChannelFundingDbRepository.CreateEntity(channelModel.ChannelId, initial, row.Sequence));
        }
    }

    /// <summary>
    /// Stages the channel row like <see cref="BaseDbRepository{TEntity}.Update"/> but leaves the
    /// <paramref name="keptColumns"/> as they are (stored or already staged).
    /// </summary>
    private void UpdateExcept(ChannelEntity channelEntity, IReadOnlyCollection<string> keptColumns)
    {
        var tracked = DbSet.Local.FirstOrDefault(c => c.ChannelId == channelEntity.ChannelId);
        if (tracked is not null)
        {
            var entry = _context.Entry(tracked);
            var kept = keptColumns.ToDictionary(c => c, c => entry.Property(c).CurrentValue);
            entry.CurrentValues.SetValues(channelEntity);
            foreach (var (column, value) in kept)
                entry.Property(column).CurrentValue = value;

            return;
        }

        DbSet.Update(channelEntity);
        var newEntry = _context.Entry(channelEntity);
        foreach (var column in keptColumns)
            newEntry.Property(column).IsModified = false;
    }

    internal static ChannelEntity MapDomainToEntity(ChannelModel channelModel)
    {
        var config = ChannelConfigDbRepository.MapDomainToEntity(channelModel.ChannelId, channelModel.ChannelParams);

        // A persisted channel always carries both key sets and its funding output (the factories create them together;
        // the dual-fund placeholder is persisted only after the negotiation fills them)
        ImmutableArray<ChannelKeySetEntity> keySets =
        [
            ChannelKeySetDbRepository.MapDomainToEntity(channelModel.ChannelId, true, channelModel.LocalKeySet),
            ChannelKeySetDbRepository.MapDomainToEntity(channelModel.ChannelId, false, channelModel.RemoteKeySet!)
        ];

        List<ChannelLocalAliasEntity>? localAliasEntities = null;
        if (channelModel.LocalAliases is { Count: > 0 })
            localAliasEntities = channelModel.LocalAliases
                                             .Select(alias => new ChannelLocalAliasEntity
                                             {
                                                 Alias = alias,
                                                 ChannelId = channelModel.ChannelId
                                             })
                                             .ToList();

        return new ChannelEntity
        {
            ChannelId = channelModel.ChannelId,

            FundingCreatedAtBlockHeight = channelModel.FundingCreatedAtBlockHeight,
            FundingTxId = channelModel.FundingOutput!.TransactionId ?? new byte[CryptoConstants.Sha256HashLen],
            FundingOutputIndex = channelModel.FundingOutput.Index ?? 0,
            FundingAmountSatoshis = channelModel.FundingOutput.Amount.Satoshi,

            IsInitiator = channelModel.IsInitiator,
            RemoteNodeId = channelModel.RemoteNodeId,
            State = (byte)channelModel.State,
            Version = (byte)channelModel.Version,

            LocalBalanceMsat = checked((long)channelModel.LocalBalance.MilliSatoshi),
            RemoteBalanceMsat = checked((long)channelModel.RemoteBalance.MilliSatoshi),
            ShortChannelId = IsSet(channelModel.ShortChannelId) ? channelModel.ShortChannelId : (ShortChannelId?)null,

            LocalNextHtlcId = channelModel.LocalNextHtlcId,
            RemoteNextHtlcId = channelModel.RemoteNextHtlcId,
            LocalRevocationNumber = channelModel.LocalRevocationNumber,
            RemoteRevocationNumber = channelModel.RemoteRevocationNumber,
            LocalCommitmentNumber = channelModel.LocalCommitmentNumber,
            RemoteCommitmentNumber = channelModel.RemoteCommitmentNumber,
            LastSentSignature = channelModel.LastSentSignature?.Value ?? null,
            LastReceivedSignature = channelModel.LastReceivedSignature?.Value ?? null,
            LastReceivedPartialSignature = channelModel.LastReceivedPartialSignature?.ToBytes(),

            RemoteAlias = channelModel.RemoteAlias,

            RemoteNextPerCommitmentPoint = channelModel.Commitments?.RemoteNextPerCommitmentPoint,
            RemoteNextNonces = channelModel.Commitments is { } commitments
                                   ? CommitmentStateEncoding.EncodeRemoteNonces(commitments.RemoteNextNonces)
                                   : null,
            SentCommitDiff = channelModel.SentCommitDiff?.ToArray(),
            LastSentOrder = (byte)channelModel.LastSentCommitmentMessage,
            ErrorSent = channelModel.ErrorSent?.ToArray(),
            DataLossDetected = channelModel.DataLossDetected,

            LocalShutdownScript = channelModel.LocalShutdownScript is { } localScript ? (byte[])localScript : null,
            RemoteShutdownScript = channelModel.RemoteShutdownScript is { } remoteScript ? (byte[])remoteScript : null,
            FirstRemoteHtlcIdAfterLocalShutdown = channelModel.FirstRemoteHtlcIdAfterLocalShutdown,
            ClosingTxId = channelModel.ClosingTransaction?.TxId,
            ClosingTransaction = channelModel.ClosingTransaction?.RawTxBytes,
            CloseProtocol = (byte?)channelModel.CloseProtocol,
            LocalIsCloser = channelModel.LocalIsCloser,

            RemoteAnnouncementNodeSig = channelModel.RemoteAnnouncementSignatures?.NodeSignature.Value,
            RemoteAnnouncementBitcoinSig = channelModel.RemoteAnnouncementSignatures?.BitcoinSignature.Value,
            LocalAnnouncementSigsSentAt = channelModel.LocalAnnouncementSignaturesSentAt,

            Label = channelModel.Label,
            Tags = channelModel.Tags,

            Config = config,
            KeySets = keySets,
            LocalAliases = localAliasEntities
        };
    }

    /// <summary>
    /// Maps the channel row and its config, key sets and aliases. The commitment state (HTLCs, fee updates,
    /// commitments) is attached separately from <see cref="ChannelStateDbRepository"/>; the legacy HTLC collections of
    /// <see cref="ChannelModel"/> are no longer persisted and stay empty.
    /// </summary>
    internal static ChannelModel MapEntityToDomain(ChannelEntity channelEntity, ISha256 sha256,
                                                   ChannelFundingEntity? currentFunding = null)
    {
        if (channelEntity.Config is null)
            throw new InvalidOperationException(
                "Channel config cannot be null when mapping channel entity to domain model.");

        if (channelEntity.KeySets is not { Count: 2 })
            throw new InvalidOperationException(
                "Channel key sets must contain exactly two entries when mapping channel entity to domain model.");

        var localKeySetEntity = channelEntity.KeySets.FirstOrDefault(k => k.IsLocal) ??
                                throw new InvalidOperationException(
                                    "Local key set cannot be null when mapping channel entity to domain model.");
        var remoteKeySetEntity = channelEntity.KeySets.FirstOrDefault(k => !k.IsLocal) ??
                                 throw new InvalidOperationException(
                                     "Remote key set cannot be null when mapping channel entity to domain model.");
        var config = ChannelConfigDbRepository.MapEntityToDomain(channelEntity.Config);
        var localKeySet = ChannelKeySetDbRepository.MapEntityToDomain(localKeySetEntity);
        var remoteKeySet = ChannelKeySetDbRepository.MapEntityToDomain(remoteKeySetEntity);

        // After a splice the current funding's keys are its row's (our key rotates per splice, D5); the initial
        // funding's are the key sets'
        var splicedFunding = currentFunding is { Kind: not (byte)ChannelFundingKind.Initial } ? currentFunding : null;
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(channelEntity.FundingAmountSatoshis),
                                                  splicedFunding?.LocalFundingPubKey ?? localKeySet.FundingCompactPubKey,
                                                  splicedFunding?.RemoteFundingPubKey
                                               ?? remoteKeySet.FundingCompactPubKey)
        {
            Index = channelEntity.FundingOutputIndex,
            TransactionId = channelEntity.FundingTxId
        };

        // BOLT 3: the obscuring factor is SHA256(opener payment_basepoint || accepter payment_basepoint)
        var (openerPaymentBasepoint, accepterPaymentBasepoint) = channelEntity.IsInitiator
            ? (localKeySet.PaymentCompactBasepoint, remoteKeySet.PaymentCompactBasepoint)
            : (remoteKeySet.PaymentCompactBasepoint, localKeySet.PaymentCompactBasepoint);
        var commitmentNumber = new CommitmentNumber(openerPaymentBasepoint, accepterPaymentBasepoint, sha256);

        var remoteNodeId = channelEntity.RemoteNodeId;

        CompactSignature? lastSentSig = null;
        if (channelEntity.LastSentSignature != null)
            lastSentSig = new CompactSignature(channelEntity.LastSentSignature);

        CompactSignature? lastReceivedSig = null;
        if (channelEntity.LastReceivedSignature != null)
            lastReceivedSig = new CompactSignature(channelEntity.LastReceivedSignature);

        var channelModel = new ChannelModel(config, channelEntity.ChannelId, commitmentNumber, fundingOutput,
                                channelEntity.IsInitiator, lastSentSig, lastReceivedSig,
                                LightningMoney.MilliSatoshis((ulong)channelEntity.LocalBalanceMsat), localKeySet,
                                channelEntity.LocalNextHtlcId, channelEntity.LocalRevocationNumber,
                                LightningMoney.MilliSatoshis((ulong)channelEntity.RemoteBalanceMsat), remoteKeySet,
                                channelEntity.RemoteNextHtlcId, remoteNodeId, channelEntity.RemoteRevocationNumber,
                                (ChannelState)channelEntity.State, (ChannelVersion)channelEntity.Version,
                                localCommitmentNumber: channelEntity.LocalCommitmentNumber,
                                remoteCommitmentNumber: channelEntity.RemoteCommitmentNumber)
        {
            FundingCreatedAtBlockHeight = channelEntity.FundingCreatedAtBlockHeight,
            LocalAliases = channelEntity.LocalAliases is { Count: > 0 }
                               ? channelEntity.LocalAliases.Select(a => a.Alias).ToList()
                               : null,
            RemoteAlias = channelEntity.RemoteAlias,
            ShortChannelId = channelEntity.ShortChannelId ?? default,
            ChangeAddress = channelEntity.ChangeAddress is null
                                ? null
                                : WalletAddressesDbRepository.MapEntityToModel(channelEntity.ChangeAddress),
            Label = channelEntity.Label,
            Tags = channelEntity.Tags
        };
        // The current funding's key index (NL-495): the signer's view of a spliced channel uses its rotated key
        channelModel.SetLocalFundingKeyIndex(splicedFunding?.LocalFundingKeyIndex ?? 0);
        channelModel.FundingKeysUnknown = currentFunding?.FundingKeysUnknown ?? false;
        if (channelEntity.LastReceivedPartialSignature is { } partialSignature)
            channelModel.UpdateLastReceivedPartialSignature(new MusigPartialSignatureWithNonce(partialSignature));
        if (channelEntity.ErrorSent is not null)
            channelModel.MarkErrorSent(channelEntity.ErrorSent);
        if (channelEntity.DataLossDetected)
            channelModel.MarkDataLossDetected();
        if (channelEntity.LocalShutdownScript is not null)
            channelModel.SetLocalShutdownScript(channelEntity.LocalShutdownScript);
        if (channelEntity.RemoteShutdownScript is not null)
            channelModel.SetRemoteShutdownScript(channelEntity.RemoteShutdownScript);
        if (channelEntity.FirstRemoteHtlcIdAfterLocalShutdown is { } firstAfterShutdown)
            channelModel.SetFirstRemoteHtlcIdAfterLocalShutdown(firstAfterShutdown);
        if (channelEntity is { ClosingTxId: { } closingTxId, ClosingTransaction: { Length: > 0 } closingTx })
            channelModel.SetClosingTransaction(new SignedTransaction(closingTxId, closingTx));
        if (channelEntity.CloseProtocol is { } closeProtocol)
            channelModel.SetCloseTerms((MutualCloseProtocol)closeProtocol, channelEntity.LocalIsCloser);
        if (channelEntity is { RemoteAnnouncementNodeSig: { } nodeSig, RemoteAnnouncementBitcoinSig: { } bitcoinSig })
            channelModel.SetRemoteAnnouncementSignatures(
                new ChannelAnnouncementSignatures(new CompactSignature(nodeSig), new CompactSignature(bitcoinSig)));
        if (channelEntity.LocalAnnouncementSigsSentAt is { } sentAt)
            channelModel.MarkAnnouncementSignaturesSent(sentAt);

        return channelModel;
    }

    /// <summary>
    /// Sets the (Index, IsChange, AddressType) foreign key legs of the funding change address (NL-134: real columns
    /// on <see cref="ChannelEntity"/>, no longer shadow properties set through the change tracker).
    /// </summary>
    private static void SetChangeAddressForeignKey(ChannelEntity channelEntity, WalletAddressModel? changeAddress)
    {
        channelEntity.ChangeAddressIndex = changeAddress?.Index;
        channelEntity.ChangeAddressIsChange = changeAddress?.IsChange;
        channelEntity.ChangeAddressAddressType = changeAddress?.AddressType;
    }

    /// <summary>
    /// Makes the tracked rows of one child table of the channel match <paramref name="desiredChildren"/>, matching rows
    /// by primary key: missing rows are added, existing rows get the new values and rows that are no longer wanted are
    /// removed. Rows already tracked by this context (including ones added but not saved yet) are taken into account.
    /// </summary>
    /// <returns>The removed children.</returns>
    private async Task<List<TChild>> SyncChildrenAsync<TChild>(Expression<Func<TChild, bool>> belongsToChannel,
                                                               ICollection<TChild> desiredChildren)
        where TChild : class
    {
        var set = _context.Set<TChild>();

        // A tracking query returns the already tracked instance for rows this context knows about
        var currentChildren = await set.Where(belongsToChannel).ToListAsync();
        var isChildOfChannel = belongsToChannel.Compile();
        var knownChildren = new HashSet<TChild>(currentChildren, ReferenceEqualityComparer.Instance);
        currentChildren.AddRange(set.Local.Where(isChildOfChannel).Where(knownChildren.Add).ToList());

        var primaryKey = _context.Model.FindEntityType(typeof(TChild))?.FindPrimaryKey()
                      ?? throw new InvalidOperationException($"Entity {typeof(TChild).Name} has no primary key.");
        var currentByKey = new Dictionary<object, TChild>(s_keyComparer);
        foreach (var child in currentChildren)
            currentByKey[GetKey(child)] = child;

        foreach (var desiredChild in desiredChildren)
        {
            if (currentByKey.Remove(GetKey(desiredChild), out var currentChild))
            {
                var entry = _context.Entry(currentChild);
                entry.CurrentValues.SetValues(desiredChild);
                if (entry.State == EntityState.Deleted)
                    entry.State = EntityState.Modified;
            }
            else
            {
                set.Add(desiredChild);
            }
        }

        var removedChildren = currentByKey.Values.ToList();
        foreach (var removedChild in removedChildren)
            set.Remove(removedChild);

        return removedChildren;

        object GetKey(TChild child)
        {
            var entry = _context.Entry(child);
            return primaryKey.Properties.Select(p => entry.Property(p.Name).CurrentValue).ToArray();
        }
    }

    /// <summary>
    /// A channel that is not confirmed yet has a default <see cref="ShortChannelId"/>, which has no bytes.
    /// </summary>
    private static bool IsSet(ShortChannelId shortChannelId) => ((byte[]?)shortChannelId) is not null;
}
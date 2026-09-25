using System.Collections;
using System.Collections.Immutable;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NLightning.Domain.Protocol.Models;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Bitcoin;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Serialization.Interfaces;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;
using Persistence.Entities.Channel;

public class ChannelDbRepository : BaseDbRepository<ChannelEntity>, IChannelDbRepository
{
    /// <summary>
    /// Compares primary keys given as arrays of key values, element by element.
    /// </summary>
    private static readonly IEqualityComparer<object> s_keyComparer =
        EqualityComparer<object>.Create((x, y) => StructuralComparisons.StructuralEqualityComparer.Equals(x, y),
                                        x => StructuralComparisons.StructuralEqualityComparer.GetHashCode(x));

    private readonly NLightningDbContext _context;
    private readonly IMessageSerializer _messageSerializer;
    private readonly ISha256 _sha256;

    public ChannelDbRepository(NLightningDbContext context, IMessageSerializer messageSerializer, ISha256 sha256)
        : base(context)
    {
        _context = context;
        _messageSerializer = messageSerializer ?? throw new ArgumentNullException(nameof(messageSerializer));
        _sha256 = sha256 ?? throw new ArgumentNullException(nameof(sha256));
    }

    public async Task AddAsync(ChannelModel channelModel)
    {
        var channelEntity = await MapDomainToEntity(channelModel, _messageSerializer);

        Insert(channelEntity);
        SetChangeAddressForeignKey(channelEntity, channelModel.ChangeAddress);
    }

    public async Task UpdateAsync(ChannelModel channelModel)
    {
        var channelEntity = await MapDomainToEntity(channelModel, _messageSerializer);

        // The children are synchronized one table at a time (NL-192). Pushing the whole graph through
        // DbSet.Update marks every child Modified, so a new HTLC fails with a concurrency exception and a removed one
        // is never deleted; on an already tracked channel only the root values used to be copied.
        var config = channelEntity.Config;
        var keySets = channelEntity.KeySets;
        var htlcs = channelEntity.Htlcs;
        channelEntity.Config = null;
        channelEntity.KeySets = null;
        channelEntity.Htlcs = null;

        Update(channelEntity);

        // Update() may have copied the values onto an already tracked instance
        var trackedEntity = DbSet.Local.FirstOrDefault(c => c.ChannelId == channelEntity.ChannelId) ?? channelEntity;
        SetChangeAddressForeignKey(trackedEntity, channelModel.ChangeAddress);

        var channelId = channelModel.ChannelId;
        await SyncChildrenAsync<ChannelConfigEntity>(c => c.ChannelId == channelId, config is null ? [] : [config]);
        await SyncChildrenAsync<ChannelKeySetEntity>(k => k.ChannelId == channelId, keySets ?? []);
        var removedHtlcs = await SyncChildrenAsync<HtlcEntity>(h => h.ChannelId == channelId, htlcs ?? []);

        // A tracked channel still references the removed children, and change detection must not add them back
        foreach (var htlc in removedHtlcs)
            trackedEntity.Htlcs?.Remove(htlc);
    }

    public async Task<ChannelModel?> GetByIdAsync(ChannelId channelId)
    {
        var channelEntity = await DbSet
                                 .AsNoTracking()
                                 .Include(c => c.Config)
                                 .Include(c => c.KeySets)
                                 .Include(c => c.Htlcs)
                                 .Include(c => c.ChangeAddress)
                                 .FirstOrDefaultAsync(c => c.ChannelId == channelId);

        if (channelEntity is null)
            return null;

        return await MapEntityToDomain(channelEntity, _messageSerializer, _sha256);
    }

    public async Task<IEnumerable<ChannelModel>> GetAllAsync()
    {
        var channelEntities = await DbSet
                                   .AsNoTracking()
                                   .Include(c => c.Config)
                                   .Include(c => c.KeySets)
                                   .Include(c => c.Htlcs)
                                   .Include(c => c.ChangeAddress)
                                   .ToListAsync();

        return await Task.WhenAll(
                   channelEntities.Select(async entity =>
                                              await MapEntityToDomain(entity, _messageSerializer, _sha256)));
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
            (byte)ChannelState.Closing
        ];

        var channelEntities = await DbSet
                                   .AsNoTracking()
                                   .Include(c => c.Config)
                                   .Include(c => c.KeySets)
                                   .Include(c => c.Htlcs)
                                   .Include(c => c.ChangeAddress)
                                   .Where(c => readyStateList.Contains(c.State))
                                   .ToListAsync();

        return await Task.WhenAll(
                   channelEntities.Select(async entity =>
                                              await MapEntityToDomain(entity, _messageSerializer, _sha256)));
    }

    public async Task<IEnumerable<ChannelModel?>> GetByPeerIdAsync(CompactPubKey peerNodeId)
    {
        var channelEntities = await DbSet
                                   .AsNoTracking()
                                   .Include(c => c.Config)
                                   .Include(c => c.KeySets)
                                   .Include(c => c.Htlcs)
                                   .Include(c => c.ChangeAddress)
                                   .Where(c => c.RemoteNodeId.Equals(peerNodeId))
                                   .ToListAsync();

        return await Task.WhenAll(
                   channelEntities.Select(async entity =>
                                              await MapEntityToDomain(entity, _messageSerializer, _sha256)));
    }

    internal static async Task<ChannelEntity> MapDomainToEntity(ChannelModel channelModel,
                                                                IMessageSerializer messageSerializer)
    {
        var config = ChannelConfigDbRepository.MapDomainToEntity(channelModel.ChannelId, channelModel.ChannelConfig);
        ImmutableArray<ChannelKeySetEntity> keySets =
        [
            ChannelKeySetDbRepository.MapDomainToEntity(channelModel.ChannelId, true, channelModel.LocalKeySet),
            ChannelKeySetDbRepository.MapDomainToEntity(channelModel.ChannelId, false, channelModel.RemoteKeySet)
        ];

        var htlcs = new List<Htlc>();
        htlcs.AddRange(GetHtlcsOrNull(channelModel.LocalOfferedHtlcs));
        htlcs.AddRange(GetHtlcsOrNull(channelModel.LocalFulfilledHtlcs));
        htlcs.AddRange(GetHtlcsOrNull(channelModel.LocalOldHtlcs));
        htlcs.AddRange(GetHtlcsOrNull(channelModel.RemoteOfferedHtlcs));
        htlcs.AddRange(GetHtlcsOrNull(channelModel.RemoteFulfilledHtlcs));
        htlcs.AddRange(GetHtlcsOrNull(channelModel.RemoteOldHtlcs));

        List<HtlcEntity>? htlcEntities = null;
        if (htlcs.Count > 0)
        {
            htlcEntities = [];

            foreach (var htlc in htlcs)
                htlcEntities.Add(
                    await HtlcDbRepository.MapDomainToEntityAsync(channelModel.ChannelId, htlc, messageSerializer));
        }

        return new ChannelEntity
        {
            ChannelId = channelModel.ChannelId,

            FundingCreatedAtBlockHeight = channelModel.FundingCreatedAtBlockHeight,
            FundingTxId = channelModel.FundingOutput.TransactionId ?? new byte[CryptoConstants.Sha256HashLen],
            FundingOutputIndex = channelModel.FundingOutput.Index ?? 0,
            FundingAmountSatoshis = channelModel.FundingOutput.Amount.Satoshi,

            IsInitiator = channelModel.IsInitiator,
            RemoteNodeId = channelModel.RemoteNodeId,
            State = (byte)channelModel.State,
            Version = (byte)channelModel.Version,

            LocalBalanceSatoshis = channelModel.LocalBalance.Satoshi,
            RemoteBalanceSatoshis = channelModel.RemoteBalance.Satoshi,

            ChangeAddressType = channelModel.ChangeAddress?.AddressType,
            ChangeAddressIndex = channelModel.ChangeAddress?.Index,

            LocalNextHtlcId = channelModel.LocalNextHtlcId,
            RemoteNextHtlcId = channelModel.RemoteNextHtlcId,
            LocalRevocationNumber = channelModel.LocalRevocationNumber,
            RemoteRevocationNumber = channelModel.RemoteRevocationNumber,
            LastSentSignature = channelModel.LastSentSignature?.Value ?? null,
            LastReceivedSignature = channelModel.LastReceivedSignature?.Value ?? null,

            Config = config,
            KeySets = keySets,
            Htlcs = htlcEntities
        };
    }

    internal static async Task<ChannelModel> MapEntityToDomain(ChannelEntity channelEntity,
                                                               IMessageSerializer messageSerializer, ISha256 sha256)
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

        var localOfferedHtlcs = new List<Htlc>();
        var localFulfilledHtlcs = new List<Htlc>();
        var localOldHtlcs = new List<Htlc>();
        var remoteOfferedHtlcs = new List<Htlc>();
        var remoteFulfilledHtlcs = new List<Htlc>();
        var remoteOldHtlcs = new List<Htlc>();
        if (channelEntity.Htlcs is { Count: > 0 })
        {
            foreach (var htlc in channelEntity.Htlcs.Where(h => h.State == (byte)HtlcState.Offered))
            {
                var domainHtlc = await HtlcDbRepository.MapEntityToDomainAsync(htlc, messageSerializer);
                if (htlc.Direction == (byte)HtlcDirection.Outgoing)
                    localOfferedHtlcs.Add(domainHtlc);
                else
                    remoteOfferedHtlcs.Add(domainHtlc);
            }

            foreach (var htlc in channelEntity.Htlcs.Where(h => h.State == (byte)HtlcState.Fulfilled))
            {
                var domainHtlc = await HtlcDbRepository.MapEntityToDomainAsync(htlc, messageSerializer);
                if (htlc.Direction == (byte)HtlcDirection.Outgoing)
                    localFulfilledHtlcs.Add(domainHtlc);
                else
                    remoteFulfilledHtlcs.Add(domainHtlc);
            }

            byte[] oldStates = [(byte)HtlcState.Expired, (byte)HtlcState.Failed];
            foreach (var htlc in channelEntity.Htlcs.Where(h => oldStates.Contains(h.State)))
            {
                var domainHtlc = await HtlcDbRepository.MapEntityToDomainAsync(htlc, messageSerializer);
                if (htlc.Direction == (byte)HtlcDirection.Outgoing)
                    localOldHtlcs.Add(domainHtlc);
                else
                    remoteOldHtlcs.Add(domainHtlc);
            }
        }

        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(channelEntity.FundingAmountSatoshis),
                                                  localKeySet.FundingCompactPubKey, remoteKeySet.FundingCompactPubKey)
        {
            Index = channelEntity.FundingOutputIndex,
            TransactionId = channelEntity.FundingTxId
        };

        // BOLT 3: the obscuring factor is SHA256(opener payment_basepoint || accepter payment_basepoint)
        var (openerPaymentBasepoint, accepterPaymentBasepoint) = channelEntity.IsInitiator
            ? (localKeySet.PaymentCompactBasepoint, remoteKeySet.PaymentCompactBasepoint)
            : (remoteKeySet.PaymentCompactBasepoint, localKeySet.PaymentCompactBasepoint);
        var commitmentNumber = new CommitmentNumber(openerPaymentBasepoint, accepterPaymentBasepoint, sha256,
                                                    channelEntity.LocalRevocationNumber + 1);

        var remoteNodeId = channelEntity.RemoteNodeId;

        CompactSignature? lastSentSig = null;
        if (channelEntity.LastSentSignature != null)
            lastSentSig = new CompactSignature(channelEntity.LastSentSignature);

        CompactSignature? lastReceivedSig = null;
        if (channelEntity.LastReceivedSignature != null)
            lastReceivedSig = new CompactSignature(channelEntity.LastReceivedSignature);

        return new ChannelModel(config, channelEntity.ChannelId, commitmentNumber, fundingOutput,
                                channelEntity.IsInitiator, lastSentSig, lastReceivedSig,
                                LightningMoney.Satoshis(channelEntity.LocalBalanceSatoshis), localKeySet,
                                channelEntity.LocalNextHtlcId, channelEntity.LocalRevocationNumber,
                                LightningMoney.Satoshis(channelEntity.RemoteBalanceSatoshis), remoteKeySet,
                                channelEntity.RemoteNextHtlcId, remoteNodeId, channelEntity.RemoteRevocationNumber,
                                (ChannelState)channelEntity.State, (ChannelVersion)channelEntity.Version,
                                localOfferedHtlcs, localFulfilledHtlcs, localOldHtlcs, null, remoteOfferedHtlcs,
                                remoteFulfilledHtlcs, remoteOldHtlcs)
        {
            FundingCreatedAtBlockHeight = channelEntity.FundingCreatedAtBlockHeight,
            ChangeAddress = channelEntity.ChangeAddress is null
                                ? null
                                : WalletAddressesDbRepository.MapEntityToModel(channelEntity.ChangeAddress)
        };
    }

    /// <summary>
    /// The change address relationship is keyed by (Index, IsChange, AddressType), and part of that foreign key only
    /// exists as EF shadow properties on <see cref="ChannelEntity"/>, so it has to be set through the change tracker.
    /// </summary>
    private void SetChangeAddressForeignKey(ChannelEntity channelEntity, WalletAddressModel? changeAddress)
    {
        var entry = _context.Entry(channelEntity);
        var foreignKey = ((INavigation)entry.Navigation(nameof(ChannelEntity.ChangeAddress)).Metadata).ForeignKey;

        for (var i = 0; i < foreignKey.Properties.Count; i++)
        {
            object? value = changeAddress is null
                                ? null
                                : foreignKey.PrincipalKey.Properties[i].Name switch
                                {
                                    nameof(WalletAddressEntity.Index) => changeAddress.Index,
                                    nameof(WalletAddressEntity.IsChange) => changeAddress.IsChange,
                                    nameof(WalletAddressEntity.AddressType) => changeAddress.AddressType,
                                    var name => throw new InvalidOperationException(
                                                    $"Unexpected change address key property {name}.")
                                };

            entry.Property(foreignKey.Properties[i].Name).CurrentValue = value;
        }
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

    private static ICollection<Htlc> GetHtlcsOrNull(ICollection<Htlc>? htlcs)
    {
        return htlcs is { Count: > 0 } ? htlcs : [];
    }
}
using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Bitcoin;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

public class UtxoDbRepository(NLightningDbContext context)
    : BaseDbRepository<UtxoEntity>(context), IUtxoDbRepository
{
    public void Add(UtxoModel utxoModel)
    {
        var utxoEntity = MapDomainToEntity(utxoModel);
        Insert(utxoEntity);
    }

    public void Spend(UtxoModel utxoModel)
    {
        // If the utxo was added in this same unit of work and not saved yet, just cancel the pending insert
        var trackedEntity = DbSet.Local.FirstOrDefault(e => e.TransactionId.Equals(utxoModel.TxId)
                                                         && e.Index == utxoModel.Index);
        if (trackedEntity is not null)
        {
            var entry = DbSet.Entry(trackedEntity);
            if (entry.State == EntityState.Added)
                entry.State = EntityState.Detached;
            else
                DbSet.Remove(trackedEntity);

            return;
        }

        var utxoEntity = MapDomainToEntity(utxoModel);
        Delete(utxoEntity);
    }

    public void Update(UtxoModel utxoModel)
    {
        var utxoEntity = MapDomainToEntity(utxoModel);
        Update(utxoEntity);
    }

    public async Task<IEnumerable<UtxoModel>> GetUnspentAsync(bool includeWalletAddress = false)
    {
        var query = Get(asNoTracking: true).Include(x => x.SilentPayment).AsQueryable();
        if (includeWalletAddress)
            query = query.Include(x => x.WalletAddress);

        var utxoSet = await query.ToListAsync();

        return utxoSet.Select(MapEntityToModel);
    }

    public async Task<UtxoModel?> GetByIdAsync(TxId txId, uint index, bool includeWalletAddress = false)
    {
        var query = DbSet.AsNoTracking().Include(x => x.SilentPayment).AsQueryable();
        if (includeWalletAddress)
            query = query.Include(x => x.WalletAddress);
        var utxoEntity = await query.SingleOrDefaultAsync(x => x.TransactionId == txId && x.Index == index);
        return utxoEntity is null
                   ? null
                   : MapEntityToModel(utxoEntity);
    }

    private UtxoEntity MapDomainToEntity(UtxoModel model)
    {
        return new UtxoEntity
        {
            TransactionId = model.TxId,
            Index = model.Index,
            AmountSats = model.Amount.Satoshi,
            BlockHeight = model.BlockHeight,
            AddressIndex = model.SilentPayment is null ? model.AddressIndex : null,
            IsAddressChange = model.SilentPayment is null ? model.IsAddressChange : null,
            AddressType = model.SilentPayment is null ? model.AddressType : null,
            SilentPaymentTransactionId = model.SilentPayment?.TransactionId,
            SilentPaymentIndex = model.SilentPayment?.Index,
            LockedToChannelId = model.LockedToChannelId,
            UsedInTransactionId = model.UsedInTransactionId
        };
    }

    private UtxoModel MapEntityToModel(UtxoEntity entity)
    {
        var utxoModel = entity.SilentPayment is not null
            ? new UtxoModel(SilentPaymentDbRepository.MapEntityToModel(entity.SilentPayment))
            : new UtxoModel(entity.TransactionId, entity.Index, LightningMoney.Satoshis(entity.AmountSats),
                                      entity.BlockHeight, entity.AddressIndex!.Value, entity.IsAddressChange!.Value,
                                      entity.AddressType!.Value);
        utxoModel.LockedToChannelId = entity.LockedToChannelId;
        utxoModel.UsedInTransactionId = entity.UsedInTransactionId;

        if (entity.WalletAddress is null)
            return utxoModel;

        var walletAddressModel = WalletAddressesDbRepository.MapEntityToModel(entity.WalletAddress);
        utxoModel.SetWalletAddress(walletAddressModel);

        return utxoModel;
    }
}
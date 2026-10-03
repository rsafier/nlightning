using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Bitcoin;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

public class WalletAddressesDbRepository(NLightningDbContext context)
    : BaseDbRepository<WalletAddressEntity>(context), IWalletAddressesDbRepository
{
    public async Task<WalletAddressModel?> GetUnusedAddressAsync(AddressType type, bool isChange)
    {
        // NL-280: never go back below an address that was handed out (reserved) or received funds. Spending deletes the
        // UTXO row, so an address handed out before reservations existed and since spent is skipped as long as a later
        // address was used.
        var highestUsedEntity = await DbSet.AsNoTracking()
                                           .Where(x => x.AddressType.Equals(type)
                                                    && x.IsChange.Equals(isChange)
                                                    && (x.IsReserved || (x.Utxos != null && x.Utxos.Any())))
                                           .OrderByDescending(x => x.Index)
                                           .FirstOrDefaultAsync();
        var query = DbSet.AsNoTracking()
                         .Include(x => x.Utxos)
                         .Where(x => x.AddressType.Equals(type)
                                  && x.IsChange.Equals(isChange)
                                  && !x.IsReserved)
                         .Where(x => x.Utxos != null
                                  && x.Utxos.Count().Equals(0));
        if (highestUsedEntity is not null)
        {
            var highestUsed = highestUsedEntity.Index;
            query = query.Where(x => x.Index > highestUsed);
        }

        var walletAddressEntity = await query.OrderBy(x => x.Index).FirstOrDefaultAsync();

        return walletAddressEntity is null ? null : MapEntityToModel(walletAddressEntity);
    }

    public async Task ReserveAsync(WalletAddressModel address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var entity = await DbSet.FirstOrDefaultAsync(x => x.Index == address.Index
                                                       && x.IsChange == address.IsChange
                                                       && x.AddressType == address.AddressType)
                  ?? throw new InvalidOperationException($"Wallet address {address.Address} is not stored");
        entity.IsReserved = true;
    }

    public async Task<uint> GetLastUsedAddressIndex(AddressType addressType, bool isChange)
    {
        var walletAddressEntity = await DbSet.AsNoTracking()
                                             .Where(x => x.AddressType.Equals(addressType)
                                                      && x.IsChange.Equals(isChange))
                                             .OrderByDescending(x => x.Index)
                                             .FirstOrDefaultAsync();

        return walletAddressEntity?.Index ?? 0;
    }

    public void AddRange(List<WalletAddressModel> addresses)
    {
        var walletAddressEntities = addresses.Select(MapDomainToEntity);
        DbSet.AddRange(walletAddressEntities);
    }

    public void UpdateAsync(WalletAddressModel address)
    {
        var walletAddressEntity = MapDomainToEntity(address);
        Update(walletAddressEntity);
    }

    public IEnumerable<WalletAddressModel> GetAllAddresses()
    {
        return DbSet.AsNoTracking().AsEnumerable().Select(MapEntityToModel);
    }

    private static WalletAddressEntity MapDomainToEntity(WalletAddressModel model)
    {
        return new WalletAddressEntity
        {
            Index = model.Index,
            IsChange = model.IsChange,
            AddressType = model.AddressType,
            Address = model.Address,
            IsReserved = model.IsReserved
        };
    }

    internal static WalletAddressModel MapEntityToModel(WalletAddressEntity entity)
    {
        return new WalletAddressModel(entity.AddressType, entity.Index, entity.IsChange, entity.Address)
        {
            IsReserved = entity.IsReserved
        };
    }
}
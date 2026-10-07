using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Bitcoin;

using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

public sealed class WalletAccountDbRepository(NLightningDbContext context) : IWalletAccountDbRepository
{
    public async Task<IReadOnlyList<WalletAccountModel>> ListAsync(CancellationToken ct = default) =>
        (await context.WalletAccounts.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)).Select(Map).ToArray();

    public async Task<WalletAccountModel?> GetAsync(string name, CancellationToken ct = default) =>
        await context.WalletAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Name == name, ct) is { } row
            ? Map(row) : null;

    public async Task StageAsync(WalletAccountModel account, CancellationToken ct = default)
    {
        var row = await context.WalletAccounts.SingleOrDefaultAsync(x => x.Name == account.Name, ct);
        if (row is null)
        {
            row = new WalletAccountEntity { Name = account.Name };
            context.WalletAccounts.Add(row);
        }
        else if (row.AddressType != account.AddressType || row.AccountIndex != account.AccountIndex
              || row.ExtendedPublicKey != account.ExtendedPublicKey || row.WatchOnly != account.WatchOnly)
            throw new InvalidOperationException("An account's key scope and ownership are immutable.");
        row.AddressType = account.AddressType;
        row.AccountIndex = account.AccountIndex;
        row.ExtendedPublicKey = account.ExtendedPublicKey;
        row.MasterFingerprint = account.MasterFingerprint.ToArray();
        row.DerivationPath = account.DerivationPath;
        row.WatchOnly = account.WatchOnly;
        row.BirthdayHeight = account.BirthdayHeight;
        row.ExternalKeyCount = account.ExternalKeyCount;
        row.InternalKeyCount = account.InternalKeyCount;
    }

    private static WalletAccountModel Map(WalletAccountEntity row) =>
        new(row.Name, row.AddressType, row.AccountIndex, row.ExtendedPublicKey,
            row.MasterFingerprint.ToArray(), row.DerivationPath, row.WatchOnly,
            row.BirthdayHeight, row.ExternalKeyCount, row.InternalKeyCount);
}
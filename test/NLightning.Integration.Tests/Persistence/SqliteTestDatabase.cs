using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

/// <summary>
/// An in-memory Sqlite database with the real Sqlite migrations applied.
/// The connection stays open for the lifetime of this object, so several contexts can share the same database.
/// </summary>
internal sealed class SqliteTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteTestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var context = CreateContext();
        context.Database.Migrate();
    }

    public NLightningDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(_connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;

        return new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite));
    }

    public static WalletAddressModel CreateWalletAddress(uint index = 0) =>
        new(AddressType.P2Wpkh, index, false, $"bcrt1qtestaddress{index}");

    public static UtxoModel CreateUtxo(WalletAddressModel walletAddress, byte txIdSeed = 1, uint index = 0,
                                       long amountSats = 100_000)
    {
        var txIdBytes = new byte[32];
        Array.Fill(txIdBytes, txIdSeed);
        return new UtxoModel(new TxId(txIdBytes), index, LightningMoney.Satoshis(amountSats), 100, walletAddress);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Entities.Accounting;
using Infrastructure.Persistence.Entities.Bitcoin;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Payment;

internal static class DanglingSchemaRoundTrip
{
    public static async Task AssertAsync(Func<NLightningDbContext> factory, Func<NLightningDbContext> compiledFactory,
        DatabaseType provider, bool upgrade, CancellationToken ct)
    {
        var sql = new MigrationSqlDialect(provider);
        var channel = new ChannelId(Enumerable.Repeat((byte)17, 32).ToArray());
        var secret = new Secret(Enumerable.Repeat((byte)23, 32).ToArray());
        var hash = new Hash(SHA256.HashData((byte[])secret));
        var txId = new TxId(Enumerable.Repeat((byte)29, 32).ToArray());
        var at = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        string Blob(byte[] bytes) => provider == DatabaseType.PostgreSql
            ? $"decode('{Convert.ToHexString(bytes)}', 'hex')" : $"X'{Convert.ToHexString(bytes)}'";
        await using (var context = factory())
        {
            var migrator = context.GetService<IMigrator>();
            if (upgrade)
            {
                var migrations = context.Database.GetMigrations().ToList();
                var latest = migrations.Single(migration => migration.EndsWith("_AddDanglingWalletHistoryAccountsAndPriceAudits", StringComparison.Ordinal));
                await migrator.MigrateAsync(migrations[migrations.IndexOf(latest) - 1], ct);
                await context.Database.ExecuteSqlRawAsync(sql.Insert("WalletAddresses", ("Index", "7"),
                    ("IsChange", sql.Bool(false)), ("AddressType", "1"), ("Address", "'bcrt1qlegacy'"),
                    ("IsReserved", sql.Bool(true))), ct);
                await context.Database.ExecuteSqlRawAsync(sql.Insert("ForwardCircuits", ("IncomingChannelId", Blob((byte[])channel)),
                    ("IncomingHtlcId", "1"), ("IncomingAmountMsat", "100100"), ("IncomingCltvExpiry", "200"),
                    ("PaymentHash", Blob((byte[])hash)), ("IncomingSharedSecret", Blob((byte[])secret)),
                    ("OutgoingShortChannelId", Blob((byte[])new ShortChannelId(1, 1, 0))), ("OutgoingAmountMsat", "100000"),
                    ("OutgoingCltvExpiry", "180"), ("CreatedAt", at.UtcTicks.ToString()), ("Status", "0")), ct);
            }
            await migrator.MigrateAsync(cancellationToken: ct);
            await context.Database.ExecuteSqlRawAsync(sql.Insert("AccountingPrices", ("Id", "1"), ("Currency", "'USD'"),
                ("Time", at.UtcTicks.ToString()), ("Price", sql.Decimal(100_000)), ("Source", "1"),
                ("FetchedAt", at.UtcTicks.ToString())), ct);
        }
        await using (var context = compiledFactory())
        {
            if (upgrade)
            {
                var address = await context.WalletAddresses.SingleAsync(ct);
                Assert.Equal(0u, address.AccountIndex);
                Assert.Equal("default", address.AccountName);
                Assert.Null(address.DerivationIndex);
                Assert.Equal(7u, address.Index);
                Assert.True(address.IsReserved);
                var legacy = await new ForwardCircuitDbRepository(context).GetByIncomingAsync(channel, 1);
                Assert.NotNull(legacy);
                Assert.Equal(legacy.IncomingAmount, legacy.ActualIncomingAmount);
                Assert.Null(legacy.IncomingClaimedPreimage);
            }
            context.WalletAccounts.Add(new WalletAccountEntity
            {
                Name = "named",
                AddressType = AddressType.P2Tr,
                AccountIndex = 1,
                ExtendedPublicKey = "test-public-key",
                MasterFingerprint = [1, 2, 3, 4],
                DerivationPath = "m/86'/1'/1'",
                BirthdayHeight = 100,
                ExternalKeyCount = 3,
                InternalKeyCount = 2
            });
            context.WalletHistoryRescanStates.Add(new WalletHistoryRescanStateEntity
            {
                Generation = Guid.NewGuid(),
                RequestedFromHeight = 90,
                AvailableFromHeight = 100,
                TargetHeight = 150,
                CursorHeight = 110,
                CursorHash = Enumerable.Repeat((byte)31, 32).ToArray(),
                AddressCount = 30,
                IsActive = true,
                IsPartial = true,
                Error = "retained checkpoint"
            });
            context.WalletTransactionLabels.Add(new WalletTransactionLabelEntity { TransactionId = txId, Label = "durable label" });
            context.AccountingPriceReplacementAudits.Add(new AccountingPriceReplacementAuditEntity
            {
                PriceId = 1,
                OldPrice = 100_000m,
                NewPrice = 110_000m,
                OldSource = 1,
                NewSource = 2,
                OldFetchedAt = at,
                ReplacedAt = at.AddHours(1),
                OperatorSource = "operator",
                Note = "correction proof"
            });
            var circuit = new ForwardCircuitModel(channel, 2, LightningMoney.MilliSatoshis(100_101), 200,
                hash, secret, new ShortChannelId(1, 1, 0), LightningMoney.MilliSatoshis(100_000), 180, at);
            circuit.SetActualIncomingAmount(LightningMoney.MilliSatoshis(100_100));
            circuit.MarkIncomingClaimed(secret);
            await new ForwardCircuitDbRepository(context).AddAsync(circuit);
            await context.SaveChangesAsync(ct);
        }
        await using (var context = compiledFactory())
        {
            var account = await context.WalletAccounts.SingleAsync(ct);
            Assert.Equal(1u, account.AccountIndex);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, account.MasterFingerprint);
            Assert.Equal("m/86'/1'/1'", account.DerivationPath);
            Assert.False(account.WatchOnly);
            Assert.Equal(3u, account.ExternalKeyCount);
            var history = await context.WalletHistoryRescanStates.SingleAsync(ct);
            Assert.True(history.IsActive && history.IsPartial);
            Assert.Equal(110u, history.CursorHeight);
            Assert.Equal(150u, history.TargetHeight);
            Assert.Equal(Enumerable.Repeat((byte)31, 32), history.CursorHash);
            Assert.Equal("retained checkpoint", history.Error);
            Assert.Equal("durable label", (await context.WalletTransactionLabels.SingleAsync(ct)).Label);
            var audit = await context.AccountingPriceReplacementAudits.SingleAsync(ct);
            Assert.Equal(100_000m, audit.OldPrice);
            Assert.Equal(110_000m, audit.NewPrice);
            Assert.Equal(at.AddHours(1), audit.ReplacedAt);
            Assert.Equal("correction proof", audit.Note);
            var restored = await new ForwardCircuitDbRepository(context).GetByIncomingAsync(channel, 2);
            Assert.NotNull(restored);
            Assert.Equal(LightningMoney.MilliSatoshis(100_101), restored.IncomingAmount);
            Assert.Equal(LightningMoney.MilliSatoshis(100_100), restored.ActualIncomingAmount);
            Assert.Equal(secret, restored.IncomingClaimedPreimage);
            Assert.Equal(LightningMoney.MilliSatoshis(100), restored.ActualFee);
        }
    }
}
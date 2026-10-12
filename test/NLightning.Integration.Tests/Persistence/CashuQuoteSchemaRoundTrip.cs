using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.ValueObjects;
using Domain.Cashu.Enums;
using Domain.Cashu.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Cashu;

/// <summary>
/// Provider-agnostic proof for migration <c>AddCashuProcessorQuotes</c> (NL-997), shared by the SQLite test and the
/// Postgres test: the schema right before it moves forward; quotes of every method and direction round-trip every
/// field (the largest amounts, a non-UTC time to the tick), updates staged before and after a save and on a reloaded
/// quote are written, the queries (by id, by payment hash, by address, pending on-chain melts) read what was saved,
/// deposits round-trip and are found unreported until marked, and a second quote with the same id fails the save.
/// </summary>
internal static class CashuQuoteSchemaRoundTrip
{
    private const string MigrationName = "_AddCashuProcessorQuotes";

    private static readonly DateTimeOffset s_now = new DateTimeOffset(2026, 10, 3, 9, 15, 30, TimeSpan.FromHours(-4))
       .AddTicks(7_654_321);

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddCashuProcessorQuotes, then the migration
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        var paymentHash = new Hash(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());
        var bolt11Melt = new CashuQuoteModel(new string('q', CashuQuoteModel.QuoteIdMaxLength), CashuQuoteMethod.Bolt11,
                                             CashuQuoteDirection.Outgoing,
                                             LightningMoney.MilliSatoshis((ulong)long.MaxValue), s_now)
        {
            MaxFee = LightningMoney.MilliSatoshis((ulong)long.MaxValue),
            Request = new string('l', 4_000),
            PaymentHash = paymentHash
        };
        var onchainMelt = new CashuQuoteModel("melt-onchain", CashuQuoteMethod.Onchain, CashuQuoteDirection.Outgoing,
                                              LightningMoney.Satoshis(50_000), s_now.AddMinutes(1))
        {
            Address = new string('b', CashuQuoteModel.AddressMaxLength),
            MaxFee = LightningMoney.Satoshis(700),
            FeeIndex = uint.MaxValue
        };
        var onchainMint = new CashuQuoteModel("mint-onchain", CashuQuoteMethod.Onchain, CashuQuoteDirection.Incoming,
                                              LightningMoney.Zero, s_now.AddMinutes(2))
        {
            Address = "bcrt1qmint"
        };
        await using (var context = contextFactory())
        {
            var repository = new CashuQuoteDbRepository(context);
            repository.Add(bolt11Melt);
            repository.Add(onchainMelt);
            repository.Add(onchainMint);
            bolt11Melt.SetState(CashuQuoteState.Dispatching, s_now.AddSeconds(1));
            repository.Update(bolt11Melt);
            await context.SaveChangesAsync(cancellationToken);

            // An update after the save goes through the tracked row
            onchainMelt.SetState(CashuQuoteState.Dispatching, s_now.AddMinutes(3));
            repository.Update(onchainMelt);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: every field, and the queries
        await using (var context = contextFactory())
        {
            var repository = new CashuQuoteDbRepository(context);
            var stored = await repository.GetAsync(bolt11Melt.QuoteId);
            Assert.NotNull(stored);
            Assert.Equal(CashuQuoteMethod.Bolt11, stored.Method);
            Assert.Equal(CashuQuoteDirection.Outgoing, stored.Direction);
            Assert.Equal(bolt11Melt.Amount, stored.Amount);
            Assert.Equal(bolt11Melt.MaxFee, stored.MaxFee);
            Assert.Null(stored.Fee);
            Assert.Equal(paymentHash, stored.PaymentHash);
            Assert.Equal(bolt11Melt.Request, stored.Request);
            Assert.Equal(CashuQuoteState.Dispatching, stored.State);
            Assert.Equal(s_now, stored.CreatedAt);
            Assert.Equal(s_now.UtcTicks, stored.CreatedAt.UtcTicks);
            Assert.Equal(s_now.AddSeconds(1), stored.UpdatedAt);

            Assert.Equal(bolt11Melt.QuoteId, (await repository.GetOutgoingByPaymentHashAsync(paymentHash))?.QuoteId);
            Assert.Null(await repository.GetOutgoingByPaymentHashAsync(new Hash(new byte[32])));
            var pending = await repository.ListOutgoingAsync(CashuQuoteMethod.Onchain, CashuQuoteState.Dispatching);
            var melt = Assert.Single(pending);
            Assert.Equal(onchainMelt.Address, melt.Address);
            Assert.Equal(uint.MaxValue, melt.FeeIndex);
            Assert.Empty(await repository.ListOutgoingAsync(CashuQuoteMethod.Onchain, CashuQuoteState.Pending));
            var mint = Assert.Single(await repository.GetIncomingByAddressesAsync(["bcrt1qmint", "bcrt1qother"]));
            Assert.Equal("mint-onchain", mint.QuoteId);
            Assert.Empty(await repository.GetIncomingByAddressesAsync([onchainMelt.Address!]));

            // A reloaded quote updated in a new context: sent, then paid with its outpoint and fee
            melt.TxId = new TxId(Enumerable.Range(0, 32).Select(i => (byte)(200 - i)).ToArray());
            melt.OutputIndex = 1;
            melt.Fee = LightningMoney.Satoshis(321);
            melt.SetState(CashuQuoteState.Failed, s_now.AddMinutes(4), new string('x', 5_000));
            repository.Update(melt);
            repository.AddDeposit(new CashuDepositModel("mint-onchain", melt.TxId.Value, 0,
                                                        LightningMoney.Satoshis(21_000_000UL * 100_000_000UL),
                                                        uint.MaxValue));
            repository.AddDeposit(new CashuDepositModel("mint-onchain", melt.TxId.Value, 2,
                                                        LightningMoney.Satoshis(1_000), 100));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new CashuQuoteDbRepository(context);
            var melt = await repository.GetAsync("melt-onchain");
            Assert.NotNull(melt);
            Assert.Equal(CashuQuoteState.Failed, melt.State);
            Assert.Equal(1024, melt.FailureReason!.Length);
            Assert.Equal(LightningMoney.Satoshis(321), melt.Fee);
            Assert.Equal($"{melt.TxId}:1", melt.Outpoint);

            var deposits = await repository.GetDepositsAsync("mint-onchain");
            Assert.Equal([100u, uint.MaxValue], deposits.Select(d => d.BlockHeight));
            Assert.Equal(LightningMoney.Satoshis(21_000_000UL * 100_000_000UL), deposits[1].Amount);
            Assert.Equal(2, (await repository.ListUnreportedDepositsAsync()).Count);
            var first = deposits[0];
            first.ReportedAt = s_now;
            repository.UpdateDeposit(first);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new CashuQuoteDbRepository(context);
            var unreported = Assert.Single(await repository.ListUnreportedDepositsAsync());
            Assert.Equal(0u, unreported.OutputIndex);
            var reported = await repository.GetDepositAsync(unreported.TxId, 2);
            Assert.Equal(s_now, reported?.ReportedAt);

            // A second quote with the same id fails the save
            repository.Add(new CashuQuoteModel("mint-onchain", CashuQuoteMethod.Bolt12, CashuQuoteDirection.Outgoing,
                                               LightningMoney.Satoshis(1), s_now));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
        }
    }
}
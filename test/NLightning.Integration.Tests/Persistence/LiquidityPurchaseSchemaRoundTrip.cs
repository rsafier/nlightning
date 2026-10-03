using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.LiquidityAds;

/// <summary>
/// Provider-agnostic proof for migration <c>AddLiquidityPurchases</c> (NL-850 LA3), shared by the SQLite test and the
/// Docker Postgres test: the schema right before it moves forward; then purchases round-trip every field (the extremes of
/// every rate field, the amounts and a non-UTC creation time to the tick), the save assigns their ids, updates staged
/// before and after a save and on a reloaded model are written, the queries (by channel, by funding attempt, the list
/// with its filters and pages, the griefing counts and the lease guard) read what was saved, and a second purchase on
/// the same funding attempt fails the save.
/// </summary>
internal static class LiquidityPurchaseSchemaRoundTrip
{
    private const string MigrationName = "_AddLiquidityPurchases";

    /// <summary>A non-UTC offset and a sub-millisecond tick: the stored instants must be exact.</summary>
    private static readonly DateTimeOffset s_now = new DateTimeOffset(2026, 10, 3, 9, 15, 30, TimeSpan.FromHours(-4))
       .AddTicks(7_654_321);

    private static readonly CompactPubKey s_peerA =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly CompactPubKey s_peerB =
        Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c");

    private static readonly FundingRate s_extremeRate =
        new(uint.MaxValue, uint.MaxValue, ushort.MaxValue, ushort.MaxValue, uint.MaxValue, uint.MaxValue);

    private static readonly FundingRate s_rate = new(100_000, 1_000_000, 500, 100, 10, 1_000);

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddLiquidityPurchases, then the migration
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

        // A sale with every field at an extreme, a buyer's purchase activated before its first save, and a splice sale
        var extremeSale = new LiquidityPurchaseModel(Channel(0x11), Txid(0x21), LiquidityPurchaseRole.Seller,
                                                     LiquidityPurchaseKind.SpliceRbf, long.MaxValue, long.MaxValue,
                                                     s_extremeRate,
                                                     LiquidityPaymentType.FromChannelBalanceForFutureHtlc,
                                                     long.MaxValue / 2_000, long.MaxValue / 2_000,
                                                     Enumerable.Range(0, 64).Select(i => (byte)(255 - i)).ToArray(),
                                                     Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(), s_peerA,
                                                     uint.MaxValue, s_now);
        var bought = Create(0x12, 0x22, LiquidityPurchaseRole.Buyer, s_peerB, s_now.AddMinutes(1));
        var splice = Create(0x11, 0x23, LiquidityPurchaseRole.Seller, s_peerA, s_now.AddMinutes(2),
                            LiquidityPurchaseKind.Splice);
        await using (var context = contextFactory())
        {
            var repository = new LiquidityPurchaseDbRepository(context);
            repository.Add(extremeSale);
            repository.Add(bought);
            repository.Add(splice);
            bought.MarkActive(800_000);
            repository.Update(bought);
            Assert.Throws<InvalidOperationException>(() => repository.Add(bought));

            await context.SaveChangesAsync(cancellationToken);

            // The save assigned the ids, and an update after it goes through the tracked row
            Assert.True(extremeSale.Id > 0);
            Assert.True(bought.Id > 0);
            Assert.True(splice.Id > 0);
            Assert.Equal(3, new[] { extremeSale.Id, bought.Id, splice.Id }.Distinct().Count());
            splice.MarkReplaced();
            repository.Update(splice);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: every field, to the tick
        await using (var context = contextFactory())
        {
            var repository = new LiquidityPurchaseDbRepository(context);
            var reloaded = await repository.GetByFundingTxIdAsync(Channel(0x11), Txid(0x21));
            Assert.NotNull(reloaded);
            AssertSame(extremeSale, reloaded);

            var reloadedBought = await repository.GetByFundingTxIdAsync(Channel(0x12), Txid(0x22));
            Assert.NotNull(reloadedBought);
            AssertSame(bought, reloadedBought);
            Assert.Equal(LiquidityPurchaseStatus.Active, reloadedBought.Status);
            Assert.Equal(800_000U, reloadedBought.LeaseStartHeight);

            var channel = await repository.GetByChannelIdAsync(Channel(0x11));
            Assert.Equal([extremeSale.Id, splice.Id], channel.Select(p => p.Id));
            Assert.Equal(LiquidityPurchaseStatus.Replaced, channel[1].Status);
            Assert.Null(await repository.GetByFundingTxIdAsync(Channel(0x12), Txid(0x21)));
            Assert.Empty(await repository.GetByChannelIdAsync(Channel(0x13)));

            // A reloaded model (never tracked by this context) closes early
            reloadedBought.MarkClosed(800_010);
            repository.Update(reloadedBought);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new LiquidityPurchaseDbRepository(context);
            var closed = await repository.GetByFundingTxIdAsync(Channel(0x12), Txid(0x22));
            Assert.NotNull(closed);
            Assert.Equal(LiquidityPurchaseStatus.Closed, closed.Status);
            Assert.Equal(800_000U, closed.LeaseStartHeight);
            Assert.Equal(800_010U, closed.ClosedAtHeight);
            Assert.True(closed.ClosedEarly);
            Assert.Equal(s_rate, closed.Rate);
        }

        // The queries: two more sales to peer A (one active on a channel of its own), one pending to peer B
        var activeSale = Create(0x14, 0x24, LiquidityPurchaseRole.Seller, s_peerA, s_now.AddMinutes(3));
        var pendingToB = Create(0x15, 0x25, LiquidityPurchaseRole.Seller, s_peerB, s_now.AddMinutes(4));
        await using (var context = contextFactory())
        {
            var repository = new LiquidityPurchaseDbRepository(context);
            activeSale.MarkActive(900_000);
            repository.Add(activeSale);
            repository.Add(pendingToB);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new LiquidityPurchaseDbRepository(context);

            // Newest first, filtered by role and status, paged
            var all = await repository.ListAsync(null, null, 0, 10);
            Assert.Equal([pendingToB.Id, activeSale.Id, splice.Id, bought.Id, extremeSale.Id], all.Select(p => p.Id));
            var sales = await repository.ListAsync(LiquidityPurchaseRole.Seller, null, 1, 2);
            Assert.Equal([activeSale.Id, splice.Id], sales.Select(p => p.Id));
            var pendingSales = await repository.ListAsync(LiquidityPurchaseRole.Seller, LiquidityPurchaseStatus.Pending,
                                                          0, 10);
            Assert.Equal([pendingToB.Id, extremeSale.Id], pendingSales.Select(p => p.Id));
            Assert.Equal([bought.Id],
                         (await repository.ListAsync(LiquidityPurchaseRole.Buyer, null, 0, 10)).Select(p => p.Id));
            Assert.Empty(await repository.ListAsync(null, null, 0, 0));

            // The griefing caps count pending sales only
            Assert.Equal(2, await repository.CountPendingSalesAsync());
            Assert.Equal(1, await repository.CountPendingSalesByPeerAsync(s_peerA));
            Assert.Equal(1, await repository.CountPendingSalesByPeerAsync(s_peerB));

            // The lease guard: an active sale binds until its lease ends, a pending one always, a buyer's never
            var lease = await repository.GetActiveSaleLeaseAsync(Channel(0x14), 904_031);
            Assert.NotNull(lease);
            Assert.Equal(activeSale.Id, lease.Id);
            Assert.Null(await repository.GetActiveSaleLeaseAsync(Channel(0x14), 904_032));
            Assert.Equal(extremeSale.Id, (await repository.GetActiveSaleLeaseAsync(Channel(0x11), 1))?.Id);
            Assert.Null(await repository.GetActiveSaleLeaseAsync(Channel(0x12), 800_001));
        }

        // A second purchase on the same funding attempt fails the save
        await using (var context = contextFactory())
        {
            var repository = new LiquidityPurchaseDbRepository(context);
            repository.Add(Create(0x14, 0x24, LiquidityPurchaseRole.Buyer, s_peerB, s_now));
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
        }
    }

    private static LiquidityPurchaseModel Create(byte channel, byte txid, LiquidityPurchaseRole role,
                                                 CompactPubKey peer, DateTimeOffset createdAt,
                                                 LiquidityPurchaseKind kind = LiquidityPurchaseKind.ChannelOpen)
    {
        return new LiquidityPurchaseModel(Channel(channel), Txid(txid), role, kind, 400_000, 450_000, s_rate,
                                          LiquidityPaymentType.FromChannelBalance, 625, 5_010,
                                          Enumerable.Repeat(channel, 64).ToArray(), [0x00, 0x20, txid], peer,
                                          4_032, createdAt);
    }

    private static void AssertSame(LiquidityPurchaseModel expected, LiquidityPurchaseModel actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.FundingTxId, actual.FundingTxId);
        Assert.Equal(expected.Role, actual.Role);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.RequestedSat, actual.RequestedSat);
        Assert.Equal(expected.ContributedSat, actual.ContributedSat);
        Assert.Equal(expected.Rate, actual.Rate);
        Assert.Equal(expected.PaymentType, actual.PaymentType);
        Assert.Equal(expected.MiningFeeSat, actual.MiningFeeSat);
        Assert.Equal(expected.ServiceFeeSat, actual.ServiceFeeSat);
        Assert.Equal(expected.Signature, actual.Signature);
        Assert.Equal(expected.FundingScript, actual.FundingScript);
        Assert.Equal(expected.PeerNodeId, actual.PeerNodeId);
        Assert.Equal(expected.LeaseBlocks, actual.LeaseBlocks);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.CreatedAt.UtcTicks, actual.CreatedAt.UtcTicks);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.LeaseStartHeight, actual.LeaseStartHeight);
        Assert.Equal(expected.ClosedAtHeight, actual.ClosedAtHeight);
        Assert.Equal(expected.ClosedEarly, actual.ClosedEarly);
    }

    private static ChannelId Channel(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static TxId Txid(byte fill) => Enumerable.Repeat(fill, 32).ToArray();
}
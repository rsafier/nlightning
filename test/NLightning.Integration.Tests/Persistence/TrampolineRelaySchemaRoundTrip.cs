using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Payment;

/// <summary>
/// Provider-agnostic proof for migration <c>AddTrampolineRelays</c> (NL-875), shared by the SQLite test and the Docker
/// Postgres test: a payment seeded on the schema right before it reads back as not a trampoline relay's; relays and
/// their parts round-trip every field (to the tick, the extremes of every amount), staged rows are seen by the key
/// lookups before the save, the status moves through updates, the listings filter and page, a payment flagged as a
/// relay's outgoing leg keeps the flag (also through a retry's replacement), and the payer side's trampoline hops
/// round-trip per attempt.
/// </summary>
internal static class TrampolineRelaySchemaRoundTrip
{
    private const string MigrationName = "_AddTrampolineRelays";

    /// <summary>A non-UTC offset and a sub-millisecond tick: the stored instants must be exact.</summary>
    private static readonly DateTimeOffset s_now = new DateTimeOffset(2026, 10, 3, 9, 15, 30, TimeSpan.FromHours(-4))
       .AddTicks(7_654_321);

    private static readonly CompactPubKey s_next =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly CompactPubKey s_payee =
        Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c");

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddTrampolineRelays, with a payment in it
        var legacyPayment = PaymentSchemaRoundTrip.CreatePayment(0x91);
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);
            var sql = new MigrationSqlDialect(databaseType);
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("Payments",
                           ("PaymentHash", "{0}"), ("Bolt11", "{1}"), ("PayeeNodeId", "{2}"),
                           ("AmountMsat", $"{legacyPayment.Amount.MilliSatoshi}"),
                           ("FeeMsat", $"{legacyPayment.Fee.MilliSatoshi}"),
                           ("CreatedAt", $"{legacyPayment.CreatedAt.UtcTicks}"), ("Status", "0")),
                [(byte[])legacyPayment.PaymentHash, legacyPayment.Bolt11!, (byte[])legacyPayment.PayeeNodeId],
                cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: the seeded payment is our own spend
        await using (var context = contextFactory())
        {
            var payment = await new PaymentDbRepository(context).GetByPaymentHashAsync(legacyPayment.PaymentHash);
            Assert.NotNull(payment);
            Assert.False(payment.IsTrampolineRelay);
            Assert.False((await context.Payments.AsNoTracking().SingleAsync(cancellationToken)).IsTrampolineRelay);
        }

        await AssertRelaysRoundTripAsync(contextFactory, cancellationToken);
        await AssertRelayPaymentRoundTripAsync(contextFactory, cancellationToken);
        await AssertTrampolineHopsRoundTripAsync(contextFactory, cancellationToken);
    }

    private static async Task AssertRelaysRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                         CancellationToken cancellationToken)
    {
        // A relay with every field at an extreme, one to blinded paths only, and an old one that failed
        var full = new TrampolineRelayModel(HashOf(0x01), s_next, LightningMoney.MilliSatoshis(long.MaxValue),
                                            uint.MaxValue, LightningMoney.MilliSatoshis(long.MaxValue), s_now,
                                            Bytes(0x10, 1_366), Bytes(0x20, 300), s_payee, Bytes(0x30, 8),
                                            Bytes(0x40, 500));
        var blinded = new TrampolineRelayModel(HashOf(0x02), null, LightningMoney.MilliSatoshis(1), 0,
                                               LightningMoney.MilliSatoshis(2), s_now.AddMinutes(1),
                                               recipientBlindedPaths: Bytes(0x50, 200));
        var old = new TrampolineRelayModel(HashOf(0x03), s_next, LightningMoney.MilliSatoshis(10_000), 800_000,
                                           LightningMoney.MilliSatoshis(10_500), s_now.AddDays(-1));
        TrampolineRelayPartModel[] parts =
        [
            Part(0x01, ChannelOf(0xB2), 7, 6_000, 800_200),
            Part(0x01, ChannelOf(0xA1), ulong.MaxValue, long.MaxValue, uint.MaxValue, outerPaymentSecret: null),
            Part(0x02, ChannelOf(0xA1), 0, 2, 0),
            Part(0x03, ChannelOf(0xC3), 1, 10_500, 800_100)
        ];

        await using (var context = contextFactory())
        {
            var repository = new TrampolineRelayDbRepository(context);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddPartAsync(parts[0]));
            await repository.AddAsync(full);
            await repository.AddAsync(blinded);
            await repository.AddAsync(old);
            foreach (var part in parts)
                await repository.AddPartAsync(part);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddAsync(full));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddPartAsync(parts[1]));

            // Staged rows are seen by the key lookups before the save
            var staged = await repository.GetAsync(full.PaymentHash);
            Assert.NotNull(staged);
            AssertRelay(full, staged.Value.Relay);
            Assert.Equal([parts[1], parts[0]], staged.Value.Parts, PartComparer.Instance);
            Assert.Equal(parts[3], (await repository.GetPartAsync(ChannelOf(0xC3), 1))!, PartComparer.Instance);

            old.MarkFailed(0x2002, "temporary_node_failure", s_now.AddHours(-23));
            await repository.UpdateAsync(old);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: every field, to the tick, and the parts in key order
        await using (var context = contextFactory())
        {
            var repository = new TrampolineRelayDbRepository(context);
            var reloaded = await repository.GetAsync(full.PaymentHash);
            Assert.NotNull(reloaded);
            AssertRelay(full, reloaded.Value.Relay);
            Assert.Equal([parts[1], parts[0]], reloaded.Value.Parts, PartComparer.Instance);
            Assert.Equal([parts[1], parts[0]], await repository.GetPartsAsync(full.PaymentHash),
                         PartComparer.Instance);
            AssertRelay(blinded, (await repository.GetAsync(blinded.PaymentHash))!.Value.Relay);
            AssertRelay(old, (await repository.GetAsync(old.PaymentHash))!.Value.Relay);
            Assert.Null(await repository.GetAsync(HashOf(0x04)));
            Assert.Empty(await repository.GetPartsAsync(HashOf(0x04)));
            Assert.Null(await repository.GetPartAsync(ChannelOf(0xC3), 2));
            Assert.Equal(parts[2], (await repository.GetPartAsync(ChannelOf(0xA1), 0))!, PartComparer.Instance);

            Assert.Equal([full.PaymentHash, blinded.PaymentHash],
                         (await repository.ListUnfinishedAsync()).Select(r => r.PaymentHash));

            // A reloaded model moves on: Sending with the outgoing secret, then Fulfilled
            var relay = reloaded.Value.Relay;
            relay.MarkSending(Bytes(0x60, 32));
            await repository.UpdateAsync(relay);
            await context.SaveChangesAsync(cancellationToken);
            relay.MarkFulfilled(new Secret(Bytes(0x70, 32)), LightningMoney.MilliSatoshis(long.MaxValue),
                                s_now.AddSeconds(5));
            await repository.UpdateAsync(relay);
            await context.SaveChangesAsync(cancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpdateAsync(
                                                                    new TrampolineRelayModel(
                                                                        HashOf(0x04), s_next,
                                                                        LightningMoney.MilliSatoshis(1), 1,
                                                                        LightningMoney.MilliSatoshis(1), s_now)));
            full = relay;
        }

        await using (var context = contextFactory())
        {
            var repository = new TrampolineRelayDbRepository(context);
            var fulfilled = (await repository.GetAsync(full.PaymentHash))!.Value.Relay;
            AssertRelay(full, fulfilled);
            Assert.Equal(TrampolineRelayStatus.Fulfilled, fulfilled.Status);
            Assert.Equal([blinded.PaymentHash], (await repository.ListUnfinishedAsync()).Select(r => r.PaymentHash));

            // The listing: newest first, filtered by status, incoming channel and time, paged
            Assert.Equal([blinded.PaymentHash, full.PaymentHash, old.PaymentHash],
                         await ListAsync(repository, new TrampolineRelayListQuery(0, 10), cancellationToken));
            Assert.Equal([full.PaymentHash],
                         await ListAsync(repository, new TrampolineRelayListQuery(1, 1), cancellationToken));
            Assert.Equal([old.PaymentHash],
                         await ListAsync(repository,
                                         new TrampolineRelayListQuery(0, 10, Status: TrampolineRelayStatus.Failed),
                                         cancellationToken));
            Assert.Equal([blinded.PaymentHash, full.PaymentHash],
                         await ListAsync(repository, new TrampolineRelayListQuery(0, 10, IncomingChannelId: ChannelOf(0xA1)),
                                         cancellationToken));
            Assert.Equal([blinded.PaymentHash, full.PaymentHash],
                         await ListAsync(repository, new TrampolineRelayListQuery(0, 10, Since: s_now),
                                         cancellationToken));
            Assert.Equal([full.PaymentHash, old.PaymentHash],
                         await ListAsync(repository, new TrampolineRelayListQuery(0, 10, Until: s_now),
                                         cancellationToken));
            Assert.Empty(await ListAsync(repository, new TrampolineRelayListQuery(0, 0), cancellationToken));
        }
    }

    private static async Task AssertRelayPaymentRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                               CancellationToken cancellationToken)
    {
        // The outgoing leg of a relay keeps its flag, also when a failed attempt is replaced by a retry
        var leg = new PaymentModel(HashOf(0x05), null, s_payee, LightningMoney.MilliSatoshis(10_000),
                                   LightningMoney.MilliSatoshis(7), s_now)
        { IsTrampolineRelay = true };
        await using (var context = contextFactory())
        {
            await new PaymentDbRepository(context).AddAsync(leg);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new PaymentDbRepository(context);
            var stored = await repository.GetByPaymentHashAsync(leg.PaymentHash);
            PaymentSchemaRoundTrip.AssertPayment(leg, stored);
            Assert.True(stored!.IsTrampolineRelay);
            Assert.True((await repository.GetInFlightAsync()).Single(p => p.PaymentHash == leg.PaymentHash)
                                                              .IsTrampolineRelay);

            stored.Fail(null, null, "attempt failed", s_now.AddSeconds(1));
            await repository.UpdateAsync(stored);
            await context.SaveChangesAsync(cancellationToken);
            await repository.AddAsync(new PaymentModel(leg.PaymentHash, null, s_payee, leg.Amount,
                                                       LightningMoney.MilliSatoshis(9), s_now.AddSeconds(2))
            {
                IsTrampolineRelay = true
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var listed = (await new PaymentDbRepository(context).ListAsync(0, 100))
               .Single(p => p.PaymentHash == leg.PaymentHash);
            Assert.True(listed.IsTrampolineRelay);
            Assert.Equal(9UL, listed.Fee.MilliSatoshi);
        }
    }

    private static async Task AssertTrampolineHopsRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                                 CancellationToken cancellationToken)
    {
        var hash = HashOf(0x06);
        PaymentTrampolineHopModel[] first =
        [
            Hop(hash, 0, 1, s_payee, 0x81, 10_000, 800_040),
            Hop(hash, 0, 0, s_next, 0x82, long.MaxValue, uint.MaxValue)
        ];
        PaymentTrampolineHopModel[] second = [Hop(hash, 1, 0, s_payee, 0x83, 10_000, 800_040)];

        await using (var context = contextFactory())
        {
            var repository = new PaymentTrampolineHopDbRepository(context);
            await repository.AddRangeAsync(first);

            // Staged rows are seen before the save
            Assert.Equal([first[1], first[0]], await repository.GetByPaymentAsync(hash));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AddRangeAsync([first[0]]));
            await context.SaveChangesAsync(cancellationToken);

            await repository.AddRangeAsync(second);
            Assert.Equal([first[1], first[0], second[0]], await repository.GetByPaymentAsync(hash));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new PaymentTrampolineHopDbRepository(context);
            Assert.Equal([first[1], first[0], second[0]], await repository.GetByPaymentAsync(hash));
            Assert.Equal([first[1], first[0]], await repository.GetByPaymentAsync(hash, 0));
            Assert.Equal([second[0]], await repository.GetByPaymentAsync(hash, 1));
            Assert.Empty(await repository.GetByPaymentAsync(hash, 2));
            Assert.Empty(await repository.GetByPaymentAsync(HashOf(0x07)));
        }
    }

    private static async Task<IReadOnlyList<Hash>> ListAsync(TrampolineRelayDbRepository repository,
                                                             TrampolineRelayListQuery query,
                                                             CancellationToken cancellationToken) =>
        (await repository.ListAsync(query, cancellationToken)).Select(r => r.PaymentHash).ToList();

    private static void AssertRelay(TrampolineRelayModel expected, TrampolineRelayModel actual)
    {
        Assert.Equal(expected.PaymentHash, actual.PaymentHash);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.NextNodeId, actual.NextNodeId);
        Assert.Equal(expected.NextEncryptedRecipientData, actual.NextEncryptedRecipientData);
        Assert.Equal(expected.NextPathKey, actual.NextPathKey);
        Assert.Equal(expected.RecipientFeatures, actual.RecipientFeatures);
        Assert.Equal(expected.RecipientBlindedPaths, actual.RecipientBlindedPaths);
        Assert.Equal(expected.NextTrampolinePacket, actual.NextTrampolinePacket);
        Assert.Equal(expected.AmountOut, actual.AmountOut);
        Assert.Equal(expected.CltvExpiryOut, actual.CltvExpiryOut);
        Assert.Equal(expected.IncomingTotal, actual.IncomingTotal);
        Assert.Equal(expected.FeeEarned, actual.FeeEarned);
        Assert.Equal(expected.OutgoingPaymentSecret, actual.OutgoingPaymentSecret);
        Assert.Equal(expected.Preimage, actual.Preimage);
        Assert.Equal(expected.FailureCode, actual.FailureCode);
        Assert.Equal(expected.FailureReason, actual.FailureReason);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.CreatedAt.UtcTicks, actual.CreatedAt.UtcTicks);
        Assert.Equal(expected.CompletedAt, actual.CompletedAt);
    }

    private static TrampolineRelayPartModel Part(byte relay, ChannelId channelId, ulong htlcId, long amountMsat,
                                                 uint cltvExpiry, byte[]? outerPaymentSecret = null) =>
        new(HashOf(relay), channelId, htlcId, LightningMoney.MilliSatoshis(amountMsat), cltvExpiry,
            new Secret(Bytes((byte)(htlcId + relay), 32)), new Secret(Bytes((byte)(htlcId + relay + 1), 32)),
            outerPaymentSecret ?? (htlcId == ulong.MaxValue ? null : Bytes((byte)(relay + 2), 32)));

    private static PaymentTrampolineHopModel Hop(Hash hash, int attempt, int index, CompactPubKey node, byte secret,
                                                 long amountMsat, uint cltvExpiry) =>
        new(hash, attempt, index, node, new Secret(Bytes(secret, 32)), LightningMoney.MilliSatoshis(amountMsat),
            cltvExpiry);

    private static Hash HashOf(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    private static ChannelId ChannelOf(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    private static byte[] Bytes(byte seed, int length) => Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();

    /// <summary>Parts compare by value, their byte arrays by content.</summary>
    private sealed class PartComparer : IEqualityComparer<TrampolineRelayPartModel>
    {
        public static readonly PartComparer Instance = new();

        public bool Equals(TrampolineRelayPartModel? x, TrampolineRelayPartModel? y) =>
            x is not null && y is not null
         && x with { OuterPaymentSecret = null } == y with { OuterPaymentSecret = null }
         && (x.OuterPaymentSecret ?? []).AsSpan().SequenceEqual(y.OuterPaymentSecret ?? [])
         && x.OuterPaymentSecret is null == y.OuterPaymentSecret is null;

        public int GetHashCode(TrampolineRelayPartModel obj) => obj.HtlcId.GetHashCode();
    }
}
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.Commitments;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Database.Payment;

/// <summary>
/// Provider-agnostic proof for migration <c>AddAttributionData</c> (NL-326), shared by the SQLite test and the Docker
/// Postgres/SQL Server tests: an HTLC and a payment hop written before the migration load after it with no
/// attribution, no receipt time (hold time zero) and no hold time; afterwards a commitment dance whose removals carry
/// <c>attribution_data</c> (920 bytes) and <c>fulfillment_payload</c> round-trips byte for byte, new HTLC rows keep
/// their exact receipt time, and a payment's per-hop hold times round-trip.
/// </summary>
internal static class AttributionSchemaRoundTrip
{
    private const string MigrationName = "_AddAttributionData";

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddAttributionData, with a channel, an HTLC and a payment hop in it
        var payment = PaymentSchemaRoundTrip.CreatePayment(0x61);
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);
            await PaymentSchemaRoundTrip.SeedAsync(context, databaseType, cancellationToken);
            await SeedPaymentAsync(context, databaseType, payment, cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: the seeded rows moved forward with the new columns empty
        await using (var context = contextFactory())
        {
            var htlc = await context.Htlcs.AsNoTracking().SingleAsync(cancellationToken);
            Assert.Null(htlc.AttributionData);
            Assert.Null(htlc.FulfillmentPayload);
            Assert.Null(htlc.AddedAt);
            Assert.Null(await new ChannelStateDbRepository(context).GetHtlcAddedAtAsync(
                            PaymentSchemaRoundTrip.SeededChannelId, PaymentSchemaRoundTrip.SeededHtlc));

            var stored = await new PaymentDbRepository(context).GetByPaymentHashAsync(payment.PaymentHash);
            Assert.NotNull(stored);
            Assert.All(stored.Route, h => Assert.Null(h.HoldTime));
            Assert.Equal(payment.HopSharedSecrets, stored.HopSharedSecrets);
        }

        await AssertAttributedDanceRoundTripsAsync(contextFactory, cancellationToken);
        await AssertHoldTimesRoundTripAsync(contextFactory, payment, cancellationToken);
    }

    /// <summary>
    /// A new channel driven through a seeded commitment dance on this provider: every removal of an even HTLC id
    /// carries 920 bytes of <c>attribution_data</c> (every fourth fulfill a <c>fulfillment_payload</c>), each
    /// transition is saved and the whole state reloaded and compared, and new rows keep their receipt time exactly.
    /// </summary>
    public static async Task AssertAttributedDanceRoundTripsAsync(Func<NLightningDbContext> contextFactory,
                                                                  CancellationToken cancellationToken)
    {
        var channel = SqliteDbTestContext.CreateChannel(true);
        var @params = CommitmentParams.FromChannel(channel);
        var driver = new CommitmentDanceDriver(channel.ChannelId, @params, channel.LocalBalance.MilliSatoshi,
                                               channel.RemoteBalance.MilliSatoshi, seed: 5);
        await using (var context = contextFactory())
        {
            await new ChannelDbRepository(context, new FakeSha256()).AddAsync(channel);
            await new ChannelStateDbRepository(context).InitializeAsync(driver.Us);
            await context.SaveChangesAsync(cancellationToken);
        }

        // A clock with a sub-millisecond tick and a non-UTC offset: the stored instant must be exact
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(2))
                                             .AddTicks(7_654_321));
        var attributedKinds = new HashSet<HtlcRemovalKind>();
        var stamped = new Dictionary<HtlcKey, DateTimeOffset>();
        for (var i = 0; i < 120 && attributedKinds.Count < 2; i++)
        {
            var result = driver.NextTransition();
            await using (var context = contextFactory())
            {
                await new ChannelStateDbRepository(context, clock).ApplyAsync(result.Next, result.Transition);
                await context.SaveChangesAsync(cancellationToken);
            }

            foreach (var htlc in result.Transition.UpsertedHtlcs)
            {
                if (htlc.Removal is { AttributionData.IsEmpty: false } removal)
                    attributedKinds.Add(removal.Kind);
                stamped.TryAdd(htlc.Key, clock.GetUtcNow());
            }

            await using var readContext = contextFactory();
            var state = await new ChannelStateDbRepository(readContext).LoadAsync(channel.ChannelId, @params);
            CommitmentsAssert.Equal(driver.Us, state!.Commitments);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Contains(HtlcRemovalKind.Fulfill, attributedKinds);
        Assert.Contains(HtlcRemovalKind.Fail, attributedKinds);
        Assert.NotEmpty(stamped);
        await using (var context = contextFactory())
        {
            var repository = new ChannelStateDbRepository(context);
            foreach (var (key, expected) in stamped)
            {
                // Every row is kept (nothing prunes here): stamped once, on insert, to the tick
                var addedAt = await repository.GetHtlcAddedAtAsync(channel.ChannelId, key);
                Assert.Equal(expected.UtcTicks, addedAt!.Value.UtcTicks);
            }
        }
    }

    /// <summary>The seeded payment's per-hop hold times, written by an outcome's update, round-trip.</summary>
    private static async Task AssertHoldTimesRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                           Domain.Payments.Models.PaymentModel seeded,
                                                           CancellationToken cancellationToken)
    {
        await using (var context = contextFactory())
        {
            var repository = new PaymentDbRepository(context);
            var payment = await repository.GetByPaymentHashAsync(seeded.PaymentHash);
            payment!.RecordHoldTimes([TimeSpan.FromMilliseconds(429_496_729_500), TimeSpan.Zero]);
            await repository.UpdateAsync(payment);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var readContext = contextFactory();
        var stored = await new PaymentDbRepository(readContext).GetByPaymentHashAsync(seeded.PaymentHash);
        Assert.Equal([TimeSpan.FromMilliseconds(429_496_729_500), TimeSpan.Zero], stored!.Route.Select(h => h.HoldTime));
    }

    private static async Task SeedPaymentAsync(NLightningDbContext context, DatabaseType databaseType,
                                               Domain.Payments.Models.PaymentModel payment,
                                               CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Payments",
                       ("PaymentHash", "{0}"), ("Bolt11", "{1}"), ("PayeeNodeId", "{2}"), ("AmountMsat", "20000000"),
                       ("FeeMsat", "2001"), ("CreatedAt", $"{payment.CreatedAt.UtcTicks}"), ("Status", "0")),
            [(byte[])payment.PaymentHash, payment.Bolt11!, (byte[])payment.PayeeNodeId], cancellationToken);
        for (var i = 0; i < payment.Route.Count; i++)
        {
            var hop = payment.Route[i];
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("PaymentHops",
                           ("PaymentHash", "{0}"), ("HopIndex", $"{i}"), ("NodeId", "{1}"), ("ShortChannelId", "{2}"),
                           ("AmountMsat", $"{hop.Amount.MilliSatoshi}"), ("CltvExpiry", $"{hop.CltvExpiry}"),
                           ("SharedSecret", "{3}")),
                [(byte[])payment.PaymentHash, (byte[])hop.NodeId, (byte[])hop.ShortChannelId,
                 (byte[])hop.SharedSecret], cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
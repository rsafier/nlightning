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
/// Provider-agnostic proof for migration <c>AddTrampolineRelayAttempts</c> (NL-899/NL-981), shared by the SQLite test
/// and the Docker Postgres test: a failed relay saved on the schema right before it is replaced by a payer's retry
/// after the upgrade and kept as attempt 1, a second failure as attempt 2; the replaced attempts are listed and
/// filtered (status, incoming channel, time, page) next to the relay the hash has now, the relay totals count them as
/// failed and sum the fulfilled relays' fees, and <c>listpayments</c>' repository leaves relay legs out and counts
/// them.
/// </summary>
internal static class TrampolineRelayAttemptsSchemaRoundTrip
{
    private const string MigrationName = "_AddTrampolineRelayAttempts";

    private static readonly DateTimeOffset s_now = new DateTimeOffset(2026, 10, 3, 9, 15, 30, TimeSpan.FromHours(-4))
       .AddTicks(7_654_321);

    private static readonly CompactPubKey s_next =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly Hash s_hash = new(Enumerable.Repeat((byte)0xE1, 32).ToArray());
    private static readonly Hash s_otherHash = new(Enumerable.Repeat((byte)0xE2, 32).ToArray());
    private static readonly ChannelId s_channelA = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());
    private static readonly ChannelId s_channelB = new(Enumerable.Repeat((byte)0xB2, 32).ToArray());
    private static readonly ChannelId s_channelC = new(Enumerable.Repeat((byte)0xC3, 32).ToArray());

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddTrampolineRelayAttempts, with a failed relay of two parts on two
        // channels (the first attempt of the payer). Raw SQL, because the current model maps a column (a later
        // migration's, NL-923) that this older schema does not have
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            await context.GetService<IMigrator>()
                         .MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);

            var sql = new MigrationSqlDialect(databaseType);
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("TrampolineRelays",
                           ("PaymentHash", "{0}"), ("Status", "3"), ("NextNodeId", "{1}"),
                           ("AmountOutMsat", "1000000"), ("CltvExpiryOut", "800000"),
                           ("IncomingTotalMsat", "1000500"), ("FailureCode", $"{(ushort)0x2019}"),
                           ("FailureReason", "{2}"), ("CreatedAt", $"{s_now.UtcTicks}"),
                           ("CompletedAt", $"{s_now.AddSeconds(1).UtcTicks}")),
                [(byte[])s_hash, (byte[])s_next, "fee or expiry insufficient"], cancellationToken);
            foreach (var (channel, htlcId, amount) in new[] { (s_channelA, 1UL, 600_000L), (s_channelB, 2UL, 400_500L) })
                await context.Database.ExecuteSqlRawAsync(
                    sql.Insert("TrampolineRelayParts",
                               ("ChannelId", "{0}"), ("HtlcId", $"{htlcId}"), ("PaymentHash", "{1}"),
                               ("AmountMsat", $"{amount}"), ("CltvExpiry", "800200"),
                               ("OuterSharedSecret", "{2}"), ("TrampolineSharedSecret", "{3}")),
                    [(byte[])channel, (byte[])s_hash, (byte[])SecretOf(0x21), (byte[])SecretOf(0x22)],
                    cancellationToken);
        }

        // Act: the upgrade, then the payer's retry replaces it (the engine's remove + save + add)
        await using (var context = contextFactory())
        {
            await context.Database.MigrateAsync(cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
            Assert.Empty(await new TrampolineRelayDbRepository(context).ListReplacedAttemptsAsync(
                             new TrampolineRelayListQuery(0, 10), cancellationToken));
        }

        await ReplaceAsync(contextFactory, Relay(s_now.AddMinutes(1)), [Part(s_channelA, 3, 1_001_000)],
                           cancellationToken);

        // A second failure, replaced by a third attempt that is fulfilled
        await using (var context = contextFactory())
        {
            var repository = new TrampolineRelayDbRepository(context);
            var second = (await repository.GetAsync(s_hash))!.Value.Relay;
            second.MarkFailed(0x2002, "temporary trampoline failure", s_now.AddMinutes(1).AddSeconds(1));
            await repository.UpdateAsync(second);
            await context.SaveChangesAsync(cancellationToken);
        }

        await ReplaceAsync(contextFactory, Relay(s_now.AddMinutes(2)), [Part(s_channelC, 4, 1_010_000)],
                           cancellationToken);
        await using (var context = contextFactory())
        {
            var repository = new TrampolineRelayDbRepository(context);
            var third = (await repository.GetAsync(s_hash))!.Value.Relay;
            third.MarkSending();
            third.MarkFulfilled(new Secret(Enumerable.Repeat((byte)0x77, 32).ToArray()),
                                LightningMoney.MilliSatoshis(5_000), s_now.AddMinutes(2).AddSeconds(3));
            await repository.UpdateAsync(third);

            // Another hash's failed relay that nobody retried: a relay row, not an attempt
            var other = new TrampolineRelayModel(s_otherHash, s_next, LightningMoney.MilliSatoshis(1_000), 600,
                                                 LightningMoney.MilliSatoshis(1_100), s_now.AddMinutes(3));
            await repository.AddAsync(other);
            await repository.AddPartAsync(new TrampolineRelayPartModel(s_otherHash, s_channelB, 9,
                                                                       LightningMoney.MilliSatoshis(1_100), 700,
                                                                       SecretOf(0x31), SecretOf(0x32), null));
            other.MarkFailed(null, "mpp_timeout", s_now.AddMinutes(4));
            await repository.UpdateAsync(other);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: both replaced attempts kept with what they had, newest first, and filtered like the relays
        await using (var context = contextFactory())
        {
            var repository = new TrampolineRelayDbRepository(context);
            var attempts = await repository.ListReplacedAttemptsAsync(new TrampolineRelayListQuery(0, 10),
                                                                      cancellationToken);
            Assert.Equal([2, 1], attempts.Select(a => a.Attempt));
            var firstAttempt = attempts[1];
            Assert.Equal(s_hash, firstAttempt.PaymentHash);
            Assert.Equal(s_next, firstAttempt.NextNodeId);
            Assert.Equal(LightningMoney.MilliSatoshis(1_000_000), firstAttempt.AmountOut);
            Assert.Equal(800_000u, firstAttempt.CltvExpiryOut);
            Assert.Equal(LightningMoney.MilliSatoshis(1_000_500), firstAttempt.IncomingTotal);
            Assert.Equal(LightningMoney.MilliSatoshis(1_000_500), firstAttempt.IncomingAmount);
            Assert.Equal(2, firstAttempt.Parts);
            Assert.Equal([s_channelA, s_channelB], firstAttempt.IncomingChannelIds);
            Assert.Equal((ushort)0x2019, firstAttempt.FailureCode);
            Assert.Equal("fee or expiry insufficient", firstAttempt.FailureReason);
            Assert.Equal(s_now.UtcTicks, firstAttempt.CreatedAt.UtcTicks);
            Assert.Equal(s_now.AddSeconds(1).UtcTicks, firstAttempt.CompletedAt!.Value.UtcTicks);
            Assert.Equal([s_channelA], attempts[0].IncomingChannelIds);
            Assert.Equal(LightningMoney.MilliSatoshis(1_001_000), attempts[0].IncomingAmount);

            Assert.Equal([1], (await repository.ListReplacedAttemptsAsync(
                                   new TrampolineRelayListQuery(1, 5), cancellationToken)).Select(a => a.Attempt));
            Assert.Equal([1], (await repository.ListReplacedAttemptsAsync(
                                   new TrampolineRelayListQuery(0, 10, IncomingChannelId: s_channelB),
                                   cancellationToken)).Select(a => a.Attempt));
            Assert.Equal([2, 1], (await repository.ListReplacedAttemptsAsync(
                                      new TrampolineRelayListQuery(0, 10, Status: TrampolineRelayStatus.Failed),
                                      cancellationToken)).Select(a => a.Attempt));
            Assert.Empty(await repository.ListReplacedAttemptsAsync(
                             new TrampolineRelayListQuery(0, 10, Status: TrampolineRelayStatus.Fulfilled),
                             cancellationToken));
            Assert.Equal([2], (await repository.ListReplacedAttemptsAsync(
                                   new TrampolineRelayListQuery(0, 10, Since: s_now.AddSeconds(30)),
                                   cancellationToken)).Select(a => a.Attempt));
            Assert.Equal([1], (await repository.ListReplacedAttemptsAsync(
                                   new TrampolineRelayListQuery(0, 10, Until: s_now.AddSeconds(30)),
                                   cancellationToken)).Select(a => a.Attempt));
            Assert.Empty(await repository.ListReplacedAttemptsAsync(new TrampolineRelayListQuery(0, 0),
                                                                    cancellationToken));

            // The hash's relay now is the fulfilled third attempt, alone with its part
            var (relay, parts) = (await repository.GetAsync(s_hash))!.Value;
            Assert.Equal(TrampolineRelayStatus.Fulfilled, relay.Status);
            Assert.Equal([s_channelC], parts.Select(p => p.ChannelId));
            Assert.Null(await repository.GetPartAsync(s_channelA, 1));

            // The totals: one fulfilled relay (5,000 msat) and three failed (the other hash's and two attempts)
            Assert.Equal(new TrampolineRelayTotals(0, 0, 1, 3, 5_000),
                         await repository.SummarizeAsync(new TrampolineRelayListQuery(0, 1), cancellationToken));
            Assert.Equal(new TrampolineRelayTotals(0, 0, 0, 2, 0),
                         await repository.SummarizeAsync(new TrampolineRelayListQuery(0, 1,
                                                                                      IncomingChannelId: s_channelB),
                                                         cancellationToken));
            Assert.Equal(new TrampolineRelayTotals(0, 0, 1, 0, 5_000),
                         await repository.SummarizeAsync(
                             new TrampolineRelayListQuery(0, 1, Status: TrampolineRelayStatus.Fulfilled),
                             cancellationToken));
            Assert.Equal(new TrampolineRelayTotals(0, 0, 1, 1, 5_000),
                         await repository.SummarizeAsync(new TrampolineRelayListQuery(0, 1,
                                                                                      Since: s_now.AddSeconds(30),
                                                                                      Until: s_now.AddMinutes(2)
                                                                                         .AddSeconds(30)),
                                                         cancellationToken));
        }

        await AssertRelayLegsAsync(contextFactory, cancellationToken);
    }

    /// <summary>The engine's replacement: remove the failed relay in one save, then the new relay and its parts.</summary>
    private static async Task ReplaceAsync(Func<NLightningDbContext> contextFactory, TrampolineRelayModel relay,
                                           IReadOnlyList<TrampolineRelayPartModel> parts,
                                           CancellationToken cancellationToken)
    {
        await using var context = contextFactory();
        var repository = new TrampolineRelayDbRepository(context);
        await repository.RemoveFailedAsync(s_hash);
        await context.SaveChangesAsync(cancellationToken);
        Assert.Null(await repository.GetAsync(s_hash));

        await repository.AddAsync(relay);
        foreach (var part in parts)
            await repository.AddPartAsync(part);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>NL-899: the relay legs left out of a listing and counted.</summary>
    private static async Task AssertRelayLegsAsync(Func<NLightningDbContext> contextFactory,
                                                   CancellationToken cancellationToken)
    {
        var ours = new PaymentModel(new Hash(Enumerable.Repeat((byte)0xD1, 32).ToArray()), "lnbcrt1ours", s_next,
                                    LightningMoney.MilliSatoshis(10_000), LightningMoney.MilliSatoshis(7), s_now);
        var leg = new PaymentModel(s_hash, null, s_next, LightningMoney.MilliSatoshis(1_000_000),
                                   LightningMoney.MilliSatoshis(5_000), s_now.AddMinutes(2))
        {
            IsTrampolineRelay = true
        };
        await using (var context = contextFactory())
        {
            var repository = new PaymentDbRepository(context);
            await repository.AddAsync(ours);
            await repository.AddAsync(leg);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new PaymentDbRepository(context);
            Assert.Equal([leg.PaymentHash, ours.PaymentHash],
                         (await repository.ListAsync(0, 10)).Select(p => p.PaymentHash));
            Assert.Equal([leg.PaymentHash, ours.PaymentHash],
                         (await repository.ListAsync(0, 10, true)).Select(p => p.PaymentHash));
            Assert.Equal([ours.PaymentHash], (await repository.ListAsync(0, 10, false)).Select(p => p.PaymentHash));
            Assert.Empty(await repository.ListAsync(1, 10, false));
            Assert.Equal(1, await repository.CountTrampolineRelaysAsync());
        }
    }

    private static TrampolineRelayModel Relay(DateTimeOffset createdAt) =>
        new(s_hash, s_next, LightningMoney.MilliSatoshis(1_000_000), 800_000, LightningMoney.MilliSatoshis(1_000_500),
            createdAt);

    private static TrampolineRelayPartModel Part(ChannelId channelId, ulong htlcId, ulong amountMsat) =>
        new(s_hash, channelId, htlcId, LightningMoney.MilliSatoshis(amountMsat), 800_200, SecretOf(0x21),
            SecretOf(0x22), null);

    private static Secret SecretOf(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());
}
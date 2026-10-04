using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Trampoline;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Payment;

/// <summary>
/// Provider-agnostic proof for migration <c>AddTrampolineRelayBlindedDelta</c> (NL-923), shared by the SQLite test
/// and the Docker Postgres test: a collecting blinded relay seeded on the schema right before it reads back with no
/// kept delta (the completion after a restart then uses the safe upper-bound fallback), and the delta a later part
/// keeps round-trips, only ever upwards.
/// </summary>
internal static class TrampolineRelayBlindedDeltaSchemaRoundTrip
{
    private const string MigrationName = "_AddTrampolineRelayBlindedDelta";

    private static readonly DateTimeOffset s_now = new DateTimeOffset(2026, 10, 4, 8, 30, 15, TimeSpan.FromHours(-4))
       .AddTicks(2_468_012);

    private static readonly Hash s_hash = new(Enumerable.Repeat((byte)0xE7, 32).ToArray());

    private static readonly CompactPubKey s_next =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    private static readonly ChannelId s_channel = new(Enumerable.Repeat((byte)0xD4, 32).ToArray());

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before the migration, with a collecting blinded relay of one part. Raw SQL,
        // because the current model maps the column this older schema does not have
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);

            var sql = new MigrationSqlDialect(databaseType);
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("TrampolineRelays",
                           ("PaymentHash", "{0}"), ("Status", "0"), ("NextNodeId", "{1}"),
                           ("NextEncryptedRecipientData", "{2}"), ("NextPathKey", "{3}"),
                           ("AmountOutMsat", "1000000"), ("CltvExpiryOut", "800000"),
                           ("IncomingTotalMsat", "1000500"), ("CreatedAt", $"{s_now.UtcTicks}")),
                [(byte[])s_hash, (byte[])s_next, Bytes(0x51, 128), Bytes(0x52, 33)], cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("TrampolineRelayParts",
                           ("ChannelId", "{0}"), ("HtlcId", "5"), ("PaymentHash", "{1}"), ("AmountMsat", "1000500"),
                           ("CltvExpiry", "800200"), ("OuterSharedSecret", "{2}"), ("TrampolineSharedSecret", "{3}")),
                [(byte[])s_channel, (byte[])s_hash, Bytes(0x53, 32), Bytes(0x54, 32)], cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: the seeded relay moved forward untouched, a blinded one without the kept delta (the upper bound of
        // the completion after a restart), and the engine's raises round-trip, only ever upwards
        await using (var context = contextFactory())
        {
            var repository = new TrampolineRelayDbRepository(context);
            var seeded = (await repository.GetAsync(s_hash))!.Value.Relay;
            Assert.Equal(TrampolineRelayStatus.Collecting, seeded.Status);
            Assert.Equal(s_next, seeded.NextNodeId);
            Assert.Equal(LightningMoney.MilliSatoshis(1_000_000), seeded.AmountOut);
            Assert.Null(seeded.BlindedKeptCltvExpiryDelta);

            Assert.True(seeded.KeepBlindedDelta(34));
            await repository.UpdateAsync(seeded);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new TrampolineRelayDbRepository(context);
            var kept = (await repository.GetAsync(s_hash))!.Value.Relay;
            Assert.Equal((ushort)34, kept.BlindedKeptCltvExpiryDelta);

            // A later part that paid a smaller delta (a policy change between parts) does not lower it
            Assert.False(kept.KeepBlindedDelta(30));
            Assert.True(kept.KeepBlindedDelta(60));
            await repository.UpdateAsync(kept);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var raised = (await new TrampolineRelayDbRepository(context).GetAsync(s_hash))!.Value.Relay;
            Assert.Equal((ushort)60, raised.BlindedKeptCltvExpiryDelta);
        }
    }

    private static byte[] Bytes(byte seed, int length) =>
        Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();
}

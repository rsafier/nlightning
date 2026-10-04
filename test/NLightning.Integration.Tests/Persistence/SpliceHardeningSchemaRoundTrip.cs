using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.Commitments;
using Domain.Node.Models;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Database.Node;

/// <summary>
/// Provider-agnostic proof of the hand-written Down step of migration <c>AddSpliceHardening</c> (wave spr review),
/// run by the SQLite test and the Docker Postgres/SQL Server tests: the rollback is refused while a remote commitment
/// records the fundings it was signed on (NL-494, the revocation data of discarded and replaced fundings), and once it
/// runs the inbound-only peers (NL-497, rows without an address) are removed, so the older build never dials an empty
/// address; dialable peers are kept.
/// </summary>
internal static class SpliceHardeningSchemaRoundTrip
{
    private const string MigrationName = "_AddSpliceHardening";

    public static async Task AssertDownAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                             CancellationToken cancellationToken)
    {
        // Arrange: the current schema with a channel whose unacked remote commitment records two fundings, an
        // inbound-only peer and a dialable one
        var sql = new MigrationSqlDialect(databaseType);
        var channel = SqliteDbTestContext.CreateChannel(true);
        var inboundOnly = new PeerModel(Key(0x5C), string.Empty, 0, "IPv4")
        {
            LastSeenAt = DateTime.UtcNow,
            IsInboundOnly = true
        };
        var dialable = new PeerModel(Key(0x5D), "10.0.0.7", 9735, "IPv4") { LastSeenAt = DateTime.UtcNow };
        await using (var context = contextFactory())
        {
            await context.GetService<IMigrator>().MigrateAsync(cancellationToken: cancellationToken);
            using var sha256 = new Sha256();
            await new ChannelDbRepository(context, sha256).AddAsync(channel);
            var driver = new CommitmentDanceDriver(channel.ChannelId, CommitmentParams.FromChannel(channel),
                                                   channel.LocalBalance.MilliSatoshi,
                                                   channel.RemoteBalance.MilliSatoshi);
            await new ChannelStateDbRepository(context).InitializeAsync(driver.Us);
            var peers = new PeerDbRepository(context);
            await peers.AddOrUpdateAsync(inboundOnly);
            await peers.AddOrUpdateAsync(dialable);
            await context.SaveChangesAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                sql.Update("Commitments", ("SignedOnFundings", "{0}"), ("ChannelId", "{1}")),
                [new byte[96], (byte[])channel.ChannelId], cancellationToken);
        }

        string previous;
        List<string> later;
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            previous = migrations[migrations.IndexOf(target) - 1];
            later = migrations.Skip(migrations.IndexOf(target) + 1).ToList();

            // Act
            var refusal = await Record.ExceptionAsync(() => context.GetService<IMigrator>()
                                                                     .MigrateAsync(previous, cancellationToken));

            // Assert: refused, nothing dropped
            Assert.NotNull(refusal);
        }

        await using (var context = contextFactory())
        {
            // Migrations roll back one at a time, each in its own transaction: the later ones went, this one refused
            Assert.Equal(later, await context.Database.GetPendingMigrationsAsync(cancellationToken));
            // Only the column this migration owns: later migrations' columns (AddSimpleTaprootChannels) are gone
            Assert.All(await context.Commitments.AsNoTracking()
                                    .Where(c => c.ChannelId == channel.ChannelId)
                                    .Select(c => c.SignedOnFundings)
                                    .ToListAsync(cancellationToken),
                       signedOn => Assert.Equal(96, signedOn!.Length));
            Assert.Equal(2, await context.Peers.CountAsync(cancellationToken));

            // Without the record the rollback runs and drops the inbound-only peer; the migration applies again
            await context.Database.ExecuteSqlRawAsync(
                sql.Update("Commitments", ("SignedOnFundings", "NULL"), ("ChannelId", "{0}")),
                [(byte[])channel.ChannelId], cancellationToken);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(previous, cancellationToken);
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        await using (var context = contextFactory())
        {
            var peer = Assert.Single(await context.Peers.AsNoTracking().ToListAsync(cancellationToken));
            Assert.Equal(dialable.NodeId, peer.NodeId);
            Assert.False(peer.IsInboundOnly);
            Assert.Equal("10.0.0.7", peer.Host);
        }
    }

    private static byte[] Key(byte seed)
    {
        var key = new byte[33];
        key[0] = 0x03;
        key[32] = seed;
        return key;
    }
}
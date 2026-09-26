using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Crypto.ValueObjects;
using Domain.Node.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Node;

/// <summary>
/// Provider-agnostic persistence round trip shared by the Sqlite (in-memory) and the Docker Postgres/SqlServer tests.
/// </summary>
internal static class PersistenceRoundTrip
{
    private static readonly CompactPubKey s_nodeId =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    /// <summary>
    /// Applies all migrations, stores a peer through <see cref="PeerDbRepository"/> using one context and reads it back
    /// through a fresh context.
    /// </summary>
    public static async Task AssertPeerRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                      CancellationToken cancellationToken)
    {
        var lastSeenAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        await using (var migrationContext = contextFactory())
        {
            await migrationContext.Database.MigrateAsync(cancellationToken);
            Assert.Empty(await migrationContext.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        await using (var writeContext = contextFactory())
        {
            var repository = new PeerDbRepository(writeContext);
            await repository.AddOrUpdateAsync(new PeerModel(s_nodeId, "127.0.0.1", 9735, "IPv4")
            {
                LastSeenAt = lastSeenAt
            });
            await writeContext.SaveChangesAsync(cancellationToken);
        }

        await using (var readContext = contextFactory())
        {
            var repository = new PeerDbRepository(readContext);
            var peer = await repository.GetByNodeIdAsync(s_nodeId);

            Assert.NotNull(peer);
            Assert.Equal(s_nodeId, peer.NodeId);
            Assert.Equal("127.0.0.1", peer.Host);
            Assert.Equal(9735U, peer.Port);
            Assert.Equal("IPv4", peer.Type);
            Assert.Equal(lastSeenAt, DateTime.SpecifyKind(peer.LastSeenAt, DateTimeKind.Utc));
            Assert.Single(await repository.GetAllAsync());
        }
    }
}
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Crypto.ValueObjects;
using Domain.Node.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Repositories.Database.Node;

/// <summary>
/// NL-1386: a pong's last-seen update writes only <c>LastSeenAt</c>, so an address another save stored between its read
/// and its save is kept.
/// </summary>
public class PeerLastSeenPersistenceTests
{
    private static readonly CompactPubKey s_nodeId =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    [Fact]
    public async Task Given_AnAddressSavedAfterTheLastSeenRead_When_TheLastSeenIsSaved_Then_TheAddressIsKept()
    {
        // Arrange: an inbound-only row
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                     .Options;
        var databaseTypeProvider = new DatabaseTypeProvider(DatabaseType.Sqlite);
        await using (var context = new NLightningDbContext(options, databaseTypeProvider))
        {
            await context.Database.MigrateAsync(ct);
            await new PeerDbRepository(context).AddOrUpdateAsync(new PeerModel(s_nodeId, string.Empty, 0, "IPv4")
            {
                IsInboundOnly = true
            });
            await context.SaveChangesAsync(ct);
        }

        // Act: the pong handler reads the row, a dial saves the peer's address, then the pong handler saves
        await using var pongContext = new NLightningDbContext(options, databaseTypeProvider);
        await new PeerDbRepository(pongContext).UpdatePeerLastSeenAsync(s_nodeId);
        await using (var dialContext = new NLightningDbContext(options, databaseTypeProvider))
        {
            await new PeerDbRepository(dialContext).AddOrUpdateAsync(new PeerModel(s_nodeId, "203.0.113.7", 9735,
                                                                                    "IPv4"));
            await dialContext.SaveChangesAsync(ct);
        }

        await pongContext.SaveChangesAsync(ct);

        // Assert
        await using var readContext = new NLightningDbContext(options, databaseTypeProvider);
        var peer = await new PeerDbRepository(readContext).GetByNodeIdAsync(s_nodeId);
        Assert.NotNull(peer);
        Assert.False(peer.IsInboundOnly);
        Assert.Equal("203.0.113.7", peer.Host);
        Assert.Equal(9735U, peer.Port);
        Assert.True(peer.LastSeenAt > DateTime.UtcNow.AddMinutes(-1));
    }
}
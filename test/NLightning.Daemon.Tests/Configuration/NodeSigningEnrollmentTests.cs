using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NLightning.Daemon.Extensions;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Signing;
using NLightning.Infrastructure.Persistence.Contexts;
using NLightning.Infrastructure.Persistence.Entities.Bitcoin;
using NLightning.Infrastructure.Persistence.Entities.Node;
using NLightning.Infrastructure.Persistence.Enums;
using NLightning.Infrastructure.Persistence.Providers;

namespace NLightning.Daemon.Tests.Configuration;

public sealed class NodeSigningEnrollmentTests
{
    private static readonly CompactPubKey s_identity = new(new Key().PubKey.ToBytes());
    private static NodeSigningContext Context() => new("node-a", "owner-a", "signer-a", "regtest", s_identity);

    [Fact]
    public async Task FreshDatabaseEnrollsOnceAndSameContextCanRestart()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using (var database = fixture.Open())
            await NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(database, Context(), TestContext.Current.CancellationToken);
        await using var restarted = fixture.Open();
        var original = await restarted.Set<NodeSigningEnrollmentEntity>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        await NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(restarted, Context(), TestContext.Current.CancellationToken);
        var restored = await restarted.Set<NodeSigningEnrollmentEntity>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(original.CreatedAtTicks, restored.CreatedAtTicks);
        Assert.Equal(((byte[])s_identity), restored.NodePublicKey);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("owner")]
    [InlineData("signer")]
    [InlineData("network")]
    [InlineData("identity")]
    public async Task ExistingEnrollmentRejectsChangedAuthorityBeforeMutation(string change)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var database = fixture.Open();
        var original = Context();
        await NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(database, original, TestContext.Current.CancellationToken);
        var changed = change switch
        {
            "node" => original with { NodeId = "node-b" },
            "owner" => original with { OwnerId = "owner-b" },
            "signer" => original with { SignerId = "signer-b" },
            "network" => original with { Network = "testnet" },
            _ => new NodeSigningContext(original.NodeId, original.OwnerId, original.SignerId, original.Network,
                new CompactPubKey(new Key().PubKey.ToBytes()))
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(database, changed, TestContext.Current.CancellationToken));
        var enrolled = await database.Set<NodeSigningEnrollmentEntity>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(original.NodeId, enrolled.NodeId);
        Assert.Equal(original.OwnerId, enrolled.OwnerId);
        Assert.Equal(original.SignerId, enrolled.SignerId);
        Assert.Equal(original.Network, enrolled.Network);
        Assert.Equal((byte[])original.NodePublicKey, enrolled.NodePublicKey);
        Assert.DoesNotContain(database.ChangeTracker.Entries(),
            entry => entry.State is EntityState.Added or EntityState.Modified);
    }

    [Fact]
    public async Task CopiedDatabaseRetainsItsOriginalOwnerEnrollment()
    {
        await using var source = await DatabaseFixture.CreateAsync();
        await using (var database = source.Open())
            await NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(database, Context(), TestContext.Current.CancellationToken);
        await using var copy = await DatabaseFixture.CopyAsync(source);
        await using var copiedDatabase = copy.Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(copiedDatabase,
                Context() with { OwnerId = "owner-b" }, TestContext.Current.CancellationToken));
        Assert.Equal("owner-a", (await copiedDatabase.Set<NodeSigningEnrollmentEntity>().SingleAsync(TestContext.Current.CancellationToken)).OwnerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WalletStateCannotBeAdoptedWithoutEnrollment(bool enrollmentWasDeleted)
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var database = fixture.Open();
        if (enrollmentWasDeleted)
            await NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(database, Context(), TestContext.Current.CancellationToken);
        database.WalletAccounts.Add(new WalletAccountEntity { Name = "customer-wallet" });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (enrollmentWasDeleted)
            await database.Set<NodeSigningEnrollmentEntity>().ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(database, Context(), TestContext.Current.CancellationToken));
        Assert.Empty(await database.Set<NodeSigningEnrollmentEntity>().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("customer-wallet", (await database.WalletAccounts.SingleAsync(TestContext.Current.CancellationToken)).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ResidualWalletRescan_When_EnrollmentIsMissing_Then_NewOwnerCannotAdoptHistory(bool enrollmentWasDeleted)
    {
        // Arrange: scan recovery can remain after wallet accounts and outputs have been removed.
        await using var fixture = await DatabaseFixture.CreateAsync();
        await using var database = fixture.Open();
        if (enrollmentWasDeleted)
            await NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(database, Context(),
                TestContext.Current.CancellationToken);
        var generation = Guid.NewGuid();
        database.WalletHistoryRescanStates.Add(new WalletHistoryRescanStateEntity
        {
            Generation = generation,
            RequestedFromHeight = 100,
            TargetHeight = 200,
            IsActive = true
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (enrollmentWasDeleted)
            await database.NodeSigningEnrollments.ExecuteDeleteAsync(TestContext.Current.CancellationToken);

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(database,
                Context() with { OwnerId = "owner-b" }, TestContext.Current.CancellationToken));

        // Assert: enrollment refusal leaves the original recovery history untouched.
        Assert.Empty(await database.NodeSigningEnrollments.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(generation,
            (await database.WalletHistoryRescanStates.SingleAsync(TestContext.Current.CancellationToken)).Generation);
        Assert.DoesNotContain(database.ChangeTracker.Entries(),
            entry => entry.State is EntityState.Added or EntityState.Modified);
    }

    private sealed class DatabaseFixture(string path) : IAsyncDisposable
    {
        private string PathName => path;
        public NLightningDbContext Open() => new(new DbContextOptionsBuilder<NLightningDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options, new DatabaseTypeProvider(DatabaseType.Sqlite));
        public static async Task<DatabaseFixture> CreateAsync()
        {
            var fixture = new DatabaseFixture(Path.Combine(Path.GetTempPath(), $"nltg-enrollment-{Guid.NewGuid():N}.db"));
            await using var database = fixture.Open();
            await database.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return fixture;
        }
        public static Task<DatabaseFixture> CopyAsync(DatabaseFixture original)
        {
            var copyPath = Path.Combine(Path.GetTempPath(), $"nltg-enrollment-copy-{Guid.NewGuid():N}.db");
            File.Copy(original.PathName, copyPath);
            return Task.FromResult(new DatabaseFixture(copyPath));
        }
        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            return ValueTask.CompletedTask;
        }
    }
}
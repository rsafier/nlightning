using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Protocol.InteractiveTx.Enums;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Memory;

/// <summary>
/// Interactive-tx negotiations (splicing plan IT3-T2, migration <c>AddInteractiveTxSessions</c>) on the real SQLite
/// schema.
/// </summary>
public class InteractiveTxSessionPersistenceTests
{
    private static readonly DateTimeOffset s_createdAt =
        new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero).AddTicks(42);

    [Fact]
    public async Task Given_SchemaFromBeforeAddInteractiveTxSessions_When_Migrated_Then_SessionsRoundTrip()
    {
        // Arrange (the SQLite run of the round trip the Docker Postgres test shares)
        await using var connection = await OpenConnectionAsync();
        var options = Options(connection);

        // Act & Assert
        await InteractiveTxSessionSchemaRoundTrip.AssertAsync(
            () => new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.Sqlite)),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_TheUnitOfWork_When_ASessionIsAddedAndSaved_Then_ANewUnitOfWorkReadsEveryField()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var options = await MigratedOptionsAsync(connection);
        var session = InteractiveTxSessionSchemaRoundTrip.FullSession(
            InteractiveTxSessionSchemaRoundTrip.ChannelIdOf(0x31), s_createdAt);

        // Act
        using (var unitOfWork = CreateUnitOfWork(options))
        {
            unitOfWork.InteractiveTxSessionDbRepository.Add(session);
            await unitOfWork.SaveChangesAsync();
        }

        // Assert
        using (var unitOfWork = CreateUnitOfWork(options))
        {
            InteractiveTxSessionSchemaRoundTrip.AssertSessionEqual(
                session,
                await unitOfWork.InteractiveTxSessionDbRepository.GetByIdAsync(session.ChannelId, session.SessionId));
        }
    }

    [Fact]
    public async Task Given_ACrashAtTheSaveThatStagesASession_When_Reopened_Then_NoRowWasWritten()
    {
        // Arrange (crash injection through CrashingUnitOfWork, as the driver's "CS sent" save will be tested)
        await using var connection = await OpenConnectionAsync();
        var options = await MigratedOptionsAsync(connection);
        var session = InteractiveTxSessionSchemaRoundTrip.MinimalSession(
            InteractiveTxSessionSchemaRoundTrip.ChannelIdOf(0x32), s_createdAt);

        // Act
        using (var crashing = new CrashingUnitOfWork(CreateUnitOfWork(options), crashAtSave: 1))
        {
            crashing.InteractiveTxSessionDbRepository.Add(session);
            await Assert.ThrowsAsync<SimulatedCrashException>(() => crashing.SaveChangesAsync());
            Assert.True(crashing.HasCrashed);
        }

        // Assert
        using var reopened = CreateUnitOfWork(options);
        Assert.Null(await reopened.InteractiveTxSessionDbRepository.GetByIdAsync(session.ChannelId, session.SessionId));
        Assert.Empty(await reopened.InteractiveTxSessionDbRepository.GetUnresolvedAsync());
    }

    [Fact]
    public async Task Given_ASavedSessionAndACrashAtItsUpdate_When_Reopened_Then_TheSavedVersionIsKept()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var options = await MigratedOptionsAsync(connection);
        var session = InteractiveTxSessionSchemaRoundTrip.MinimalSession(
            InteractiveTxSessionSchemaRoundTrip.ChannelIdOf(0x33), s_createdAt);

        using (var crashing = new CrashingUnitOfWork(CreateUnitOfWork(options), crashAtSave: 2))
        {
            crashing.InteractiveTxSessionDbRepository.Add(session);
            await crashing.SaveChangesAsync();

            // Act
            await crashing.InteractiveTxSessionDbRepository.UpdateAsync(session with
            {
                TxSignaturesSent = true,
                State = InteractiveTxSessionState.TxSignaturesSent
            });
            await Assert.ThrowsAsync<SimulatedCrashException>(() => crashing.SaveChangesAsync());
        }

        // Assert
        using var reopened = CreateUnitOfWork(options);
        InteractiveTxSessionSchemaRoundTrip.AssertSessionEqual(
            session,
            await reopened.InteractiveTxSessionDbRepository.GetByIdAsync(session.ChannelId, session.SessionId));
    }

    [Theory]
    [InlineData("Inputs", "02")] // unknown format version
    [InlineData("Inputs", "0100000001")] // count 1, no input
    [InlineData("Outputs", "010000000000")] // trailing byte
    [InlineData("LocalContribution", "")] // no version byte
    [InlineData("ConstructedTx", "01")] // truncated
    [InlineData("OurWitnesses", "0100000001000000FF")] // witness longer than the blob
    public async Task Given_ACorruptBlob_When_TheSessionIsRead_Then_InvalidOperationException(string column,
        string hex)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var options = await MigratedOptionsAsync(connection);
        var session = InteractiveTxSessionSchemaRoundTrip.FullSession(
            InteractiveTxSessionSchemaRoundTrip.ChannelIdOf(0x34), s_createdAt);
        await using (var context = CreateContext(options))
        {
            new InteractiveTxSessionDbRepository(context).Add(session);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            // The column name is one of the fixed values above
            command.CommandText = $"UPDATE InteractiveTxSessions SET \"{column}\" = $blob";
            command.Parameters.AddWithValue("$blob", Convert.FromHexString(hex));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Act & Assert
        await using var readContext = CreateContext(options);
        var repository = new InteractiveTxSessionDbRepository(readContext);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetUnresolvedAsync());
    }

    [Theory]
    [InlineData("Purpose", 9)]
    [InlineData("State", 6)]
    public async Task Given_AnUnknownEnumValue_When_TheSessionIsRead_Then_InvalidOperationException(string column,
        int value)
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var options = await MigratedOptionsAsync(connection);
        var session = InteractiveTxSessionSchemaRoundTrip.MinimalSession(
            InteractiveTxSessionSchemaRoundTrip.ChannelIdOf(0x35), s_createdAt);
        await using (var context = CreateContext(options))
        {
            new InteractiveTxSessionDbRepository(context).Add(session);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"UPDATE InteractiveTxSessions SET \"{column}\" = $value";
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // Act & Assert
        await using var readContext = CreateContext(options);
        var repository = new InteractiveTxSessionDbRepository(readContext);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.GetByIdAsync(session.ChannelId,
                                                                session.SessionId));
    }

    [Fact]
    public async Task Given_SavedAndStagedSessionsOfTwoChannels_When_DeletedByChannelId_Then_OnlyThatChannelsRowsGo()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var options = await MigratedOptionsAsync(connection);
        var forgotten = InteractiveTxSessionSchemaRoundTrip.ChannelIdOf(0x41);
        var kept = InteractiveTxSessionSchemaRoundTrip.ChannelIdOf(0x42);
        var savedA = InteractiveTxSessionSchemaRoundTrip.FullSession(forgotten, s_createdAt);
        var savedB = InteractiveTxSessionSchemaRoundTrip.MinimalSession(forgotten, s_createdAt.AddSeconds(1)) with
        {
            SessionId = Guid.NewGuid()
        };
        var other = InteractiveTxSessionSchemaRoundTrip.MinimalSession(kept, s_createdAt);
        using (var unitOfWork = CreateUnitOfWork(options))
        {
            unitOfWork.InteractiveTxSessionDbRepository.Add(savedA);
            unitOfWork.InteractiveTxSessionDbRepository.Add(savedB);
            unitOfWork.InteractiveTxSessionDbRepository.Add(other);
            await unitOfWork.SaveChangesAsync();
        }

        int deleted;
        using (var unitOfWork = CreateUnitOfWork(options))
        {
            var staged = InteractiveTxSessionSchemaRoundTrip.MinimalSession(forgotten, s_createdAt.AddSeconds(2)) with
            {
                SessionId = Guid.NewGuid()
            };
            unitOfWork.InteractiveTxSessionDbRepository.Add(staged);

            // Act
            deleted = await unitOfWork.InteractiveTxSessionDbRepository.DeleteByChannelIdAsync(forgotten);
            await unitOfWork.SaveChangesAsync();
        }

        // Assert
        Assert.Equal(3, deleted);
        using var reopened = CreateUnitOfWork(options);
        Assert.Empty(await reopened.InteractiveTxSessionDbRepository.GetByChannelIdAsync(forgotten));
        var unresolved = Assert.Single(await reopened.InteractiveTxSessionDbRepository.GetUnresolvedAsync());
        InteractiveTxSessionSchemaRoundTrip.AssertSessionEqual(other, unresolved);
        Assert.Equal(0, await reopened.InteractiveTxSessionDbRepository.DeleteByChannelIdAsync(forgotten));
    }

    [Fact]
    public async Task Given_ADefaultWitness_When_TheSessionIsAdded_Then_ArgumentExceptionAndNothingIsStaged()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        var options = await MigratedOptionsAsync(connection);
        var session = InteractiveTxSessionSchemaRoundTrip.MinimalSession(
            InteractiveTxSessionSchemaRoundTrip.ChannelIdOf(0x43), s_createdAt) with
        {
            OurWitnesses = [default]
        };
        using var unitOfWork = CreateUnitOfWork(options);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => unitOfWork.InteractiveTxSessionDbRepository.Add(session));
        await unitOfWork.SaveChangesAsync();
        Assert.Null(await unitOfWork.InteractiveTxSessionDbRepository.GetByIdAsync(session.ChannelId,
                                                                                    session.SessionId));
    }

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static DbContextOptions<NLightningDbContext> Options(SqliteConnection connection) =>
        new DbContextOptionsBuilder<NLightningDbContext>()
           .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
           .Options;

    private static async Task<DbContextOptions<NLightningDbContext>> MigratedOptionsAsync(SqliteConnection connection)
    {
        var options = Options(connection);
        await using var context = CreateContext(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        return options;
    }

    private static NLightningDbContext CreateContext(DbContextOptions<NLightningDbContext> options) =>
        new(options, new DatabaseTypeProvider(DatabaseType.Sqlite));

    private static UnitOfWork CreateUnitOfWork(DbContextOptions<NLightningDbContext> options) =>
        new(CreateContext(options), new Mock<ILogger<UnitOfWork>>().Object, new ThreadLocalSha256(),
            new UtxoMemoryRepository());
}
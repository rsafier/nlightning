using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;

public sealed class DanglingSchemaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_FreshOrLegacySqlite_When_DanglingSchemaMigrates_Then_CompiledModelRestartsKeepEveryNewFact(bool upgrade)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<NLightningDbContext>().UseSqlite(connection,
            builder => builder.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite")).Options;
        var compiled = new DbContextOptionsBuilder<NLightningDbContext>(options)
            .UseModel(Infrastructure.Persistence.CompiledModels.Sqlite.NLightningDbContextModel.Instance).Options;
        var provider = new DatabaseTypeProvider(DatabaseType.Sqlite);
        await DanglingSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, provider),
            () => new NLightningDbContext(compiled, provider), DatabaseType.Sqlite, upgrade, ct);
    }
}
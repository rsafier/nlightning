using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Docker;

using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Persistence;

public partial class PostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_FreshOrLegacyPostgres_When_DanglingSchemaMigrates_Then_CompiledModelRestartsKeepEveryNewFact(bool upgrade)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = await CreateOwnDatabaseOptionsAsync(upgrade ? "nltg_dangling_upgrade" : "nltg_dangling_fresh");
        var compiled = new DbContextOptionsBuilder<NLightningDbContext>(options)
            .UseModel(Infrastructure.Persistence.CompiledModels.Postgres.NLightningDbContextModel.Instance).Options;
        var provider = new DatabaseTypeProvider(DatabaseType.PostgreSql);
        await DanglingSchemaRoundTrip.AssertAsync(() => new NLightningDbContext(options, provider),
            () => new NLightningDbContext(compiled, provider), DatabaseType.PostgreSql, upgrade, ct);
        Console.WriteLine($"dangling PostgreSQL schema {(upgrade ? "upgrade" : "fresh")}: compiled model restart and legacy defaults proved");
    }
}
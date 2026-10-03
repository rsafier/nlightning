using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Domain.Enums;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.CompiledModels;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Entities.Channel;
using Infrastructure.Repositories.Database.Channel;

/// <summary>
/// The compiled models (NL-708) must match the model <c>OnModelCreating</c> builds, provider by provider: a NativeAOT
/// node runs on them. When one of these tests fails, run
/// <c>src/NLightning.Infrastructure.Persistence/scripts/optimize_model.sh</c> (no database needed).
/// </summary>
public class CompiledModelTests
{
    public static TheoryData<string, string> Providers => new()
    {
        { "postgres", "Host=localhost;Database=nlightning" },
        { "sqlite", "Data Source=:memory:" },
        { "sqlserver", "Server=localhost;Database=nlightning" }
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public void Given_TheCompiledModel_When_ComparedToTheRuntimeModel_Then_TheyDescribeTheSameModel(
        string provider, string connectionString)
    {
        // Arrange
        using var runtime = OpenContext(provider, connectionString, useCompiledModel: false);
        using var compiled = OpenContext(provider, connectionString, useCompiledModel: true);

        // Act
        var runtimeModel = runtime.Context.Model.ToDebugString(MetadataDebugStringOptions.LongDefault);
        var compiledModel = compiled.Context.Model.ToDebugString(MetadataDebugStringOptions.LongDefault);

        // Assert
        Assert.NotSame(runtime.Context.Model, compiled.Context.Model);
        AssertSameText(runtimeModel, compiledModel, provider);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Given_TheCompiledModel_When_ItsSchemaIsComparedToTheRuntimeModel_Then_TablesColumnsAndKeysMatch(
        string provider, string connectionString)
    {
        // Arrange
        using var runtime = OpenContext(provider, connectionString, useCompiledModel: false);
        using var compiled = OpenContext(provider, connectionString, useCompiledModel: true);

        // Act (tables, columns with their store types, keys, indexes and foreign keys)
        var runtimeSchema = runtime.Context.Model.GetRelationalModel()
                                   .ToDebugString(MetadataDebugStringOptions.LongDefault);
        var compiledSchema = WithoutCompiledOnlyAnnotations(compiled.Context.Model.GetRelationalModel()
                                                                    .ToDebugString(MetadataDebugStringOptions.LongDefault));

        // Assert
        AssertSameText(runtimeSchema, compiledSchema, provider);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Given_TheCompiledModel_When_ItsPropertiesAreComparedToTheRuntimeModel_Then_TheyMapAndConvertAlike(
        string provider, string connectionString)
    {
        // Arrange
        using var runtime = OpenContext(provider, connectionString, useCompiledModel: false);
        using var compiled = OpenContext(provider, connectionString, useCompiledModel: true);

        // Act (what the debug strings leave out: converters, comparers and sentinels)
        var runtimeProperties = DescribeProperties(runtime.Context.Model);
        var compiledProperties = DescribeProperties(compiled.Context.Model);

        // Assert
        Assert.True(runtimeProperties.Count > 300);
        AssertSameText(string.Join('\n', runtimeProperties), string.Join('\n', compiledProperties), provider);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Given_TheCompiledModel_When_ItsConverterAndComparerBodiesAreComparedToTheRuntimeModel_Then_TheyMatch(
        string provider, string connectionString)
    {
        // Arrange
        using var runtime = OpenContext(provider, connectionString, useCompiledModel: false);
        using var compiled = OpenContext(provider, connectionString, useCompiledModel: true);

        // Act (the compiled models carry copies of the lambdas: a changed body shows only here)
        var runtimeBodies = DescribeBodies(runtime.Context.Model);
        var compiledBodies = DescribeBodies(compiled.Context.Model);

        // Assert
        Assert.Contains(runtimeBodies, b => b.Contains("ChannelEntity.ChannelId converter", StringComparison.Ordinal));
        AssertSameText(string.Join('\n', runtimeBodies), string.Join('\n', compiledBodies), provider);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void Given_TheCompiledModel_When_ListingItsValueTypes_Then_NativeAotCanCreateEachDefaultComparer(
        string provider, string connectionString)
    {
        // Arrange
        using var compiled = OpenContext(provider, connectionString, useCompiledModel: true);
        var rooted = CompiledModelCatalog.RootedComparerTypes.ToHashSet();

        // Act
        var missing = compiled.Context.Model.GetEntityTypes()
                              .SelectMany(entityType => entityType.GetProperties())
                              .Select(property => Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType)
                              .Where(type => type.IsValueType && !rooted.Contains(type))
                              .Select(type => type.Name)
                              .Distinct()
                              .Order()
                              .ToList();

        // Assert: add them to CompiledModelCatalog's comparer roots
        Assert.Empty(missing);
    }

    [Fact]
    public void Given_ThePersistenceAssembly_When_LookingForAutomaticallyDiscoveredModels_Then_ThereAreNone()
    {
        // Arrange: three providers' models live in one assembly, so none may be picked up for every
        // NLightningDbContext (optimize_model.sh deletes the generated DbContextModel assembly attribute)
        var assembly = typeof(NLightningDbContext).Assembly;

        // Act
        var attributes = assembly.GetCustomAttributes<DbContextModelAttribute>().ToList();

        // Assert
        Assert.Empty(attributes);
    }

    [Theory]
    [InlineData(null, true, false)]
    [InlineData(null, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void Given_TheSetting_When_DecidingOnTheCompiledModel_Then_NativeAotUsesItUnlessTurnedOff(
        bool? configured, bool dynamicCodeSupported, bool expected)
    {
        // Act
        var actual = CompiledModelCatalog.ShouldUse(configured, dynamicCodeSupported);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Given_AnInvalidSetting_When_RegisteringPersistence_Then_ItIsRefused()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = "sqlite",
                               ["Database:ConnectionString"] = "Data Source=:memory:",
                               ["Database:UseCompiledModel"] = "yes"
                           })
                           .Build();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddPersistenceInfrastructureServices(configuration));

        // Assert
        Assert.Contains("Database:UseCompiledModel", exception.Message);
    }

    [Fact]
    public async Task Given_TheSqliteCompiledModel_When_RoundTrippingAChannelGraph_Then_RowsAndReloadMatchTheRuntimeModel()
    {
        // Act: the same channel (value-object keys and foreign keys: config, key sets, local aliases) added, updated
        // and reloaded in fresh contexts, once on the compiled model as a NativeAOT node uses it and once on the
        // runtime model, each on its own migrated SQLite file
        var (compiledRows, compiledReload) = await RoundTripChannelAsync(useCompiledModel: true);
        var (runtimeRows, runtimeReload) = await RoundTripChannelAsync(useCompiledModel: false);

        // Assert: the update reached every table, and both models wrote and read the same
        Assert.Contains("ChannelLocalAliases: Alias=4C4B400000040004", compiledRows, StringComparison.Ordinal);
        Assert.DoesNotContain("Alias=4C4B400000010001", compiledRows, StringComparison.Ordinal);
        Assert.Contains("ShortChannelId=700000x5x1", compiledReload, StringComparison.Ordinal);
        Assert.Equal(runtimeRows, compiledRows);
        Assert.Equal(runtimeReload, compiledReload);
    }

    private static async Task<(string Rows, string Reloaded)> RoundTripChannelAsync(bool useCompiledModel)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = Path.Combine(Path.GetTempPath(), $"nltg-compiled-model-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        var sha256 = new Sha256();
        var channel = SqliteDbTestContext.CreateChannel(true, useScidAlias: FeatureSupport.Optional);
        channel.LocalAliases = [new ShortChannelId(5_000_000, 1, 1), new ShortChannelId(5_000_000, 2, 2)];
        try
        {
            using (var opened = OpenContext("sqlite", connectionString, useCompiledModel))
            {
                Assert.Equal(useCompiledModel,
                             ReferenceEquals(CompiledModelCatalog.Get(Infrastructure.Persistence.Enums.DatabaseType.Sqlite),
                                             opened.Context.Model));
                await opened.Context.Database.MigrateAsync(cancellationToken);
                await new ChannelDbRepository(opened.Context, sha256).AddAsync(channel);
                await opened.Context.SaveChangesAsync(cancellationToken);
            }

            using (var opened = OpenContext("sqlite", connectionString, useCompiledModel))
            {
                var repository = new ChannelDbRepository(opened.Context, sha256);
                var loaded = await repository.GetByIdAsync(channel.ChannelId)
                          ?? throw new InvalidOperationException("The channel was not loaded");
                loaded.ShortChannelId = new ShortChannelId(700_000, 5, 1);
                loaded.LocalAliases = [new ShortChannelId(5_000_000, 2, 2), new ShortChannelId(5_000_000, 4, 4)];
                await repository.UpdateAsync(loaded);
                await opened.Context.SaveChangesAsync(cancellationToken);
            }

            using (var opened = OpenContext("sqlite", connectionString, useCompiledModel))
            {
                var reloaded = await opened.Context.Channels.AsNoTracking()
                                           .Include(c => c.Config)
                                           .Include(c => c.KeySets)
                                           .Include(c => c.LocalAliases)
                                           .SingleAsync(cancellationToken);
                var described = new List<string> { DescribeEntity(reloaded), DescribeEntity(reloaded.Config!) };
                described.AddRange(reloaded.KeySets!.OrderBy(k => k.IsLocal).Select(DescribeEntity));
                described.AddRange(reloaded.LocalAliases!.Select(DescribeEntity).Order(StringComparer.Ordinal));

                Type[] tables =
                [
                    typeof(ChannelEntity), typeof(ChannelConfigEntity), typeof(ChannelKeySetEntity),
                    typeof(ChannelLocalAliasEntity)
                ];
                var rows = new List<string>();
                var connection = opened.Context.Database.GetDbConnection();
                await connection.OpenAsync(cancellationToken);
                foreach (var table in tables.Select(t => opened.Context.Model.FindEntityType(t)!.GetTableName()!))
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT * FROM \"{table}\"";
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    var tableRows = new List<string>();
                    while (await reader.ReadAsync(cancellationToken))
                        tableRows.Add(string.Join(", ", Enumerable.Range(0, reader.FieldCount)
                                                                  .Select(i => $"{reader.GetName(i)}="
                                                                             + Format(reader.GetValue(i)))));
                    rows.AddRange(tableRows.Order(StringComparer.Ordinal).Select(r => $"{table}: {r}"));
                }

                return (string.Join('\n', rows), string.Join('\n', described));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The entity's scalar properties (navigations left out), byte arrays in hex
    private static string DescribeEntity(object entity)
        => entity.GetType().Name + " " + string.Join(", ", entity.GetType().GetProperties()
                                                                .Where(p => p.PropertyType.IsValueType
                                                                         || p.PropertyType == typeof(string)
                                                                         || p.PropertyType == typeof(byte[]))
                                                                .OrderBy(p => p.Name, StringComparer.Ordinal)
                                                                .Select(p => $"{p.Name}={Format(p.GetValue(entity))}"));

    private static string Format(object? value) => value switch
    {
        null or DBNull => "null",
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null"
    };

    private static void AssertSameText(string expected, string actual, string provider)
    {
        if (expected == actual)
            return;

        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var line = 0;
        while (line < expectedLines.Length && line < actualLines.Length && expectedLines[line] == actualLines[line])
            line++;

        Assert.Fail($"{provider}: the compiled model differs from the runtime model at line {line + 1}: runtime "
                  + $"'{Line(expectedLines, line)}', compiled '{Line(actualLines, line)}'. "
                  + "Run src/NLightning.Infrastructure.Persistence/scripts/optimize_model.sh.");
    }

    private static List<string> DescribeProperties(IModel model)
        => model.GetEntityTypes()
                .SelectMany(entityType => entityType.GetProperties())
                .Select(property =>
                 {
                     var mapping = property.GetTypeMapping();
                     var converter = mapping.Converter;
                     return $"{property.DeclaringType.Name}.{property.Name}: {property.ClrType.Name} "
                          + $"store={(mapping as RelationalTypeMapping)?.StoreType} provider={converter?.ProviderClrType.Name} "
                          + $"model={converter?.ModelClrType.Name} nullable={property.IsNullable} "
                          + $"generated={property.ValueGenerated} concurrency={property.IsConcurrencyToken} "
                          + $"comparer={property.GetValueComparer().Type.Name} "
                          + $"keyComparer={property.GetKeyValueComparer().Type.Name} "
                          + $"sentinel={DescribeSentinel(property)}";
                 })
                .Order(StringComparer.Ordinal)
                .ToList();

    /// <summary>
    /// The bodies of every property's converter (both directions) and comparers (equality, hash code, snapshot), with
    /// the lambdas' parameters renamed by position: the generator names them after the source or after EF's defaults.
    /// </summary>
    private static List<string> DescribeBodies(IModel model)
        => model.GetEntityTypes()
                .SelectMany(entityType => entityType.GetProperties())
                .SelectMany(property =>
                 {
                     var name = $"{property.DeclaringType.Name}.{property.Name}";
                     var lines = new List<string>();
                     if (property.GetTypeMapping().Converter is { } converter)
                         lines.Add($"{name} converter {Body(converter.ConvertToProviderExpression)} | "
                                 + Body(converter.ConvertFromProviderExpression));
                     lines.Add($"{name} comparer {Bodies(property.GetValueComparer())}");
                     lines.Add($"{name} keyComparer {Bodies(property.GetKeyValueComparer())}");
                     return lines;
                 })
                .Order(StringComparer.Ordinal)
                .ToList();

    private static string Bodies(ValueComparer comparer)
        => $"{Body(comparer.EqualsExpression)} | {Body(comparer.HashCodeExpression)} | "
         + Body(comparer.SnapshotExpression);

    /// <summary>
    /// A lambda's body with its parameters renamed by position, in the shape the generator writes it: the C# it emits
    /// boxes a value for <c>GetHashCode</c> (<c>((object)v).GetHashCode()</c>), compares small integers as
    /// <c>int</c> and writes a <c>DateTime</c> <c>==</c> as <c>Equals</c>; none of these changes what the lambda does.
    /// </summary>
    private static string Body(LambdaExpression lambda)
    {
        var body = new ParameterRenamer(lambda.Parameters).Visit(lambda.Body).ToString();
        body = s_boxedHashCode.Replace(body, "$1.GetHashCode()");
        body = s_promotedEquality.Replace(body, "$1 == $2");
        return s_dateTimeEquality.Replace(body, "$1.Equals($2)");
    }

    private const string Operand = @"(p\d+|Convert\(p\d+, [\w`]+\))";
    private static readonly Regex s_boxedHashCode = new($@"Convert\({Operand}, Object\)\.GetHashCode\(\)");
    private static readonly Regex s_promotedEquality = new($@"Convert\({Operand}, Int32\) == Convert\({Operand}, Int32\)");
    private static readonly Regex s_dateTimeEquality =
        new(@"\((Convert\(p\d+, DateTime\)) == (Convert\(p\d+, DateTime\))\)");

    private sealed class ParameterRenamer(IReadOnlyList<ParameterExpression> parameters) : ExpressionVisitor
    {
        private readonly Dictionary<ParameterExpression, ParameterExpression> _renamed =
            parameters.Select((p, i) => (p, Expression.Parameter(p.Type, $"p{i}"))).ToDictionary(x => x.p, x => x.Item2);

        protected override Expression VisitParameter(ParameterExpression node)
            => _renamed.TryGetValue(node, out var renamed) ? renamed : node;
    }

    /// <summary>
    /// The compiled model of a non-nullable byte-backed value object (<c>Hash</c>, <c>ChannelId</c>, ...) has no
    /// sentinel where the runtime model has <c>default</c> (its converter turns that into null; see
    /// <c>ValueObjectCSharpHelper</c> in NLightning.Infrastructure.Persistence.Design), so both read as "default".
    /// </summary>
    private static string DescribeSentinel(IProperty property)
    {
        var sentinel = property.Sentinel;
        if (sentinel is null)
            return property.ClrType.IsValueType && Nullable.GetUnderlyingType(property.ClrType) is null
                       ? "default"
                       : "null";

        return sentinel.Equals(Activator.CreateInstance(sentinel.GetType())) ? "default" : sentinel.ToString()!;
    }

    /// <summary>
    /// Npgsql's compiled model keeps <c>Npgsql:ValueGenerationStrategy</c> on its identity columns, which the runtime
    /// model's relational view drops; the identity itself is compared on the properties (ValueGenerated.OnAdd).
    /// </summary>
    private static string WithoutCompiledOnlyAnnotations(string schema)
    {
        var lines = schema.Split('\n').ToList();
        for (var i = lines.Count - 1; i > 0; i--)
        {
            if (!lines[i].TrimStart().StartsWith("Npgsql:ValueGenerationStrategy:", StringComparison.Ordinal))
                continue;

            lines.RemoveAt(i);

            // An "Annotations:" header left without any annotation under it goes too
            var headerIsEmpty = lines[i - 1].Trim() == "Annotations:"
                             && (i == lines.Count || Indentation(lines[i]) <= Indentation(lines[i - 1]));
            if (headerIsEmpty)
                lines.RemoveAt(i - 1);
        }

        return string.Join('\n', lines);
    }

    private static int Indentation(string line) => line.Length - line.TrimStart().Length;

    private static string Line(string[] lines, int index) => index < lines.Length ? lines[index].Trim() : "<end>";

    private static OpenedContext OpenContext(string provider, string connectionString, bool useCompiledModel)
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = provider,
                               ["Database:ConnectionString"] = connectionString,
                               ["Database:UseCompiledModel"] = useCompiledModel ? "true" : "false"
                           })
                           .Build();
        var services = new ServiceCollection();
        services.AddPersistenceInfrastructureServices(configuration);
        var serviceProvider = services.BuildServiceProvider();
        var scope = serviceProvider.CreateScope();

        return new OpenedContext(serviceProvider, scope, scope.ServiceProvider.GetRequiredService<NLightningDbContext>());
    }

    private sealed record OpenedContext(ServiceProvider Provider, IServiceScope Scope, NLightningDbContext Context)
        : IDisposable
    {
        public void Dispose()
        {
            Scope.Dispose();
            Provider.Dispose();
        }
    }
}
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence;
using Infrastructure.Persistence.CompiledModels;
using Infrastructure.Persistence.Contexts;

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
    public async Task Given_TheSqliteCompiledModel_When_MigratingAndRoundTrippingAChannelConfig_Then_ItWorks()
    {
        // Arrange: the compiled model as a NativeAOT node uses it, on a real SQLite file
        var path = Path.Combine(Path.GetTempPath(), $"nltg-compiled-model-{Guid.NewGuid():N}.db");
        try
        {
            using (var opened = OpenContext("sqlite", $"Data Source={path};Pooling=False", useCompiledModel: true))
            {
                // Act
                await opened.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);
                var tables = opened.Context.Model.GetEntityTypes().Count();
                var peers = await opened.Context.Peers.AsNoTracking()
                                        .CountAsync(TestContext.Current.CancellationToken);

                // Assert
                Assert.Same(CompiledModelCatalog.Get(Infrastructure.Persistence.Enums.DatabaseType.Sqlite),
                            opened.Context.Model);
                Assert.True(tables > 30);
                Assert.Equal(0, peers);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

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
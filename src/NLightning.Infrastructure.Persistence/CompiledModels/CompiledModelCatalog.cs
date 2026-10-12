using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NLightning.Infrastructure.Persistence.CompiledModels;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Enums;

/// <summary>
/// The EF Core compiled models of <c>NLightningDbContext</c>, one per provider (NL-708), generated into
/// <c>CompiledModels/{Sqlite,Postgres,SqlServer}</c> by <c>scripts/optimize_model.sh</c> (which
/// <c>add_migration.sh</c> runs). A NativeAOT build cannot build a model at run time, so it uses these; the JIT build
/// builds its model from <c>OnModelCreating</c> unless <c>Database:UseCompiledModel</c> is true.
/// <c>CompiledModelTests</c> fails when a compiled model differs from the runtime model.
/// </summary>
public static class CompiledModelCatalog
{
    /// <summary>
    /// A provider's default type mapping (<c>SqliteDecimalTypeMapping.Default</c>, ...) creates its comparer on first use
    /// through <c>ValueComparer.CreateDefault(Type, bool)</c>, i.e. <c>MakeGenericMethod</c> over the property type.
    /// NativeAOT can only invoke instantiations it compiled, so the value types of our model are referenced here (a
    /// real AOT binary failed on <c>CreateDefault&lt;decimal&gt;</c> while initializing the SQLite model, NL-708).
    /// Reference types share one instantiation and need no entry.
    /// </summary>
    private static readonly Func<bool, ValueComparer>[] s_defaultComparerRoots =
    [
        ValueComparer.CreateDefault<bool>, ValueComparer.CreateDefault<byte>, ValueComparer.CreateDefault<ushort>,
        ValueComparer.CreateDefault<int>, ValueComparer.CreateDefault<uint>, ValueComparer.CreateDefault<long>,
        ValueComparer.CreateDefault<ulong>, ValueComparer.CreateDefault<decimal>, ValueComparer.CreateDefault<DateTime>,
        ValueComparer.CreateDefault<DateTimeOffset>, ValueComparer.CreateDefault<Guid>,
        ValueComparer.CreateDefault<AddressType>, ValueComparer.CreateDefault<HtlcDirection>,
        ValueComparer.CreateDefault<ChannelId>,
        ValueComparer.CreateDefault<CompactPubKey>, ValueComparer.CreateDefault<Hash>,
        ValueComparer.CreateDefault<ShortChannelId>, ValueComparer.CreateDefault<TxId>
    ];

    /// <summary>The value types whose default comparer a NativeAOT binary can create (CompiledModelTests checks the
    /// model's value types are all here).</summary>
    public static IReadOnlyList<Type> RootedComparerTypes
        => s_defaultComparerRoots.Select(root => root.Method.GetGenericArguments()[0]).ToList();

    /// <summary>
    /// Whether the node uses the compiled model: <paramref name="configured"/> (<c>Database:UseCompiledModel</c>) when
    /// set, otherwise only when dynamic code is not supported (NativeAOT).
    /// </summary>
    public static bool ShouldUse(bool? configured) => ShouldUse(configured, RuntimeFeature.IsDynamicCodeSupported);

    /// <summary><see cref="ShouldUse(bool?)"/> for a given runtime (tests).</summary>
    public static bool ShouldUse(bool? configured, bool dynamicCodeSupported) => configured ?? !dynamicCodeSupported;

    /// <summary>The compiled model of <paramref name="databaseType"/>.</summary>
    public static IModel Get(DatabaseType databaseType)
    {
#if NLTG_NO_COMPILED_MODELS
        // Only the regeneration build (optimize_model.sh) compiles without them
        throw new InvalidOperationException(
            $"This build has no compiled models (NLTG_NO_COMPILED_MODELS); cannot use one for {databaseType}.");
#else
        // Keeps the comparer instantiations above in a NativeAOT binary
        GC.KeepAlive(s_defaultComparerRoots);

        return databaseType switch
        {
            DatabaseType.Sqlite => Sqlite.NLightningDbContextModel.Instance,
            DatabaseType.PostgreSql => Postgres.NLightningDbContextModel.Instance,
            DatabaseType.MicrosoftSql => SqlServer.NLightningDbContextModel.Instance,
            _ => throw new ArgumentOutOfRangeException(nameof(databaseType), databaseType, "Unknown database type.")
        };
#endif
    }
}
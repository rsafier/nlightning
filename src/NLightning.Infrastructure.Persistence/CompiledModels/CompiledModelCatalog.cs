using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NLightning.Infrastructure.Persistence.CompiledModels;

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
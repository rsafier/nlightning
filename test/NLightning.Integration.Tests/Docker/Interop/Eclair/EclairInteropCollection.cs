namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Fixtures;

/// <summary>
/// The Eclair interop collection (NL-180): one <see cref="EclairFixture"/> (its own bitcoind and Eclair) for every
/// Eclair test class, never in parallel with other collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class EclairInteropCollection : ICollectionFixture<EclairFixture>
{
    public const string Name = "eclair-interop";

    /// <summary>
    /// The trait every Eclair interop test carries (<c>-trait "Category=Interop.Eclair"</c> runs them).
    /// </summary>
    public const string Category = "Interop.Eclair";
}
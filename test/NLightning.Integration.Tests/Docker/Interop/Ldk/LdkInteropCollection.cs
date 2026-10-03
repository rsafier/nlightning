namespace NLightning.Integration.Tests.Docker.Interop.Ldk;

using Fixtures;

/// <summary>
/// The LDK interop collection (NL-180): one <see cref="LdkFixture"/> (its own bitcoind and ldk-server) for every LDK
/// test class, never in parallel with other collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class LdkInteropCollection : ICollectionFixture<LdkFixture>
{
    public const string Name = "ldk-interop";

    /// <summary>
    /// The trait every LDK interop test carries (<c>-trait "Category=Interop.Ldk"</c> runs them).
    /// </summary>
    public const string Category = "Interop.Ldk";
}
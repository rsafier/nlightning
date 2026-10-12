namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Fixtures;

/// <summary>
/// The CLN interop collection: one <see cref="ClnFixture"/> (its own bitcoind and CLN) for every CLN test class. It
/// does not run in parallel with other collections, so it never competes with the LND <c>regtest</c> collection for
/// CPU while that one's timing-sensitive tests run.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ClnInteropCollection : ICollectionFixture<ClnFixture>
{
    public const string Name = "cln-interop";

    /// <summary>
    /// The trait every CLN interop test carries (<c>--filter "Category=Interop.Cln"</c> runs them,
    /// <c>"Category!=Interop.Cln"</c> leaves them out of a Docker run).
    /// </summary>
    public const string Category = "Interop.Cln";
}
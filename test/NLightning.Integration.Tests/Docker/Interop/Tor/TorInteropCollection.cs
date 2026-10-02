namespace NLightning.Integration.Tests.Docker.Interop.Tor;

using Fixtures;

/// <summary>
/// The Tor interop collection (NL-572): one <see cref="TorInteropFixture"/> (its own bitcoind, a Tor client and a CLN
/// reachable only through its onion service) for every Tor interop class. Not parallel with other collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class TorInteropCollection : ICollectionFixture<TorInteropFixture>
{
    public const string Name = "tor-interop";

    /// <summary>
    /// The trait every Tor interop test carries (<c>-trait Category=Interop.Tor</c>; <c>scripts/run-interop.sh tor</c>).
    /// The fixture needs Internet access (the public Tor network).
    /// </summary>
    public const string Category = "Interop.Tor";
}
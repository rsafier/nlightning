namespace NLightning.Integration.Tests.Docker.Interop.Cashu;

using Fixtures.Cashu;

/// <summary>
/// The Cashu mint proof collection (Cashu plan C2, NL-993): one <see cref="CashuMintFixture"/> (a run namespace of the
/// cluster harness with its own bitcoind, CDK's <c>cdk-mintd</c> and <c>cdk-cli</c>) for every class. Not parallel with
/// other collections.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class CashuMintCollection : ICollectionFixture<CashuMintFixture>
{
    public const string Name = "cashu-mint";

    /// <summary>
    /// The trait every Cashu mint test carries (<c>-trait Category=Interop.Cashu</c>; <c>scripts/run-cluster.sh
    /// --matrix cashu</c>).
    /// </summary>
    public const string Category = "Interop.Cashu";
}
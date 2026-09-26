namespace NLightning.Integration.Tests.Docker.Onchain;

using Fixtures;

/// <summary>
/// The BOLT 5 on-chain proofs (plan §5, Proof conventions): their own <see cref="LightningRegtestNetworkFixture"/>,
/// never shared with the <c>regtest</c> collection, because they close channels and reorg the chain.
/// </summary>
/// <remarks>
/// Every fixture uses the same container names, so two fixtures must never be alive at once. The collection does not
/// run in parallel with others (xUnit runs it after the parallel collections, whose fixtures are disposed when they
/// finish), but the supported way to run it is its own <c>dotnet test</c> process: <c>scripts/run-onchain.sh</c>.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class OnchainRegtestCollection : ICollectionFixture<LightningRegtestNetworkFixture>
{
    public const string Name = "onchain-regtest";
}
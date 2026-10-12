namespace NLightning.Integration.Tests.Docker.Onchain;

using Fixtures;

/// <summary>
/// The BOLT 5 on-chain proofs (plan §5, Proof conventions): their own <see cref="LightningRegtestNetworkFixture"/>,
/// never shared with the <c>regtest</c> collection, because they close channels and reorg the chain.
/// </summary>
/// <remarks>
/// The collection does not run in parallel with others (xUnit runs it after the parallel collections, whose fixtures
/// are disposed when they finish); its network runs in a namespace of its own on the cluster, in its own test process:
/// <c>scripts/run-cluster.sh -n 1 --suite onchain</c> (and <c>--suite anchors</c>).
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class OnchainRegtestCollection : ICollectionFixture<LightningRegtestNetworkFixture>
{
    public const string Name = "onchain-regtest";
}
namespace NLightning.Integration.Tests.Docker.Gossip;

using Fixtures;

/// <summary>
/// The BOLT 7 Docker proofs (plan §5, Proof conventions, D13): their own <see cref="LightningRegtestNetworkFixture"/>,
/// never shared with the <c>regtest</c> collection, because public channels permanently change the LND nodes' graph
/// for every later test (and Proof G2 (c) closes a channel).
/// </summary>
/// <remarks>
/// <para>Every fixture uses the same container names, so two fixtures must never be alive at once. The supported way
/// to run the collection is its own process: <c>scripts/run-gossip.sh</c>.</para>
/// <para>D13 asked for LND's <c>--trickledelay=1000</c>: LNUnit 3.0.4 already starts every LND with
/// <c>--trickledelay=5000</c> (ms) and <c>--gossip.sub-batch-delay=1s</c>, so gossip reaches the other LND nodes
/// within seconds and the shared fixture needs no extra flag.</para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class GossipRegtestCollection : ICollectionFixture<LightningRegtestNetworkFixture>
{
    public const string Name = "gossip-regtest";
}
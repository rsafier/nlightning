namespace NLightning.Integration.Tests.Docker.Taproot;

using Fixtures;
using Testing.Cluster.Nodes.Lnd;
using Testing.Cluster.Topology.Lnd;

/// <summary>
/// The LND network of the simple taproot interop proofs (taproot plan T6, wave t02): bitcoind <c>miner</c> and one LND
/// 0.21.4, <see cref="Alias"/>, run with <see cref="LndNodeOptions.SimpleTaprootChannelsFlag"/> and
/// <c>--accept-keysend</c>, two wallet outputs and no startup channels, in a run namespace of its own.
/// </summary>
/// <remarks>
/// A network of its own instead of the flag on the shared <c>regtest</c> network's alice: the flag also turns on LND's
/// RBF cooperative close and advertises bits 81/181 to every peer, which would change the legacy and simple close
/// proofs and the feature negotiation of every other class of that collection. One LND node is all the proofs need
/// (our node opens to it and it opens to us), so the collection costs one namespace.
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class LndTaprootNetworkFixture()
    : LightningRegtestNetworkFixture(new LndRegtestNetworkOptions { Spec = NetworkSpec, WaitForGraph = false })
{
    /// <summary>The LND node's alias.</summary>
    public const string Alias = "tara";

    /// <summary>The network: <see cref="Alias"/> with the taproot flag, no channels.</summary>
    public static LndRegtestNetworkSpec NetworkSpec { get; } = new(
        [new LndRegtestNodeSpec(Alias, [LndNodeOptions.SimpleTaprootChannelsFlag, "--accept-keysend"])], []);
}

/// <summary>The collection of the LND taproot interop proofs (<c>scripts/run-cluster.sh --suite taproot</c>).</summary>
[CollectionDefinition(Name)]
public class LndTaprootRegtestCollection : ICollectionFixture<LndTaprootNetworkFixture>
{
    public const string Name = "taproot-regtest";
}
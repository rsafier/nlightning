namespace NLightning.Testing.Cluster.Topology.Lnd;

/// <summary>
/// The <see cref="LndRegtestNetwork"/> kept warm for an xunit collection (<see cref="ClusterTopologyFixture"/>: built
/// once in its own run namespace, nothing reset between tests, every isolation rule of the base class): the cluster
/// counterpart of the Docker suites' <c>LightningRegtestNetworkFixture</c>. Derive to change the network
/// (<see cref="CreateNetworkOptions"/>) or to stop something of the suite's own before the namespace goes
/// (<see cref="ClusterTopologyFixture.OnStoppingAsync"/>, e.g. in-process nodes that joined).
/// </summary>
public class LndRegtestNetworkFixture : ClusterTopologyFixture
{
    private LndRegtestNetworkOptions? _options;
    private LndRegtestNetwork? _network;

    /// <summary>The network, once the fixture started.</summary>
    public LndRegtestNetwork Network =>
        _network ?? throw new InvalidOperationException($"The {Suite} network is not started");

    protected override string Suite => "lnd-regtest";

    /// <summary>The network's options; the fixture's log is added when they have none.</summary>
    protected virtual LndRegtestNetworkOptions CreateNetworkOptions() => new();

    protected sealed override void Configure(TopologyBuilder builder)
    {
        var options = CreateNetworkOptions();
        _options = options.Log is null ? options with { Log = Log } : options;
        LndRegtestNetwork.Declare(builder, _options);
    }

    protected override async Task OnBuiltAsync(TestTopology topology, CancellationToken cancellationToken) =>
        _network = await LndRegtestNetwork.SetUpAsync(topology, _options!, cancellationToken).ConfigureAwait(false);

    protected override ValueTask OnStoppingAsync()
    {
        _network?.Dispose();
        return ValueTask.CompletedTask;
    }
}
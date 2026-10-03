using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace NLightning.Testing.Cluster.Topology;

using Run;

/// <summary>
/// A topology kept warm for a whole xunit collection, as the Docker fixtures keep their containers: built once, when
/// the collection's first test that runs asks for it (<see cref="EnsureStartedAsync()"/>), in its own run namespace,
/// and deleted after its last one (in the background, <see cref="TestRunOptions.WaitForDeletion"/>). Nothing is reset
/// between tests.
/// </summary>
/// <remarks>
/// <para>
/// Starting it: each test class of the collection implements <see cref="IAsyncLifetime"/> and awaits
/// <see cref="EnsureStartedAsync()"/> in its <c>InitializeAsync</c> (the first call builds, later calls return the
/// same start). xunit's own <see cref="InitializeAsync"/> builds nothing: xunit creates a collection's or class's
/// fixtures whenever the selection holds any of its tests, Explicit ones included (they turn into "not run" only
/// later), so an eager build would start a namespace in every <c>FullyQualifiedName!~Docker</c> run and fail its
/// Explicit tests where no cluster is reachable (CI; NL-800). A Docker-era fixture's cluster backend calls
/// <see cref="EnsureStartedAsync(CancellationToken)"/> from its own start.
/// </para>
/// <para>Isolation expectations (what a test of a shared topology may and may not assume):</para>
/// <list type="bullet">
/// <item>Collections are isolated from each other: each fixture instance has its own namespace, chain and nodes
/// (collections run in parallel in one process, each holding one run slot, <see cref="RunAdmission"/>).</item>
/// <item>Tests of one collection run one after another and see what the earlier ones left: the chain height, wallet
/// and channel balances, peers, invoices, channels opened or closed. Assert deltas, never absolute balances or
/// heights; give invoices and labels values unique to the test; open a channel of the test's own when it needs a
/// fresh one; mine through <see cref="TestTopology.MineAndSyncAsync"/>.</item>
/// <item>A test that restarts, kills, crashes, pauses or partitions a node, or reconfigures it, restores the topology
/// before it returns (heal, resume, <see cref="TestTopology.ReconnectChannelsAsync"/>,
/// <see cref="TestTopology.WaitChannelsActiveAsync"/>), or runs in a collection (or a <see cref="TestRun"/>) of its
/// own. Nodes on <see cref="Kube.NodeStorage.Ephemeral"/> storage cannot be restarted or killed at all.</item>
/// <item>A test never deletes or replaces the fixture's objects; anything it adds to the namespace (a node, a
/// NetworkPolicy) it removes, or it goes with the namespace at the end.</item>
/// </list>
/// <para>
/// Use <see cref="ClusterTopologyFixture{TDefinition}"/> with an <see cref="IClusterTopologyDefinition"/>, or derive
/// from this class for a fixture that does more after the build (<see cref="OnBuiltAsync"/>) or stops something of its
/// own before the namespace goes (<see cref="OnStoppingAsync"/>, e.g. in-process nodes).
/// </para>
/// </remarks>
public abstract class ClusterTopologyFixture : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> _startLog = new();
    private readonly object _startGate = new();
    private TestRun? _run;
    private TestTopology? _topology;
    private Task? _start;
    private int _disposed;

    /// <summary>The suite label of the run (<see cref="RunLabels.Suite"/>) and its run options' suite.</summary>
    protected abstract string Suite { get; }

    /// <summary>Declares the topology.</summary>
    protected abstract void Configure(TopologyBuilder builder);

    /// <summary>
    /// The run's options: <see cref="TestRunOptions.FromEnvironment"/> for <see cref="Suite"/>, the spike's quota, and
    /// the fixture's log.
    /// </summary>
    protected virtual TestRunOptions CreateRunOptions() =>
        TestRunOptions.FromEnvironment(Suite) with { Quota = NamespaceQuota.Spike, Log = Log };

    /// <summary>Runs once the topology is built, before the first test (more nodes, channels, wallets).</summary>
    protected virtual Task OnBuiltAsync(TestTopology topology, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>The run (namespace) of the collection.</summary>
    public TestRun Run => _run ?? throw NotStarted();

    /// <summary>The built topology.</summary>
    public TestTopology Topology => _topology ?? throw NotStarted();

    /// <summary>How long the namespace and the topology took to start (once per collection).</summary>
    public TimeSpan StartTime { get; private set; }

    /// <summary>
    /// What the start logged (namespace, each topology step with its time). Fixtures have no test output; a test can
    /// write these to its own.
    /// </summary>
    public IReadOnlyCollection<string> StartLog => _startLog;

    /// <summary>The Lightning node named <paramref name="name"/>.</summary>
    public ITopologyLightningNode Node(string name) => Topology.Node(name);

    /// <summary>The Lightning node named <paramref name="name"/> as its implementation's adapter.</summary>
    public T Node<T>(string name) where T : class, ITopologyLightningNode => Topology.Node<T>(name);

    /// <summary>
    /// xunit's fixture start: nothing is built here (see the remarks: xunit also creates the fixtures of tests that
    /// will not run); the first <see cref="EnsureStartedAsync()"/> builds.
    /// </summary>
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Builds the run and the topology on the first call (under the calling test's token) and returns that same start
    /// to every later call, failed or not: call it from each test class's <c>InitializeAsync</c>.
    /// </summary>
    public Task EnsureStartedAsync() => EnsureStartedAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// <see cref="EnsureStartedAsync()"/> under <paramref name="cancellationToken"/> (a caller with a start budget of
    /// its own, e.g. the cluster backend of a Docker-era fixture, passes its token).
    /// </summary>
    public Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        lock (_startGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 && _start is null, this);
            return _start ??= StartAsync(cancellationToken);
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        _run = await TestRun.StartAsync(CreateRunOptions(), cancellationToken).ConfigureAwait(false);
        try
        {
            var builder = new TopologyBuilder { Log = Log };
            Configure(builder);
            _topology = await builder.BuildAsync(_run, cancellationToken).ConfigureAwait(false);
            await OnBuiltAsync(_topology, cancellationToken).ConfigureAwait(false);
            StartTime = watch.Elapsed;
            Log($"[fixture] {_run.Namespace}: {Suite} topology started in {StartTime.TotalSeconds:F1} s");
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        GC.SuppressFinalize(this);
        try
        {
            _topology?.Dispose();
        }
        finally
        {
            try
            {
                await OnStoppingAsync().ConfigureAwait(false);
            }
            finally
            {
                if (_run is not null)
                    await _run.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs when the fixture is disposed (also after a failed start), after the topology's adapters and before the
    /// run's namespace is deleted: stop what the fixture started outside the namespace here (in-process nodes, which
    /// would otherwise keep calling a bitcoind that is going away).
    /// </summary>
    protected virtual ValueTask OnStoppingAsync() => ValueTask.CompletedTask;

    /// <summary>Records <paramref name="line"/> in <see cref="StartLog"/> and as an xunit diagnostic message.</summary>
    protected void Log(string line)
    {
        _startLog.Enqueue(line);
        TestContext.Current.SendDiagnosticMessage(line);
    }

    private InvalidOperationException NotStarted() =>
        new($"The {Suite} topology is not started: use the fixture as an xunit collection or class fixture and await "
          + $"its {nameof(EnsureStartedAsync)} in the test class's InitializeAsync (IAsyncLifetime)");
}

/// <summary>
/// A topology declared once, for <see cref="ClusterTopologyFixture{TDefinition}"/>.
/// </summary>
/// <example>
/// <code>
/// public sealed class ClnPair : IClusterTopologyDefinition
/// {
///     public static string Suite => "cln-pair";
///     public static void Configure(TopologyBuilder builder) =>
///         builder.AddBitcoinCore("miner").AddCln("alice").AddCln("bob")
///                .FundWallet("alice", 2_000_000).AddChannel("alice", "bob", 1_000_000);
/// }
///
/// [CollectionDefinition("cln-pair")]
/// public sealed class ClnPairCollection : ICollectionFixture&lt;ClusterTopologyFixture&lt;ClnPair&gt;&gt;;
///
/// [Collection("cln-pair")]
/// public class MyTests(ClusterTopologyFixture&lt;ClnPair&gt; fixture) { ... }
/// </code>
/// </example>
public interface IClusterTopologyDefinition
{
    /// <summary>The suite label of the fixture's run.</summary>
    static abstract string Suite { get; }

    /// <summary>Declares the topology.</summary>
    static abstract void Configure(TopologyBuilder builder);
}

/// <summary>
/// The warm topology of <typeparamref name="TDefinition"/> for an xunit collection (see
/// <see cref="ClusterTopologyFixture"/> for the isolation expectations).
/// </summary>
public class ClusterTopologyFixture<TDefinition> : ClusterTopologyFixture
    where TDefinition : IClusterTopologyDefinition
{
    protected override string Suite => TDefinition.Suite;

    protected override void Configure(TopologyBuilder builder) => TDefinition.Configure(builder);
}
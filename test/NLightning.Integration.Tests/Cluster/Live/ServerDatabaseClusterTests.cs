using System.Diagnostics;

namespace NLightning.Integration.Tests.Cluster.Live;

using Docker.Utils;
using Domain.Crypto.ValueObjects;
using Fixtures;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes.Lnd;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using Testing.Lnd.Lnrpc;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>
/// The server-database restart of <c>Docker/MultiNodeHarnessTests</c> (NL-347, NL-429) on the cluster harness (test
/// harness phase 4): our in-process node on a database of its own on a Postgres pod the test starts
/// (<see cref="PostgresFixture.StartNamedOnCluster"/>, whatever <c>NLTG_TEST_BACKEND</c> says) connects to an LND pod, stops, and on its next start reads the stored peer from
/// Postgres and dials it again by its Service name.
/// </summary>
/// <remarks>
/// Explicit and <c>Category=Cluster</c>: <c>scripts/run-cluster.sh -n 1 --suite postgres</c> (with
/// <c>Docker/PostgresTests</c>), <c>scripts/run-cluster.sh -n 1 -p integration --class
/// NLightning.Integration.Tests.Cluster.Live.ServerDatabaseClusterTests</c>, or the built test assembly with
/// <c>-explicit on -class ...</c>. Two namespaces: the test's Postgres server and its topology. Not in the
/// <c>postgres</c> collection: xunit creates a collection's fixture whenever the selection holds any of its tests,
/// Explicit ones included, so a <c>FullyQualifiedName!~Docker</c> run would start the collection's Postgres
/// fixture (NL-801).
/// </remarks>
[Trait("Category", "Cluster")]
[Trait("Database", "Postgres")]
public class ServerDatabaseClusterTests
{
    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromMinutes(2);

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Fact(Explicit = true)]
    public async Task Given_PostgresDatabase_When_OurNodeRestarts_Then_ItReconnectsToTheStoredPeer()
    {
        // Arrange: bitcoind + LND in a run of their own, our node on a fresh database of the collection's server
        var ct = TestContext.Current.CancellationToken;
        var watch = Stopwatch.StartNew();
        using var postgres = PostgresFixture.StartNamedOnCluster("pg-restart");
        var databaseName = $"nltg_cluster_{Guid.NewGuid():N}";
        var database = TestNodeDatabase.Postgres(postgres.ConnectionStringFor(databaseName));
        var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("pg-restart") with
        {
            Quota = NamespaceQuota.Spike,
            Log = Log
        }, ct);
        try
        {
            await using var inProcess = new InProcessNodeDeployer { Database = _ => database };
            var builder = new TopologyBuilder
            {
                Log = Log,
                Storage = NodeStorage.Ephemeral,
                ReadyTimeout = TimeSpan.FromMinutes(4),
                StepTimeout = s_stepTimeout
            };
            using var topology = await builder.AddBitcoinCore("miner")
                                              .AddNLightning("nltg")
                                              .AddLnd("lnd")
                                              .UseInProcessNodes(inProcess)
                                              .BuildAsync(run, ct);
            var nltg = topology.InProcessNode("nltg");
            var lnd = topology.Node<LndNode>("lnd");
            var lndId = await lnd.GetNodeIdAsync(ct);
            await TopologyDeployer.ConnectAsync(nltg, await lnd.GetAddressAsync(ct), s_stepTimeout, ct);
            var built = watch.Elapsed;

            // Act: stop; LND drops us; start on the same key and database
            await nltg.TestNode.StopAsync();
            await ClusterPoll.UntilAsync(async c => !await IsPeerOfAsync(lnd, nltg.TestNode.NodeIdHex, c), s_stepTimeout,
                                         TimeSpan.FromMilliseconds(250), "LND drops our node", ct);
            var restart = Stopwatch.StartNew();
            await nltg.TestNode.StartAsync(ct);

            // Assert: the node ran on the server database, and the peer came back from it without a connect call
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", nltg.TestNode.GetEfProviderName());
            await ClusterPoll.UntilAsync(async c => await IsPeerOfAsync(lnd, nltg.TestNode.NodeIdHex, c)
                                                 && nltg.TestNode.IsConnectedTo(
                                                        new CompactPubKey(Convert.FromHexString(lndId))),
                                         s_stepTimeout, TimeSpan.FromMilliseconds(250),
                                         "our node reconnects to LND on startup", ct);
            await topology.WaitAllAtTipAsync(ct);
            Log($"{run.Namespace}: built in {built.TotalSeconds:F1} s on cluster Postgres "
              + $"{postgres.Host}:{postgres.HostPort}/{databaseName}; restart to reconnected "
              + $"{restart.Elapsed.TotalSeconds:F1} s");
        }
        finally
        {
            await run.DisposeAsync();
        }
    }

    private static async Task<bool> IsPeerOfAsync(LndNode lnd, string nodeIdHex, CancellationToken ct)
    {
        var peers = await lnd.Lightning.ListPeersAsync(new ListPeersRequest(), cancellationToken: ct);
        return peers.Peers.Any(p => p.PubKey == nodeIdHex);
    }
}
using System.Reflection;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Docker;

using Domain.Exceptions;
using Domain.Node.ValueObjects;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// Smoke tests of the multi-node harness the ABCD tests build on (roadmap W0-F): two in-process NLightning nodes
/// (Bob and Carol) next to the fixture's LND nodes, the chain-sync barrier, simultaneous connects between two of our
/// own nodes, crash and restart, and the database provider parameter.
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class MultiNodeHarnessTests : IAsyncLifetime
{
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Connect rounds per test (the lane proof asks for 5 repeated runs).
    /// </summary>
    private const int Rounds = 5;

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];
    private readonly List<IDisposable> _containers = [];

    public MultiNodeHarnessTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var lnd in _fixture.LndNodes)
            Console.WriteLine($"LND {lnd.LocalAlias}: {await LndTestHelpers.GetVersionAsync(lnd, ct)}");
    }

    [Fact]
    public async Task Given_BobAndCarol_When_ConnectedToEachOtherAndToAliceAndDavid_Then_AllAtTheSameHeightAfterMining()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var david = _fixture.GetLndNode("david");
        var bob = await StartNodeAsync("bob");
        var carol = await StartNodeAsync("carol");
        var startTip = await ChainSync.WaitAllAtTipAsync(_fixture, [bob, carol], ct);

        // Act: the ABCD topology
        await bob.ConnectToAsync(alice, ct);
        await bob.ConnectToAsync(carol, ct);
        await carol.ConnectToAsync(david, ct);
        await carol.ConnectToAsync(alice, ct);
        await bob.ConnectToAsync(david, ct);
        var tip = await ChainSync.MineAndWaitAsync(_fixture, 3, _fixture.LndNodes, [bob, carol], ct);

        // Assert
        Assert.True(tip >= startTip + 3, $"Tip {tip} is not 3 blocks above {startTip}");
        Assert.Equal(tip, bob.BlockchainMonitor.LastProcessedBlockHeight);
        Assert.Equal(tip, carol.BlockchainMonitor.LastProcessedBlockHeight);

        Assert.True(bob.IsConnectedTo(carol.NodeId), "Bob is not connected to Carol");
        Assert.True(carol.IsConnectedTo(bob.NodeId), "Carol is not connected to Bob");
        foreach (var node in new[] { bob, carol })
        {
            Assert.True(node.IsConnectedTo(alice.LocalNodePubKeyBytes), $"{node.Name} is not connected to alice");
            Assert.True(node.IsConnectedTo(david.LocalNodePubKeyBytes), $"{node.Name} is not connected to david");
            Assert.True(await LndTestHelpers.IsConnectedToAsync(alice, node.NodeIdHex, ct),
                        $"alice does not list {node.Name}");
            Assert.True(await LndTestHelpers.IsConnectedToAsync(david, node.NodeIdHex, ct),
                        $"david does not list {node.Name}");
            // Only that the override is wired: no test here runs the reconnect loop (it needs a peer with a channel)
            Assert.Equal(NLightningTestNode.FastReconnectInitialDelay, GetReconnectInitialDelay(node));
        }
    }

    /// <remarks>
    /// Regression for NL-239 (the responder lost the initiator's <c>init</c>) and NL-240 (on a simultaneous connect
    /// an init write failed on a live connection, which the other end's tie-break kept): every round must leave
    /// exactly one live connection with the init exchanged, and neither bug's log line may appear.
    /// </remarks>
    [Fact]
    public async Task Given_BobAndCarol_When_ConnectingToEachOtherAtTheSameTime_Then_BothKeepOneConnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bob = await StartNodeAsync("bob");
        var carol = await StartNodeAsync("carol");
        NLightningTestNode[] ends = [bob, carol];

        for (var round = 1; round <= Rounds; round++)
        {
            // Act: raw peer-manager connects, so nothing retries behind our back
            var outcomes = await Task.WhenAll(TryConnectAsync(bob, carol), TryConnectAsync(carol, bob));
            Console.WriteLine($"Round {round}: bob->carol {outcomes[0]}, carol->bob {outcomes[1]}");

            // Assert: had the two ends kept different connections, each would close the one the other kept, and
            // neither would stay connected
            var failure = await bob.WaitForStableConnectionAsync(carol, ct);
            Assert.True(failure is null, $"Round {round}: {failure}");
            Assert.Single(bob.PeerManager.ListPeers(), p => p.NodeId == carol.NodeId);
            Assert.Single(carol.PeerManager.ListPeers(), p => p.NodeId == bob.NodeId);

            await DisconnectAsync(bob, carol, $"round {round}", ct);
        }

        AssertNoConnectBugLogged(ends);
    }

    [Fact]
    public async Task Given_BobAndCarol_When_ConnectingRepeatedlyInBothDirections_Then_EveryConnectIsStable()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bob = await StartNodeAsync("bob");
        var carol = await StartNodeAsync("carol");
        NLightningTestNode[] ends = [bob, carol];

        for (var round = 1; round <= Rounds; round++)
        {
            foreach (var (from, to) in new[] { (bob, carol), (carol, bob) })
            {
                // Act: one raw connect, no retry
                await from.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(to.Address)).WaitAsync(ct);

                // Assert: connected on return (init exchanged) and still up on both ends after the window
                Assert.True(from.IsConnectedTo(to.NodeId), $"Round {round}: {from.Name} lists no {to.Name}");
                var failure = await from.WaitForStableConnectionAsync(to, ct);
                Assert.True(failure is null, $"Round {round}, {from.Name} -> {to.Name}: {failure}");

                await DisconnectAsync(from, to, $"round {round}", ct);
            }
        }

        AssertNoConnectBugLogged(ends);
    }

    [Fact]
    public async Task Given_ConnectedCarol_When_SheCrashesAndRestarts_Then_PeersSeeTheDropAndSheReconnects()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var david = _fixture.GetLndNode("david");
        var bob = await StartNodeAsync("bob");
        var carol = await StartNodeAsync("carol");
        await carol.ConnectToAsync(bob, ct);
        await carol.ConnectToAsync(david, ct);
        await Poll.UntilAsync(() => bob.IsConnectedTo(carol.NodeId), s_connectTimeout, "Bob sees Carol", ct);

        // Act
        await carol.CrashAsync();

        // Assert: both peers notice without any message from Carol
        await Poll.UntilAsync(() => !bob.IsConnectedTo(carol.NodeId), s_connectTimeout, "Bob drops Carol", ct);
        await Poll.UntilAsync(async () => !await LndTestHelpers.IsConnectedToAsync(david, carol.NodeIdHex, ct),
                              s_connectTimeout, "david drops Carol", ct);
        Assert.False(carol.IsRunning);

        // Act: restart on the same key and database; startup connects to the peers she stored
        await carol.StartAsync(ct);

        // Assert
        await Poll.UntilAsync(() => bob.IsConnectedTo(carol.NodeId) && carol.IsConnectedTo(bob.NodeId),
                              s_connectTimeout, "Bob and Carol connected again", ct);
        await Poll.UntilAsync(async () => await LndTestHelpers.IsConnectedToAsync(david, carol.NodeIdHex, ct),
                              s_connectTimeout, "david and Carol connected again", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [bob, carol], ct);
    }

    [Theory]
    [InlineData(TestDatabaseProvider.Postgres)]
    [InlineData(TestDatabaseProvider.SqlServer)]
    public async Task Given_ServerDatabase_When_NodeRestarts_Then_ItReconnectsToTheStoredPeer(
        TestDatabaseProvider provider)
    {
        // Arrange: a container of our own, so the postgres/sqlserver collections can run at the same time
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var databaseName = $"nltg_harness_{Guid.NewGuid():N}";
        TestNodeDatabase database;
        string expectedEfProvider;
        if (provider == TestDatabaseProvider.Postgres)
        {
            expectedEfProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
            var postgres = PostgresFixture.StartNamed("nltg-harness-postgres");
            _containers.Add(postgres);
            database = TestNodeDatabase.Postgres(postgres.ConnectionStringFor(databaseName));
        }
        else
        {
            expectedEfProvider = "Microsoft.EntityFrameworkCore.SqlServer";
            var sqlServer = SqlServerFixture.StartNamed("nltg-harness-sqlserver");
            _containers.Add(sqlServer);
            database = TestNodeDatabase.SqlServer(sqlServer.ConnectionStringFor(databaseName));
        }

        var bob = await StartNodeAsync("bob", database);
        await bob.ConnectToAsync(alice, ct);

        // Act
        await bob.StopAsync();
        await Poll.UntilAsync(async () => !await LndTestHelpers.IsConnectedToAsync(alice, bob.NodeIdHex, ct),
                              s_connectTimeout, "alice drops Bob", ct);
        await bob.StartAsync(ct);

        // Assert: the node really ran on the server database, and the peer came back from it
        Assert.Equal(expectedEfProvider, bob.GetEfProviderName());
        await Poll.UntilAsync(() => bob.IsConnectedTo(alice.LocalNodePubKeyBytes), s_connectTimeout,
                              "Bob reconnects to alice on startup", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [bob], ct);
    }

    [Fact]
    public async Task Given_UnreachableDatabase_When_NodeStarts_Then_StartFailsAndTheNodeCanStartAgain()
    {
        // Arrange: nothing listens on this Postgres port
        var ct = TestContext.Current.CancellationToken;
        var deadPort = await PortPoolUtil.GetAvailablePortAsync();
        var node = await NLightningTestNode.CreateAsync(
            _fixture, "bob", TestNodeDatabase.Postgres(
                $"Host=127.0.0.1;Port={deadPort};Database=nltg_dead;Username=u;Password=p;Timeout=2"));
        _nodes.Add(node);

        // Act
        var first = await Record.ExceptionAsync(() => node.StartAsync(ct));
        var second = await Record.ExceptionAsync(() => node.StartAsync(ct));
        PortPoolUtil.ReleasePort(deadPort);

        // Assert: both starts fail on the database, not on a half-built node left over from the first one
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.IsNotType<InvalidOperationException>(second);
        Assert.Equal(first.GetType(), second.GetType());
        Assert.False(node.IsRunning);
        Assert.Throws<InvalidOperationException>(() => node.Services);
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
            await DockerDiagnostics.DumpContainerLogsAsync(["alice", "david"]);

        foreach (var node in _nodes)
        {
            try
            {
                await node.DisposeAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to dispose {node.Name}: {e.Message}");
            }
        }

        foreach (var container in _containers)
            container.Dispose();

        GC.SuppressFinalize(this);
    }

    private async Task<NLightningTestNode> StartNodeAsync(string name, TestNodeDatabase? database = null)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name, database);
        _nodes.Add(node);
        await node.StartAsync(TestContext.Current.CancellationToken);
        Console.WriteLine($"Started {node}");
        return node;
    }

    /// <returns>What happened, for the log; throws on anything but the outcomes a simultaneous connect allows.</returns>
    private static async Task<string> TryConnectAsync(NLightningTestNode from, NLightningTestNode to)
    {
        try
        {
            await from.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(to.Address));
            return "connected";
        }
        catch (InvalidOperationException e)
        {
            // The other direction won the tie-break, or was installed first
            return $"refused ({e.Message})";
        }
        catch (ConnectionException e)
        {
            // The other end kept the connection it initiated and closed this one during the init exchange
            return $"closed ({e.Message})";
        }
    }

    private static async Task DisconnectAsync(NLightningTestNode a, NLightningTestNode b, string what,
                                              CancellationToken cancellationToken)
    {
        if (a.IsConnectedTo(b.NodeId))
            a.PeerManager.DisconnectPeer(b.NodeId);
        if (b.IsConnectedTo(a.NodeId))
            b.PeerManager.DisconnectPeer(a.NodeId);
        await Poll.UntilAsync(() => !a.IsConnectedTo(b.NodeId) && !b.IsConnectedTo(a.NodeId), s_connectTimeout,
                              $"{what}: {a.Name} and {b.Name} disconnected", cancellationToken);
    }

    private static void AssertNoConnectBugLogged(IEnumerable<NLightningTestNode> nodes)
    {
        foreach (var node in nodes)
        {
            Assert.Equal(0, node.CountLogLines(NLightningTestNode.InitLostLogFragment));
            Assert.Equal(0, node.CountLogLines(NLightningTestNode.InitWriteFailedLogFragment));
        }
    }

    private static TimeSpan GetReconnectInitialDelay(NLightningTestNode node)
    {
        var property = node.PeerManager.GetType()
                           .GetProperty("ReconnectInitialDelay", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return (TimeSpan)property.GetValue(node.PeerManager)!;
    }
}
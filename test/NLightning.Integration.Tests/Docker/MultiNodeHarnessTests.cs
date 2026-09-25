using System.Reflection;

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
    /// What <c>PeerService</c> logs when the first message of a connection is not <c>init</c> (NL-239).
    /// </summary>
    private const string InitLostLogFragment = "Failed to receive init message";

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
            Assert.Equal(NLightningTestNode.FastReconnectInitialDelay, GetReconnectInitialDelay(node));
        }
    }

    [Fact]
    public async Task Given_BobAndCarol_When_ConnectingToEachOtherAtTheSameTime_Then_BothKeepOneConnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bob = await StartNodeAsync("bob");
        var carol = await StartNodeAsync("carol");
        const int roundsWanted = 5;
        const int maxAttempts = 15;
        var roundsDone = 0;
        var initRaceHits = 0;

        for (var attempt = 1; attempt <= maxAttempts && roundsDone < roundsWanted; attempt++)
        {
            var initFailuresBefore = bob.CountLogLines(InitLostLogFragment) + carol.CountLogLines(InitLostLogFragment);

            // Act: raw peer-manager connects, so nothing retries behind our back
            var outcomes = await Task.WhenAll(TryConnectAsync(bob, carol), TryConnectAsync(carol, bob));
            Console.WriteLine($"Attempt {attempt}: bob->carol {outcomes[0]}, carol->bob {outcomes[1]}");

            // Assert: had the two ends kept different connections, each would close the one the other kept, and
            // neither would stay connected
            var settled = true;
            try
            {
                await Poll.UntilAsync(() => bob.IsConnectedTo(carol.NodeId) && carol.IsConnectedTo(bob.NodeId),
                                      s_connectTimeout, $"attempt {attempt}: Bob and Carol connected", ct);
            }
            catch (TimeoutException)
            {
                settled = false;
            }

            var initLost = bob.CountLogLines(InitLostLogFragment) + carol.CountLogLines(InitLostLogFragment)
                         > initFailuresBefore;
            if (!settled)
            {
                // Only the known init race (NL-239) may break a round; a tie-break disagreement fails the test
                Assert.True(initLost, $"Attempt {attempt}: Bob and Carol did not settle on one connection");
                initRaceHits++;
                Console.WriteLine($"Attempt {attempt}: a responder lost the initiator's init (NL-239), redoing it");
            }
            else
            {
                await Poll.StaysTrueAsync(() => bob.IsConnectedTo(carol.NodeId) && carol.IsConnectedTo(bob.NodeId),
                                          TimeSpan.FromSeconds(2), $"attempt {attempt}: Bob and Carol stay connected",
                                          ct);
                Assert.Single(bob.PeerManager.ListPeers(), p => p.NodeId == carol.NodeId);
                Assert.Single(carol.PeerManager.ListPeers(), p => p.NodeId == bob.NodeId);
                roundsDone++;
            }

            // Clean up for the next attempt
            if (bob.IsConnectedTo(carol.NodeId))
                bob.PeerManager.DisconnectPeer(carol.NodeId);
            if (carol.IsConnectedTo(bob.NodeId))
                carol.PeerManager.DisconnectPeer(bob.NodeId);
            await Poll.UntilAsync(() => !bob.IsConnectedTo(carol.NodeId) && !carol.IsConnectedTo(bob.NodeId),
                                  s_connectTimeout, $"attempt {attempt}: Bob and Carol disconnected", ct);
        }

        Console.WriteLine($"{roundsDone} simultaneous connects settled, {initRaceHits} lost to the NL-239 init race");
        Assert.Equal(roundsWanted, roundsDone);
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
        if (provider == TestDatabaseProvider.Postgres)
        {
            var postgres = PostgresFixture.StartNamed("nltg-harness-postgres");
            _containers.Add(postgres);
            database = TestNodeDatabase.Postgres(postgres.ConnectionStringFor(databaseName));
        }
        else
        {
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

        // Assert: the peer came back from the server database
        Assert.Equal(provider, bob.Database.Provider);
        await Poll.UntilAsync(() => bob.IsConnectedTo(alice.LocalNodePubKeyBytes), s_connectTimeout,
                              "Bob reconnects to alice on startup", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [bob], ct);
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
            // The other end closed this connection during init because it kept the other one
            return $"closed ({e.Message})";
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
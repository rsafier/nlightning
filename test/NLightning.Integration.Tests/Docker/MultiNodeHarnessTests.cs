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
    /// This is not yet a clean proof that simultaneous connects work: two known bugs still break some rounds (a
    /// responder loses the initiator's <c>init</c>, NL-239; a responder's init write fails while the other end's
    /// tie-break keeps that dead connection, NL-240, both proposed IDs). A round broken by one of them is counted and
    /// redone; a round that breaks without one of their log lines (e.g. the two ends keeping different connections)
    /// fails the test. Once both are fixed, drop the tolerance and require every round to settle.
    /// </remarks>
    [Fact]
    public async Task Given_BobAndCarol_When_ConnectingToEachOtherAtTheSameTime_Then_BothKeepOneConnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var bob = await StartNodeAsync("bob");
        var carol = await StartNodeAsync("carol");
        NLightningTestNode[] ends = [bob, carol];
        const int roundsWanted = 5;
        const int maxAttempts = 25;
        var roundsDone = 0;
        var knownBugHits = 0;

        for (var attempt = 1; attempt <= maxAttempts && roundsDone < roundsWanted; attempt++)
        {
            var knownBugLinesBefore = NLightningTestNode.CountKnownConnectBugLines(ends);

            // Act: raw peer-manager connects, so nothing retries behind our back
            var outcomes = await Task.WhenAll(TryConnectAsync(bob, carol), TryConnectAsync(carol, bob));
            Console.WriteLine($"Attempt {attempt}: bob->carol {outcomes[0].Description}, "
                            + $"carol->bob {outcomes[1].Description}");

            // Assert: had the two ends kept different connections, each would close the one the other kept, and
            // neither would stay connected
            var failure = await bob.WaitForStableConnectionAsync(carol, ct);
            if (failure is not null)
            {
                // Only a known connect bug may break a round; a tie-break disagreement fails the test
                var knownBug = outcomes.Any(o => o.KnownBug)
                            || await NLightningTestNode.LoggedKnownConnectBugAsync(knownBugLinesBefore, ends, ct);
                Assert.True(knownBug, $"Attempt {attempt}: {failure}, and neither node logged a known connect bug");
                knownBugHits++;
                Console.WriteLine($"Attempt {attempt}: {failure} (known connect bug, NL-239/NL-240), redoing it");
            }
            else
            {
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

        Console.WriteLine($"{roundsDone} simultaneous connects settled, {knownBugHits} broken by a known connect bug");
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

    private static async Task<ConnectOutcome> TryConnectAsync(NLightningTestNode from, NLightningTestNode to)
    {
        try
        {
            await from.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(to.Address));
            return new ConnectOutcome("connected", false);
        }
        catch (InvalidOperationException e)
        {
            // The other direction won the tie-break, or was installed first
            return new ConnectOutcome($"refused ({e.Message})", false);
        }
        catch (ConnectionException e)
        {
            // The other end closed this connection during init because it kept the other one
            return new ConnectOutcome($"closed ({e.Message})", NLightningTestNode.IsKnownConnectBug(e));
        }
        catch (ErrorException e) when (NLightningTestNode.IsKnownConnectBug(e))
        {
            // Our own init write failed (NL-240)
            return new ConnectOutcome($"failed ({e.Message})", true);
        }
    }

    private sealed record ConnectOutcome(string Description, bool KnownBug);

    private static TimeSpan GetReconnectInitialDelay(NLightningTestNode node)
    {
        var property = node.PeerManager.GetType()
                           .GetProperty("ReconnectInitialDelay", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return (TimeSpan)property.GetValue(node.PeerManager)!;
    }
}
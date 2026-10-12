using System.Collections.Concurrent;

namespace NLightning.Testing.Lnd.Tests;

using Lnrpc;
using TestUtils;

public class LndNodePoolTests
{
    private const string Alice = "02aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Bob = "03bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Carol = "02cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static readonly byte[] s_certificate = TestCertificates.ToPem(TestCertificates.CreateServerCertificate());

    [Fact]
    public async Task Given_NodesToConnect_When_APassRuns_Then_OnlyTheReadyOnesAreInReadyNodes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var kit = new PoolKit();
        var alice = Settings(1);
        var bob = Settings(2);
        kit.Ready[alice.GrpcEndpoint!] = true;
        kit.Ready[bob.GrpcEndpoint!] = false;
        using var pool = kit.Create(alice, bob);

        // Act
        await pool.UpdateReadyStatesAsync(ct);

        // Assert
        Assert.Equal(2, pool.TotalNodes);
        Assert.Equal([alice.GrpcEndpoint], pool.ReadyNodes.Select(x => x.Host));
        Assert.False(pool.AllReady);
    }

    [Fact]
    public async Task Given_ANodeThatTurnsReadyAndBack_When_PassesRun_Then_ItJoinsAndLeavesReadyNodes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var kit = new PoolKit();
        var alice = Settings(1);
        kit.Ready[alice.GrpcEndpoint!] = false;
        using var pool = kit.Create(alice);
        await pool.UpdateReadyStatesAsync(ct);
        var before = pool.ReadyNodes.Count;

        // Act
        kit.Ready[alice.GrpcEndpoint!] = true;
        await pool.UpdateReadyStatesAsync(ct);
        var whenReady = pool.ReadyNodes.Count;
        var allReady = pool.AllReady;
        kit.Ready[alice.GrpcEndpoint!] = false;
        await pool.UpdateReadyStatesAsync(ct);

        // Assert
        Assert.Equal(0, before);
        Assert.Equal(1, whenReady);
        Assert.True(allReady);
        Assert.Empty(pool.ReadyNodes);
    }

    [Fact]
    public async Task Given_ANodeThatCannotBeReachedYet_When_PassesRun_Then_ItIsRetriedUntilItConnects()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var kit = new PoolKit();
        var alice = Settings(1);
        kit.Ready[alice.GrpcEndpoint!] = true;
        kit.FailConnects[alice.GrpcEndpoint!] = 2;
        using var pool = kit.Create(alice);

        // Act
        await pool.UpdateReadyStatesAsync(ct);
        await pool.UpdateReadyStatesAsync(ct);
        var afterTwoFailures = pool.Nodes.Count;
        await pool.UpdateReadyStatesAsync(ct);

        // Assert
        Assert.Equal(0, afterTwoFailures);
        Assert.Single(pool.ReadyNodes);
        Assert.Equal(3, kit.ConnectAttempts[alice.GrpcEndpoint!]);
        Assert.Equal(1, pool.TotalNodes);
    }

    [Fact]
    public async Task Given_AReadinessCheckThatThrows_When_APassRuns_Then_TheNodeIsNotReady()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = Settings(1);
        var config = new LndNodePoolConfig
        {
            StartBackgroundUpdates = false,
            ConnectionFactory = (settings, _) => Task.FromResult(LndNodeConnection.CreateWithoutNodeInfo(settings)),
            ReadinessCheck = (_, _) => throw new InvalidOperationException("boom")
        }.AddConnectionSettings(alice);
        using var pool = new LndNodePool(config);

        // Act
        await pool.UpdateReadyStatesAsync(ct);

        // Assert
        Assert.Single(pool.Nodes);
        Assert.Empty(pool.ReadyNodes);
    }

    [Fact]
    public async Task Given_NodesNeverReady_When_WaitUntilAllReady_Then_TimeoutException()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var kit = new PoolKit();
        var alice = Settings(1);
        kit.Ready[alice.GrpcEndpoint!] = false;
        using var pool = kit.Create(alice);

        // Act
        var exception = await Record.ExceptionAsync(() => pool.WaitUntilAllReadyAsync(TimeSpan.FromMilliseconds(300),
                                                                                      ct));

        // Assert
        Assert.Contains("0 of 1", Assert.IsType<TimeoutException>(exception).Message);
    }

    [Fact]
    public async Task Given_TheBackgroundLoop_When_NodesBecomeReady_Then_WaitUntilAllReadySeesThem()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var kit = new PoolKit { Background = true };
        var alice = Settings(1);
        var bob = Settings(2);
        kit.Ready[alice.GrpcEndpoint!] = true;
        kit.Ready[bob.GrpcEndpoint!] = true;
        using var pool = kit.Create(alice, bob);

        // Act
        await pool.WaitUntilAllReadyAsync(TimeSpan.FromSeconds(30), ct);

        // Assert
        Assert.True(pool.AllReady);
        Assert.Equal([alice.GrpcEndpoint, bob.GrpcEndpoint], pool.ReadyNodes.Select(x => x.Host));
    }

    [Fact]
    public async Task Given_APool_When_NodesAreAddedAndRemoved_Then_TotalsFollow()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var kit = new PoolKit();
        var alice = Settings(1);
        var bob = Settings(2);
        kit.Ready[alice.GrpcEndpoint!] = true;
        kit.Ready[bob.GrpcEndpoint!] = true;
        using var pool = kit.Create(alice);
        await pool.UpdateReadyStatesAsync(ct);

        // Act
        pool.AddNode(bob);
        var totalAfterAdd = pool.TotalNodes;
        await pool.UpdateReadyStatesAsync(ct);
        using var removed = pool.ReadyNodes[0];
        pool.RemoveNode(removed);

        // Assert
        Assert.Equal(2, totalAfterAdd);
        Assert.Equal(1, pool.TotalNodes);
        Assert.Equal([bob.GrpcEndpoint], pool.ReadyNodes.Select(x => x.Host));
        Assert.Same(pool.ReadyNodes[0], pool.GetLndNodeConnection());
    }

    [Fact]
    public async Task Given_APool_When_Disposed_Then_ItsConnectionsAreDisposed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var kit = new PoolKit();
        var alice = Settings(1);
        kit.Ready[alice.GrpcEndpoint!] = true;
        var pool = kit.Create(alice);
        await pool.UpdateReadyStatesAsync(ct);
        var node = pool.ReadyNodes[0];

        // Act
        pool.Dispose();

        // Assert
        Assert.Throws<ObjectDisposedException>(() => node.LightningClient.GetInfo(new GetInfoRequest(), cancellationToken: ct));
        Assert.Empty(pool.Nodes);
        Assert.Throws<ObjectDisposedException>(() => pool.AddNode(Settings(2)));
    }

    [Fact]
    public void Given_NoReadyNode_When_GettingAConnection_Then_InvalidOperationException()
    {
        // Arrange
        using var pool = new PoolKit().Create();

        // Act
        var exception = Record.Exception(() => pool.GetLndNodeConnection());

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void Given_AChannelBetweenPoolMembers_When_ComputingBalanceTasks_Then_TheRicherSidePaysTheExcess()
    {
        // Arrange
        var aliceChannels = new[] { Channel("1:0", Bob, capacity: 1_000_000, local: 900_000, remote: 100_000) };
        var bobChannels = new[] { Channel("1:0", Alice, capacity: 1_000_000, local: 100_000, remote: 900_000) };

        // Act
        var tasks = LndNodePool.ComputeEvenBalanceTasks([(Alice, aliceChannels), (Bob, bobChannels)], 100_000);

        // Assert
        var task = Assert.Single(tasks);
        Assert.Equal("1:0", task.ChannelPoint);
        Assert.Equal(Alice, task.SrcPk);
        Assert.Equal(Bob, task.DestPk);
        Assert.Equal(400_000, task.Amount);
    }

    [Fact]
    public void Given_ThePeerIsRicher_When_ComputingBalanceTasks_Then_ThePeerPays()
    {
        // Arrange
        var aliceChannels = new[] { Channel("2:1", Bob, capacity: 1_000_000, local: 200_000, remote: 790_000) };

        // Act
        var tasks = LndNodePool.ComputeEvenBalanceTasks([(Alice, aliceChannels), (Bob, [])], 100_000);

        // Assert
        var task = Assert.Single(tasks);
        Assert.Equal(Bob, task.SrcPk);
        Assert.Equal(Alice, task.DestPk);
        Assert.Equal(290_000, task.Amount);
    }

    [Fact]
    public void Given_ChannelsBelowTheThresholdOrToOutsiders_When_ComputingBalanceTasks_Then_NoTasks()
    {
        // Arrange
        var aliceChannels = new[]
        {
            Channel("3:0", Bob, capacity: 1_000_000, local: 540_000, remote: 450_000),
            Channel("4:0", Carol, capacity: 1_000_000, local: 1_000_000, remote: 0)
        };

        // Act
        var tasks = LndNodePool.ComputeEvenBalanceTasks([(Alice, aliceChannels), (Bob, [])], 100_000);

        // Assert
        Assert.Empty(tasks);
    }

    [Fact]
    public void Given_AnExcessOverTheMaxInFlight_When_ComputingBalanceTasks_Then_ItIsCappedBelowIt()
    {
        // Arrange
        var channel = Channel("5:0", Bob, capacity: 1_000_000, local: 1_000_000, remote: 0,
                              maxPendingMsat: 300_000_000, minHtlcMsat: 1_000);

        // Act
        var tasks = LndNodePool.ComputeEvenBalanceTasks([(Alice, [channel]), (Bob, [])], 100_000);

        // Assert
        Assert.Equal(299_999, Assert.Single(tasks).Amount);
    }

    [Fact]
    public void Given_AnExcessUnderTheMinHtlc_When_ComputingBalanceTasks_Then_NoTask()
    {
        // Arrange
        var channel = Channel("6:0", Bob, capacity: 1_000_000, local: 610_000, remote: 390_000,
                              maxPendingMsat: 990_000_000, minHtlcMsat: 200_000_000);

        // Act
        var tasks = LndNodePool.ComputeEvenBalanceTasks([(Alice, [channel]), (Bob, [])], 100_000);

        // Assert
        Assert.Empty(tasks);
    }

    private static LndSettings Settings(int index) =>
        LndSettings.FromBytes($"https://10.0.0.{index}:10009", s_certificate, [0x02, (byte)index]);

    private static Channel Channel(string channelPoint, string remotePubKey, long capacity, long local, long remote,
                                   ulong maxPendingMsat = 990_000_000, ulong minHtlcMsat = 1_000)
    {
        var constraints = new ChannelConstraints { MaxPendingAmtMsat = maxPendingMsat, MinHtlcMsat = minHtlcMsat };
        return new Channel
        {
            ChannelPoint = channelPoint,
            ChanId = 1,
            RemotePubkey = remotePubKey,
            Capacity = capacity,
            LocalBalance = local,
            RemoteBalance = remote,
            LocalConstraints = constraints,
            RemoteConstraints = constraints.Clone()
        };
    }

    /// <summary>Connections that never call a node, and readiness answered from <see cref="Ready"/> by endpoint.</summary>
    private sealed class PoolKit
    {
        public bool Background { get; init; }
        public ConcurrentDictionary<string, bool> Ready { get; } = new();
        public ConcurrentDictionary<string, int> FailConnects { get; } = new();
        public ConcurrentDictionary<string, int> ConnectAttempts { get; } = new();

        public LndNodePool Create(params LndSettings[] connectTo)
        {
            var config = new LndNodePoolConfig
            {
                StartBackgroundUpdates = Background,
                ConnectionFactory = (settings, _) =>
                {
                    var attempt = ConnectAttempts.AddOrUpdate(settings.GrpcEndpoint!, 1, (_, n) => n + 1);
                    if (attempt <= FailConnects.GetValueOrDefault(settings.GrpcEndpoint!))
                        throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable,
                                                                              "not up yet"));
                    return Task.FromResult(LndNodeConnection.CreateWithoutNodeInfo(settings));
                },
                ReadinessCheck = (node, _) => Task.FromResult(Ready.GetValueOrDefault(node.Host))
            };
            foreach (var settings in connectTo)
                config.AddConnectionSettings(settings);
            return new LndNodePool(config);
        }
    }
}
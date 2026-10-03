namespace NLightning.Testing.Cluster.Tests.Topology;

using Cluster.Nodes;
using Cluster.Topology;

public class TopologyDeployerTests
{
    [Fact]
    public async Task Given_NodesAtTheTip_When_Waited_Then_TheTipIsReturned()
    {
        // Arrange
        var chain = new FakeChain(new FakeNodeHandle("miner", NodeKind.BitcoinCore)) { Tip = 108 };
        var nodes = new[] { new FakeLightningNode("alice") { Height = 108 }, new FakeLightningNode("bob") { Height = 108 } };

        // Act
        var tip = await TopologyDeployer.WaitAllAtTipAsync(chain, nodes, TimeSpan.FromSeconds(5),
                                                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(108, tip);
    }

    [Fact]
    public async Task Given_ANodeBehind_When_Waited_Then_TheTimeoutNamesIt()
    {
        // Arrange
        var chain = new FakeChain(new FakeNodeHandle("miner", NodeKind.BitcoinCore)) { Tip = 108 };
        var nodes = new[] { new FakeLightningNode("alice") { Height = 108 }, new FakeLightningNode("bob") { Height = 102 } };

        // Act
        var exception = await Assert.ThrowsAsync<TimeoutException>(
                            () => TopologyDeployer.WaitAllAtTipAsync(chain, nodes, TimeSpan.FromMilliseconds(300),
                                                                     TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("tip 108, bob at 102", exception.Message);
    }

    [Fact]
    public async Task Given_ConnectsRefusedAtFirst_When_Connecting_Then_ItRetriesUntilOneSucceeds()
    {
        // Arrange
        var alice = new FakeLightningNode("alice") { FailingConnects = 2 };

        // Act
        await TopologyDeployer.ConnectAsync(alice, new TestPeerAddress("id-bob", "bob-p2p", 9735),
                                            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, alice.ConnectAttempts);
    }

    [Fact]
    public async Task Given_ConnectsAlwaysRefused_When_Connecting_Then_TheTimeoutCarriesTheLastError()
    {
        // Arrange
        var alice = new FakeLightningNode("alice") { FailingConnects = int.MaxValue };

        // Act
        var exception = await Assert.ThrowsAsync<TimeoutException>(
                            () => TopologyDeployer.ConnectAsync(alice, new TestPeerAddress("id-bob", "bob-p2p", 9735),
                                                                TimeSpan.FromMilliseconds(200),
                                                                TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("alice connected to id-bob@bob-p2p:9735", exception.Message);
        Assert.Contains(": refused ", exception.Message);
    }

    [Fact]
    public async Task Given_AnActiveChannel_When_Waited_Then_ItReturns()
    {
        // Arrange
        var alice = new FakeLightningNode("alice");
        alice.Channels.Add(new TestChannel("id-bob", "other", 0, null, 1, 0, false));
        alice.Channels.Add(new TestChannel("id-bob", "txid", 0, "108x1x0", 1_000_000, 0, true));

        // Act + Assert (no timeout)
        await TopologyDeployer.WaitChannelActiveAsync(alice, "id-bob", "txid", TimeSpan.FromSeconds(5),
                                                      TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_AnInactiveChannel_When_Waited_Then_TheTimeoutSaysSo()
    {
        // Arrange
        var alice = new FakeLightningNode("alice");
        alice.Channels.Add(new TestChannel("id-bob", "txid", 0, null, 1_000_000, 0, false));

        // Act
        var exception = await Assert.ThrowsAsync<TimeoutException>(
                            () => TopologyDeployer.WaitChannelActiveAsync(alice, "id-bob", "txid",
                                                                          TimeSpan.FromMilliseconds(200),
                                                                          TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("alice's channel txid active", exception.Message);
        Assert.EndsWith("listed, not active", exception.Message);
    }
}
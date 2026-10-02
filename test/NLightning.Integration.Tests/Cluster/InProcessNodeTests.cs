namespace NLightning.Integration.Tests.Cluster;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Testing.Cluster.Nodes;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;

/// <summary>
/// The pure parts of the in-process node glue (no cluster, no node): peer host resolution, the facade mappings, the
/// open request, the chain endpoint and the node settings.
/// </summary>
public class InProcessNodeTests
{
    private static readonly RunIdentity s_run =
        RunIdentity.Create(new TestRunOptions { Suite = "unit", RunId = "abc" }, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("lnd", "lnd.nltg-spike-abc.svc.cluster.local")]
    [InlineData("cln-p2p.nltg-spike-abc.svc.cluster.local", "cln-p2p.nltg-spike-abc.svc.cluster.local")]
    [InlineData("192.168.194.7", "192.168.194.7")]
    [InlineData("host.orb.internal", "host.orb.internal")]
    public void Given_APeerHost_When_Resolved_Then_ABareAliasGetsTheRunsServiceName(string host, string expected)
    {
        // Act
        var resolved = InProcessNode.ResolvePeerHost(host, s_run);

        // Assert
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void Given_NoRun_When_ABareAliasIsResolved_Then_ItIsKept()
    {
        // Act + Assert
        Assert.Equal("lnd", InProcessNode.ResolvePeerHost("lnd", null));
    }

    [Fact]
    public void Given_AnOpenChannel_When_Mapped_Then_TheFacadeHasDisplayTxIdScidAndBalances()
    {
        // Arrange
        var txIdBytes = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var peer = new CompactPubKey(Convert.FromHexString("02" + new string('a', 64)));
        var channel = Channel(ChannelState.Open, txIdBytes, peer, connected: true, reestablished: true);

        // Act
        var mapped = InProcessNode.ToTestChannel(channel);

        // Assert
        Assert.Equal("02" + new string('a', 64), mapped.RemoteNodeId);
        Assert.Equal(Convert.ToHexStringLower(txIdBytes.Reverse().ToArray()), mapped.FundingTxId);
        Assert.Equal(1, mapped.OutputIndex);
        Assert.Equal("103x2x1", mapped.ShortChannelId);
        Assert.Equal(1_000_000, mapped.CapacitySat);
        Assert.Equal(700_000_000, mapped.LocalBalanceMsat);
        Assert.True(mapped.Active);
    }

    [Theory]
    [InlineData(ChannelState.Open, false, true)]
    [InlineData(ChannelState.Open, true, false)]
    [InlineData(ChannelState.ShuttingDown, true, true)]
    public void Given_AChannelNotUsable_When_Mapped_Then_ItIsNotActive(ChannelState state, bool connected,
                                                                         bool reestablished)
    {
        // Arrange
        var channel = Channel(state, new byte[32], new CompactPubKey(Convert.FromHexString("03" + new string('b', 64))),
                              connected, reestablished);

        // Act + Assert
        Assert.False(InProcessNode.ToTestChannel(channel).Active);
    }

    [Theory]
    [InlineData(InProcessOpenMode.V1, false, true)]
    [InlineData(InProcessOpenMode.DualFund, true, false)]
    [InlineData(InProcessOpenMode.Auto, false, false)]
    public void Given_AnOpenMode_When_TheRequestIsBuilt_Then_ItSelectsTheOpen(InProcessOpenMode mode, bool dualFunded,
                                                                           bool forceV1)
    {
        // Act
        var request = InProcessNode.BuildOpenRequest(new TestOpenChannelRequest("02ab", 500_000, 0, true), mode);

        // Assert
        Assert.Equal("02ab", request.NodeInfo);
        Assert.Equal(LightningMoney.Satoshis(500_000), request.FundingAmount);
        Assert.Null(request.PushAmount);
        Assert.True(request.IsPublic);
        Assert.Equal(dualFunded, request.IsDualFunded);
        Assert.Equal(forceV1, request.ForceV1);
    }

    [Fact]
    public void Given_APush_When_AV1RequestIsBuilt_Then_ThePushIsInMsat()
    {
        // Act
        var request = InProcessNode.BuildOpenRequest(new TestOpenChannelRequest("02ab", 500_000, 123_456),
                                                     InProcessOpenMode.V1);

        // Assert
        Assert.Equal(LightningMoney.MilliSatoshis(123_456), request.PushAmount);
    }

    [Fact]
    public void Given_APush_When_ADualFundedRequestIsBuilt_Then_ItIsRefused()
    {
        // Act + Assert
        Assert.Throws<ArgumentException>(() => InProcessNode.BuildOpenRequest(
                                             new TestOpenChannelRequest("02ab", 500_000, 1_000),
                                             InProcessOpenMode.DualFund));
    }

    [Fact]
    public void Given_PaymentsInEveryState_When_Mapped_Then_OnlyASucceededOneCarriesThePreimage()
    {
        // Arrange
        var preimage = Enumerable.Repeat((byte)7, 32).ToArray();

        // Act
        var succeeded = InProcessNode.ToPaymentResult(Payment(PaymentStatus.Succeeded, preimage, null));
        var failed = InProcessNode.ToPaymentResult(Payment(PaymentStatus.Failed, null, "no route"));
        var inFlight = InProcessNode.ToPaymentResult(Payment(PaymentStatus.InFlight, null, null));

        // Assert
        Assert.True(succeeded.Succeeded);
        Assert.Equal(Convert.ToHexStringLower(preimage), succeeded.PreimageHex);
        Assert.False(failed.Succeeded);
        Assert.Equal("no route", failed.FailureReason);
        Assert.False(inFlight.Succeeded);
        Assert.Equal("still InFlight", inFlight.FailureReason);
    }

    [Theory]
    [InlineData(false, "192.168.194.9")]
    [InlineData(true, "miner.nltg-spike-abc.svc.cluster.local")]
    public void Given_AChain_When_TheEndpointIsCreated_Then_ItUsesThePodIpOnTheHostAndTheServiceInTheCluster(
        bool inCluster, string expectedHost)
    {
        // Arrange
        var handle = new Mock<INodeHandle>();
        handle.SetupGet(h => h.Name).Returns("miner");
        handle.SetupGet(h => h.PodIp).Returns("192.168.194.9");
        handle.SetupGet(h => h.ServiceDnsName).Returns("miner.nltg-spike-abc.svc.cluster.local");
        var chain = new Mock<ITopologyChain>();
        chain.SetupGet(c => c.Node).Returns(handle.Object);
        chain.SetupGet(c => c.RpcPort).Returns(18443);
        chain.SetupGet(c => c.RpcUser).Returns("nltg");
        chain.SetupGet(c => c.RpcPassword).Returns("secret");
        chain.SetupGet(c => c.ZmqRawBlockPort).Returns(28332);
        chain.SetupGet(c => c.ZmqRawTxPort).Returns(28333);

        // Act
        var endpoint = ClusterChainEndpoint.Create(chain.Object, inCluster);

        // Assert
        Assert.Equal(expectedHost, endpoint.ZmqHost);
        Assert.Equal(28332, endpoint.ZmqBlockPort);
        Assert.Equal(28333, endpoint.ZmqTxPort);
        Assert.Equal(expectedHost, endpoint.Rpc.Address.Host);
        Assert.Equal(18443, endpoint.Rpc.Address.Port);
        Assert.Equal("nltg", endpoint.Rpc.CredentialString.UserPassword.UserName);
    }

    [Fact]
    public void Given_AChainPodWithoutIp_When_TheHostEndpointIsCreated_Then_ItThrows()
    {
        // Arrange
        var handle = new Mock<INodeHandle>();
        handle.SetupGet(h => h.Name).Returns("miner");

        // Act + Assert
        Assert.Throws<InvalidOperationException>(() => ClusterChainEndpoint.HostFor(handle.Object, false));
    }

    [Fact]
    public void Given_NodeSettings_When_Parsed_Then_EachIsAConfigurationEntry()
    {
        // Act
        var settings = InProcessNodeDeployer.ParseSettings(["Node:Alias=nltg", "Gossip:Enabled = false", "A:B="]);

        // Assert
        Assert.Equal([
                         new KeyValuePair<string, string?>("Node:Alias", "nltg"),
                         new KeyValuePair<string, string?>("Gossip:Enabled", " false"),
                         new KeyValuePair<string, string?>("A:B", string.Empty)
                     ], settings);
    }

    [Theory]
    [InlineData("--experimental-dual-fund")]
    [InlineData("=value")]
    public void Given_AnArgumentThatIsNoSetting_When_Parsed_Then_ItIsRefused(string arg)
    {
        // Act + Assert
        Assert.Throws<ArgumentException>(() => InProcessNodeDeployer.ParseSettings([arg]));
    }

    [Fact]
    public void Given_ATopology_When_AnInProcessNodeIsDeclared_Then_ItIsAnNLightningNodeWithItsSettings()
    {
        // Act
        var spec = new TopologyBuilder().AddBitcoinCore("miner")
                                        .AddNLightning("nltg", "Node:Alias=x")
                                        .UseInProcessNodes(new InProcessNodeDeployer())
                                        .Build();

        // Assert
        var node = spec.GetNode("nltg");
        Assert.Equal(NodeKind.NLightning, node.Kind);
        Assert.Null(node.Image);
        Assert.Equal(["Node:Alias=x"], node.Args);
    }

    [Fact]
    public void Given_AnNLightningNodeWithoutTheDeployer_When_Built_Then_TheMissingDeployerIsNamed()
    {
        // Act
        var e = Assert.Throws<ArgumentException>(() => new TopologyBuilder().AddBitcoinCore("miner")
                                                                             .AddNLightning("nltg")
                                                                             .Build());

        // Assert
        Assert.Contains("NLightning", e.Message);
    }

    private static ChannelInfoClientResponse Channel(ChannelState state, byte[] txId, CompactPubKey peer,
                                                     bool connected, bool reestablished) =>
        new()
        {
            ChannelId = new ChannelId(new byte[32]),
            PeerId = peer,
            State = state,
            IsPeerConnected = connected,
            IsReestablished = reestablished,
            FundingTxId = new TxId(txId),
            FundingOutputIndex = 1,
            ShortChannelId = new ShortChannelId(103, 2, 1),
            Capacity = LightningMoney.Satoshis(1_000_000),
            LocalBalance = LightningMoney.MilliSatoshis(700_000_000),
            RemoteBalance = LightningMoney.MilliSatoshis(300_000_000)
        };

    private static PaymentInfoClientResponse Payment(PaymentStatus status, byte[]? preimage, string? reason) =>
        new()
        {
            PaymentHash = new Hash(new byte[32]),
            PayeeNodeId = new CompactPubKey(Convert.FromHexString("02" + new string('c', 64))),
            Amount = LightningMoney.Satoshis(1),
            Fee = LightningMoney.Zero,
            Status = status,
            Preimage = preimage is null ? (Secret?)null : new Secret(preimage),
            FailureReason = reason,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
}
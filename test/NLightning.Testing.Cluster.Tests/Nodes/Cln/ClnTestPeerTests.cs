namespace NLightning.Testing.Cluster.Tests.Nodes.Cln;

using Cluster.Nodes;
using Cluster.Nodes.Cln;
using Topology;

public class ClnTestPeerTests
{
    private static string Method(IReadOnlyList<string> command) => command[4];

    private static string? Arg(IReadOnlyList<string> command, string key) =>
        command.Skip(5).FirstOrDefault(a => a.StartsWith($"{key}=", StringComparison.Ordinal))?[(key.Length + 1)..];

    [Fact]
    public async Task Given_APeer_When_ItsNodeIdIsReadTwice_Then_GetInfoRunsOnceAndTheIdIsLowerCase()
    {
        // Arrange
        var node = new FakeNodeHandle("alice") { Respond = _ => FakeNodeHandle.Ok("{\"id\":\"02ABCD\"}") };
        var peer = new ClnTestPeer(node);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var first = await peer.GetNodeIdAsync(ct);
        var second = await peer.GetNodeIdAsync(ct);

        // Assert
        Assert.Equal("02abcd", first);
        Assert.Equal(first, second);
        Assert.Single(node.Commands);
    }

    [Fact]
    public async Task Given_APeerWithAndWithoutAP2PHost_When_ItsAddressIsRead_Then_PeersDialThatHostOrTheService()
    {
        // Arrange
        var node = new FakeNodeHandle("bob") { Respond = _ => FakeNodeHandle.Ok("{\"id\":\"03ef\"}") };
        var ct = TestContext.Current.CancellationToken;

        // Act
        var stable = await new ClnTestPeer(node, "bob-p2p.nltg-spike-r1.svc.cluster.local").GetAddressAsync(ct);
        var headless = await new ClnTestPeer(node).GetAddressAsync(ct);

        // Assert
        Assert.Equal("03ef@bob-p2p.nltg-spike-r1.svc.cluster.local:9735", stable.ToString());
        Assert.Equal(new TestPeerAddress("03ef", "bob.nltg-spike-r1.svc.cluster.local", 9735), headless);
    }

    [Fact]
    public async Task Given_AnAddress_When_Connecting_Then_ConnectGetsTheIdHostAndPort()
    {
        // Arrange
        var node = new FakeNodeHandle("alice");
        var peer = new ClnTestPeer(node);

        // Act
        await peer.ConnectAsync(new TestPeerAddress("03ef", "bob-p2p", 9735), TestContext.Current.CancellationToken);

        // Assert
        var command = Assert.Single(node.Commands);
        Assert.Equal("connect", Method(command));
        Assert.Equal(["id=03ef", "host=bob-p2p", "port=9735"], command.Skip(5));
    }

    [Theory]
    [InlineData(0, false, null)]
    [InlineData(100_000_000, true, "100000000")]
    public void Given_AnOpenRequest_When_FundChannelParametersAreBuilt_Then_PushIsSentOnlyWhenPositive(
        long pushMsat, bool announce, string? expectedPush)
    {
        // Act
        var parameters = ClnTestPeer.BuildFundChannelParameters(
            new TestOpenChannelRequest("03ef", 1_000_000, pushMsat, announce));
        var command = ClnRpc.BuildCommand("fundchannel", parameters);

        // Assert
        Assert.Equal("03ef", Arg(command, "id"));
        Assert.Equal("1000000", Arg(command, "amount"));
        Assert.Equal(announce ? "true" : "false", Arg(command, "announce"));
        Assert.Equal(expectedPush, Arg(command, "push_msat"));
    }

    [Fact]
    public async Task Given_FundChannelAnswers_When_AChannelIsOpened_Then_TheFundingOutpointIsReturned()
    {
        // Arrange
        var node = new FakeNodeHandle("alice")
        {
            Respond = _ => FakeNodeHandle.Ok("{\"txid\":\"ABCD\",\"outnum\":1,\"channel_id\":\"ff\"}")
        };

        // Act
        var open = await new ClnTestPeer(node).OpenChannelAsync(new TestOpenChannelRequest("03ef", 500_000),
                                                                TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new TestChannelOpen("abcd", 1), open);
    }

    [Theory]
    [InlineData(50_000L, "50000")]
    [InlineData(null, "any")]
    public async Task Given_AnAmountOrNone_When_AnInvoiceIsCreated_Then_ItIsLabelledUniquely(long? amountMsat,
                                                                                            string expected)
    {
        // Arrange
        var node = new FakeNodeHandle("bob")
        {
            Respond = _ => FakeNodeHandle.Ok("{\"bolt11\":\"lnbcrt1\",\"payment_hash\":\"AB\"}")
        };
        var peer = new ClnTestPeer(node);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var invoice = await peer.CreateInvoiceAsync(amountMsat, "spike", ct);
        await peer.CreateInvoiceAsync(amountMsat, "spike", ct);

        // Assert
        Assert.Equal(new TestInvoice("lnbcrt1", "ab"), invoice);
        Assert.Equal(expected, Arg(node.Commands[0], "amount_msat"));
        Assert.Equal("spike", Arg(node.Commands[0], "description"));
        Assert.StartsWith("nltg-", Arg(node.Commands[0], "label"));
        Assert.NotEqual(Arg(node.Commands[0], "label"), Arg(node.Commands[1], "label"));
    }

    [Fact]
    public async Task Given_XpayFails_When_AnInvoiceIsPaid_Then_TheFailureIsAResult()
    {
        // Arrange
        var node = new FakeNodeHandle("alice")
        {
            Respond = _ => FakeNodeHandle.Fail(1, "{\"code\":209,\"message\":\"Failed after 3 attempts\"}")
        };

        // Act
        var result = await new ClnTestPeer(node).PayInvoiceAsync("lnbcrt1", TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("Failed after 3 attempts", result.FailureReason);
        var command = Assert.Single(node.Commands);
        Assert.Equal("xpay", Method(command));
        Assert.Equal($"{ClnTestPeer.PayRetrySeconds}", Arg(command, "retry_for"));
    }

    [Fact]
    public async Task Given_GetInfoAndListFunds_When_HeightAndBalanceAreRead_Then_TheyComeFromCln()
    {
        // Arrange
        var node = new FakeNodeHandle("alice")
        {
            Respond = c => Method(c) == "getinfo"
                               ? FakeNodeHandle.Ok("{\"id\":\"02ab\",\"blockheight\":108}")
                               : FakeNodeHandle.Ok(
                                   "{\"outputs\":[{\"amount_msat\":2000000000,\"status\":\"confirmed\"}]}")
        };
        var peer = new ClnTestPeer(node);
        var ct = TestContext.Current.CancellationToken;

        // Act + Assert
        Assert.Equal(108, await peer.GetBlockHeightAsync(ct));
        Assert.Equal(2_000_000, await peer.GetConfirmedBalanceSatAsync(ct));
    }

    [Fact]
    public async Task Given_ANodeOutsideKubernetes_When_Crashed_Then_ItIsNotSupported()
    {
        // Arrange
        var peer = new ClnTestPeer(new FakeNodeHandle("alice"));

        // Act + Assert
        await Assert.ThrowsAsync<NotSupportedException>(() => peer.CrashAsync(TimeSpan.FromSeconds(1),
                                                                               TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Given_TheCrashCommand_When_Read_Then_ItKillsLightningdFromItsPidFile()
    {
        // Act
        var script = ClnTestPeer.CrashCommand[2];

        // Assert
        Assert.Contains("/root/.lightning/lightningd-regtest.pid", script);
        Assert.Contains("kill -9", script);
    }
}
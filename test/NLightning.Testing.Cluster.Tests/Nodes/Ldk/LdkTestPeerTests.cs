using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Tests.Nodes.Ldk;

using Cluster.Nodes;
using Cluster.Nodes.Ldk;
using Topology;

public class LdkTestPeerTests
{
    private const string LdkId = "02ba1becfb168dfb0bd4acae1c62d3aefb8f980708609017c25f88479cb4215b70";
    private const string ClnId = "024e4f3984ec7f48db10d296d60ea3864a0e90c5e7f38f1fc7d38d828c11403622";
    private const string FundingTxId = "a6ed0410e225e6969c4aad8fcb58f80cfef9230e359564c119d875f29e32fba3";

    /// <summary>ldk-server dc02b76c's <c>list-channels</c> (captured in the live LDK topology test, trimmed).</summary>
    private const string ListChannels =
        $$$"""
          {"channels":[{"channel_id":"a3fb329ef275d819c16495350e23f9fe0cf858cb8fad4a9c96e625e21004eda7",
          "counterparty_node_id":"{{{ClnId}}}",
          "funding_txo":{"txid":"{{{FundingTxId}}}","vout":1},
          "user_channel_id":"106892875166137175507500144775613161306","unspendable_punishment_reserve":10000,
          "channel_value_sats":1000000,"feerate_sat_per_1000_weight":253,"outbound_capacity_msat":889340000,
          "inbound_capacity_msat":90000000,"confirmations_required":1,"confirmations":6,"is_outbound":true,
          "is_channel_ready":true,"is_usable":true,"is_announced":false,"short_channel_id":118747255865345,
          "channel_type":{"12":{"name":"StaticRemoteKey","is_required":true},
          "22":{"name":"AnchorsZeroFeeHtlcTx","is_required":true}} }]}
          """;

    private static FakeNodeHandle Handle() => new("ldk", NodeKind.Ldk);

    [Fact]
    public void Given_CapturedListChannels_When_Mapped_Then_TheFacadeChannelHasTheFundingScidAndOurBalance()
    {
        // Act
        var channel = Assert.Single(LdkJson.ToTestChannels(JsonNode.Parse(ListChannels)!));

        // Assert
        Assert.Equal(ClnId, channel.RemoteNodeId);
        Assert.Equal(FundingTxId, channel.FundingTxId);
        Assert.Equal(1, channel.OutputIndex);
        Assert.Equal("108x1x1", channel.ShortChannelId);
        Assert.Equal(1_000_000, channel.CapacitySat);
        Assert.Equal(889_340_000 + 10_000_000, channel.LocalBalanceMsat);
        Assert.True(channel.Active);
    }

    [Fact]
    public void Given_AChannelBeforeItsFundingAndScid_When_Mapped_Then_ThoseAreEmptyAndItIsNotActive()
    {
        // Arrange
        var json = JsonNode.Parse($$"""{"counterparty_node_id":"{{ClnId}}","channel_value_sats":"5000","is_usable":false}""")!;

        // Act
        var channel = LdkJson.ToTestChannel(json);

        // Assert
        Assert.Equal(string.Empty, channel.FundingTxId);
        Assert.Null(channel.OutputIndex);
        Assert.Null(channel.ShortChannelId);
        Assert.Equal(5_000, channel.CapacitySat);
        Assert.False(channel.Active);
    }

    [Fact]
    public void Given_ASucceededPay_When_Mapped_Then_ThePreimageIsFoundInItsDetails()
    {
        // Arrange
        var json = JsonNode.Parse("""
                                  {"payment":{"id":"ab","status":"SUCCEEDED","kind":{"kind":{"bolt11":
                                  {"hash":"cd","preimage":"EF01","secret":"23"}}}}}
                                  """)!;

        // Act
        var result = LdkJson.ToPaymentResult(json);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Equal("ef01", result.PreimageHex);
    }

    [Fact]
    public void Given_APendingPay_When_Mapped_Then_ItIsAFailedResultWithItsStatus()
    {
        // Act
        var result = LdkJson.ToPaymentResult(JsonNode.Parse("""{"status":"PENDING"}""")!);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("PENDING", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Given_AnOpenRequest_When_TheArgsAreBuilt_Then_PushAndAnnounceAreFlags()
    {
        // Act
        var args = LdkTestPeer.BuildOpenChannelArgs(new TestOpenChannelRequest(ClnId, 1_000_000, 100_000_000, true),
                                                    "10.0.0.2:9735");

        // Assert
        Assert.Equal([ClnId, "10.0.0.2:9735", "1000000sat", "--push-to-counterparty", "100000000msat",
                      "--announce-channel"], args);
    }

    [Fact]
    public async Task Given_ANode_When_ItsIdAndAddressAreRead_Then_TheyComeFromGetNodeInfoAndTheStableHost()
    {
        // Arrange
        var handle = Handle();
        handle.Respond = _ => FakeNodeHandle.Ok($$$"""{"node_id":"{{{LdkId.ToUpperInvariant()}}}","current_best_block":{"height":113}}""");
        var peer = new LdkTestPeer(handle, "10.43.0.7");

        // Act
        var address = await peer.GetAddressAsync(TestContext.Current.CancellationToken);
        var height = await peer.GetBlockHeightAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new TestPeerAddress(LdkId, "10.43.0.7", 9735), address);
        Assert.Equal(113, height);
        Assert.Equal(["ldk-server-cli", "-c", "/data/config.toml", "get-node-info"], handle.Commands[0]);
    }

    [Fact]
    public async Task Given_AConnectedPeer_When_LdkOpens_Then_ItDialsTheListedAddressAndReturnsTheFunding()
    {
        // Arrange
        var handle = Handle();
        handle.Respond = command => command[3] switch
        {
            "list-peers" => FakeNodeHandle.Ok($$"""{"peers":[{"node_id":"{{ClnId}}","address":"10.0.0.2:9735","is_connected":true}]}"""),
            "open-channel" => FakeNodeHandle.Ok("""{"user_channel_id":"7"}"""),
            "list-channels" => FakeNodeHandle.Ok($$$"""{"channels":[{"user_channel_id":"7","funding_txo":{"txid":"{{{FundingTxId.ToUpperInvariant()}}}","vout":0}}]}"""),
            _ => FakeNodeHandle.Fail(1, string.Empty, "unexpected")
        };
        var peer = new LdkTestPeer(handle, "10.43.0.7");

        // Act
        var open = await peer.OpenChannelAsync(new TestOpenChannelRequest(ClnId, 500_000),
                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new TestChannelOpen(FundingTxId, 0), open);
        Assert.Contains(handle.Commands, c => c.SequenceEqual(["ldk-server-cli", "-c", "/data/config.toml",
                                                              "open-channel", ClnId, "10.0.0.2:9735", "500000sat"]));
    }

    [Fact]
    public async Task Given_AFailedPay_When_Paid_Then_ItIsAFailedResultNotAnException()
    {
        // Arrange
        var handle = Handle();
        handle.Respond = _ => FakeNodeHandle.Fail(2, """{"status":"FAILED"}""", "Error: payment failed");
        var peer = new LdkTestPeer(handle);

        // Act
        var result = await peer.PayInvoiceAsync("lnbcrt1", TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("payment failed", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(["ldk-server-cli", "-c", "/data/config.toml", "pay", "lnbcrt1", "--wait", "--wait-timeout", "60"],
                     handle.Commands[0]);
    }

    [Fact]
    public void Given_AFailedCallWithJson_When_Parsed_Then_TheExceptionCarriesTheJsonAndTheExitCode()
    {
        // Act
        var exception = Assert.Throws<LdkRpcException>(
            () => LdkRpc.ParseResult("pay", FakeNodeHandle.Fail(3, """{"status":"PENDING"}""", "timed out")));

        // Assert
        Assert.Equal(3, exception.ExitCode);
        Assert.Equal("PENDING", exception.Json?["status"]?.GetValue<string>());
        Assert.Contains("timed out", exception.LdkMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Given_ListPeers_When_Asked_Then_OnlyAConnectedEntryCounts()
    {
        // Arrange
        var peers = JsonNode.Parse($$"""{"peers":[{"node_id":"{{ClnId}}","is_connected":false}]}""")!;

        // Act + Assert
        Assert.False(LdkJson.IsConnected(peers, ClnId));
        Assert.False(LdkJson.IsConnected(peers, LdkId));
        Assert.True(LdkJson.IsConnected(JsonNode.Parse($$"""{"peers":[{"node_id":"{{ClnId}}","is_connected":true}]}""")!,
                                        ClnId.ToUpperInvariant()));
    }
}
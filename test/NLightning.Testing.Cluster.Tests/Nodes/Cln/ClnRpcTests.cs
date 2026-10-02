using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Tests.Nodes.Cln;

using Cluster.Nodes.Cln;
using Topology;

public class ClnRpcTests
{
    [Fact]
    public void Given_Parameters_When_TheCommandIsBuilt_Then_ValuesAreKeyValuePairsInTheInvariantCulture()
    {
        // Act
        var command = ClnRpc.BuildCommand("fundchannel",
                                          [
                                              ("id", "02ab"), ("amount", 1_000_000L), ("announce", false),
                                              ("feerate", 1.5), ("utxos", new JsonArray("a:0"))
                                          ]);

        // Assert
        Assert.Equal(
        [
            "lightning-cli", "--network=regtest", "--notifications=none", "-k", "fundchannel", "id=02ab",
            "amount=1000000", "announce=false", "feerate=1.5", "utxos=[\"a:0\"]"
        ], command);
    }

    [Fact]
    public void Given_NotificationLinesBeforeTheResult_When_Parsed_Then_TheyAreSkipped()
    {
        // Arrange
        var result = FakeNodeHandle.Ok("# progress 1\n# progress 2\n{\"payment_preimage\":\"ab\"}");

        // Act
        var json = ClnRpc.ParseResult("xpay", result);

        // Assert
        Assert.Equal("ab", json["payment_preimage"]!.GetValue<string>());
    }

    [Fact]
    public void Given_AnErrorObject_When_Parsed_Then_TheExceptionCarriesItsCodeAndMessage()
    {
        // Arrange
        var result = FakeNodeHandle.Fail(1, "{\"code\":401,\"message\":\"All addresses failed\"}");

        // Act
        var exception = Assert.Throws<ClnRpcException>(() => ClnRpc.ParseResult("connect", result));

        // Assert
        Assert.Equal("connect", exception.Method);
        Assert.Equal(401, exception.Code);
        Assert.Equal("All addresses failed", exception.ClnMessage);
    }

    [Fact]
    public void Given_AFailureWithoutJson_When_Parsed_Then_TheExceptionCarriesTheExitCodeAndStdErr()
    {
        // Arrange
        var result = FakeNodeHandle.Fail(2, string.Empty, "lightning-cli: Connecting to 'lightning-rpc': refused");

        // Act
        var exception = Assert.Throws<ClnRpcException>(() => ClnRpc.ParseResult("getinfo", result));

        // Assert
        Assert.Equal(2, exception.Code);
        Assert.Contains("Connecting to 'lightning-rpc'", exception.ClnMessage);
    }

    [Fact]
    public async Task Given_ANode_When_Called_Then_LightningCliRunsInItAndTheResultIsReturned()
    {
        // Arrange
        var node = new FakeNodeHandle("alice") { Respond = _ => FakeNodeHandle.Ok("{\"id\":\"02ab\"}") };
        var rpc = new ClnRpc(node);

        // Act
        var info = await rpc.GetInfoAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("02ab", info["id"]!.GetValue<string>());
        Assert.Equal(["lightning-cli", "--network=regtest", "--notifications=none", "-k", "getinfo"],
                     Assert.Single(node.Commands));
    }
}
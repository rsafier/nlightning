using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json.Linq;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;

/// <summary>
/// NL-1094: <see cref="BitcoinChainService.GetMempoolSpendersAsync"/> (<c>gettxspendingprevout</c>, the mempool poll of
/// the poll-only chain monitor): the request, the reading of the answer (Core and rbitcoin give the same shape) and the
/// feature detection against a node without the method.
/// </summary>
public class BitcoinChainServiceMempoolSpendersTests
{
    private static readonly uint256 s_spent = new(Enumerable.Repeat((byte)0x11, 32).ToArray());
    private static readonly uint256 s_spender = new(Enumerable.Repeat((byte)0x22, 32).ToArray());

    [Fact]
    public void Given_Outpoints_When_RequestBuilt_Then_OneArrayOfTxidVoutObjects()
    {
        // Act
        var request = BitcoinChainService.CreateTxSpendingPrevOutRequest([new OutPoint(s_spent, 3)]);
        using var writer = new StringWriter();
        request.WriteJSON(writer);
        var json = JObject.Parse(writer.ToString());

        // Assert
        Assert.Equal("gettxspendingprevout", json["method"]!.Value<string>());
        var outputs = Assert.IsType<JArray>(Assert.Single(Assert.IsType<JArray>(json["params"])));
        var output = Assert.IsType<JObject>(Assert.Single(outputs));
        Assert.Equal(s_spent.ToString(), output["txid"]!.Value<string>());
        Assert.Equal(3, output["vout"]!.Value<int>());
        Assert.Equal(2, output.Count);
    }

    [Fact]
    public void Given_AnAnswerWithSpentAndUnspentOutputs_When_Parsed_Then_OnlyTheSpentOnesAreReturned()
    {
        // Arrange
        var answer = JArray.Parse($$"""
            [ { "txid": "{{s_spent}}", "vout": 0, "spendingtxid": "{{s_spender}}" },
              { "txid": "{{s_spent}}", "vout": 1 },
              { "txid": "not-a-txid", "vout": 2, "spendingtxid": "{{s_spender}}" } ]
            """);

        // Act
        var spenders = BitcoinChainService.ParseTxSpendingPrevOutResponse(answer).ToList();

        // Assert
        var (outPoint, spender) = Assert.Single(spenders);
        Assert.Equal(new OutPoint(s_spent, 0), outPoint);
        Assert.Equal(s_spender, spender);
    }

    [Fact]
    public async Task Given_ANodeAnswering_When_Asked_Then_TheSpendersAreReturned()
    {
        // Arrange
        using var node = new FakeRpcNode(_ => $$"""
            {"result":[{"txid":"{{s_spent}}","vout":0,"spendingtxid":"{{s_spender}}"},{"txid":"{{s_spent}}","vout":1}],
             "error":null,"id":1}
            """);
        var service = node.CreateService();

        // Act
        var spenders = await service.GetMempoolSpendersAsync([new OutPoint(s_spent, 0), new OutPoint(s_spent, 1)]);

        // Assert
        Assert.NotNull(spenders);
        Assert.Equal(s_spender, Assert.Single(spenders).Value);
    }

    [Fact]
    public async Task Given_ANodeWithoutTheMethod_When_AskedTwice_Then_NullAndAskedOnlyOnce()
    {
        // Arrange
        using var node = new FakeRpcNode(
            _ => """{"result":null,"error":{"code":-32601,"message":"Method not found"},"id":1}""");
        var service = node.CreateService();

        // Act
        var first = await service.GetMempoolSpendersAsync([new OutPoint(s_spent, 0)]);
        var second = await service.GetMempoolSpendersAsync([new OutPoint(s_spent, 0)]);

        // Assert
        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, node.Calls("gettxspendingprevout"));
    }

    [Theory]
    [InlineData(RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY, "No such mempool or blockchain transaction", true)]
    [InlineData(RPCErrorCode.RPC_MISC_ERROR, "No such mempool or blockchain transaction", true)]
    [InlineData(RPCErrorCode.RPC_MISC_ERROR, "Transaction not available (pruned data)", false)]
    [InlineData(RPCErrorCode.RPC_INVALID_PARAMETER, "No such mempool or blockchain transaction", false)]
    public void Given_AGetRawTransactionError_When_Classified_Then_CoreAndRbitcoinNotFoundAreRecognised(
        RPCErrorCode code, string message, bool notFound)
    {
        // Act / Assert (NL-1098: rbitcoin answers -1 with Core's text, Core -5)
        Assert.Equal(notFound, BitcoinChainService.IsTransactionNotFound(code, message));
    }

    [Fact]
    public async Task Given_RbitcoinsNotFoundAnswer_When_ATransactionIsLookedUp_Then_NullAndZeroConfirmations()
    {
        // Arrange
        using var node = new FakeRpcNode(_ => """
            {"result":null,"error":{"code":-1,"message":"No such mempool or blockchain transaction"},"id":1}
            """);
        var service = node.CreateService();

        // Act / Assert
        Assert.Null(await service.GetTransactionAsync(s_spent));
        Assert.Equal(0u, await service.GetTransactionConfirmationsAsync(s_spent));
    }
}
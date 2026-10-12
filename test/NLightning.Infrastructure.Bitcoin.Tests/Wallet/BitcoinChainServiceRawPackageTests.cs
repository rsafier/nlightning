using NBitcoin;
using Newtonsoft.Json.Linq;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;

/// <summary>
/// NL-1186: <see cref="BitcoinChainService.SubmitRawPackageAsync"/> (LND's walletrpc <c>SubmitPackage</c>): the request
/// keeps the caller's order and <c>maxfeerate</c>, and the whole answer (per-wtxid results, other wtxids, replaced
/// transactions) comes back as bitcoind gave it.
/// </summary>
public class BitcoinChainServiceRawPackageTests
{
    private static readonly Transaction s_parent = CreateTransaction(1);
    private static readonly Transaction s_child = CreateTransaction(2);

    [Fact]
    public void Given_AMaxFeeRate_When_RequestBuilt_Then_TheTransactionsAndTheRateAreTheParameters()
    {
        // Act
        var request = BitcoinChainService.CreateRawSubmitPackageRequest([s_parent, s_child], 0.0001m);
        var without = BitcoinChainService.CreateRawSubmitPackageRequest([s_parent], null);
        var json = ToJson(request);

        // Assert
        Assert.Equal("submitpackage", json["method"]!.Value<string>());
        var parameters = Assert.IsType<JArray>(json["params"]);
        Assert.Equal(2, parameters.Count);
        Assert.Equal([s_parent.ToHex(), s_child.ToHex()],
                     Assert.IsType<JArray>(parameters[0]).Select(t => t.Value<string>()));
        Assert.Equal(0.0001m, parameters[1].Value<decimal>());
        Assert.Single(Assert.IsType<JArray>(ToJson(without)["params"]));
    }

    [Fact]
    public void Given_ACore28Answer_When_Parsed_Then_EveryResultAndTheReplacedTransactionsAreKept()
    {
        // Arrange
        var answer = JObject.Parse($$"""
            {
              "package_msg": "transaction failed",
              "tx-results": {
                "{{s_parent.GetWitHash()}}": { "txid": "{{s_parent.GetHash()}}", "other-wtxid": "{{s_child.GetWitHash()}}" },
                "{{s_child.GetWitHash()}}": { "txid": "{{s_child.GetHash()}}", "error": "min relay fee not met" }
              },
              "replaced-transactions": ["{{s_parent.GetHash()}}"]
            }
            """);

        // Act
        var result = BitcoinChainService.ParseRawSubmitPackageResponse(answer);

        // Assert
        Assert.Equal("transaction failed", result.PackageMessage);
        var parent = Assert.Single(result.Transactions, t => t.Wtxid == s_parent.GetWitHash().ToString());
        Assert.Equal(s_parent.GetHash().ToString(), parent.Txid);
        Assert.Null(parent.Error);
        Assert.Equal(s_child.GetWitHash().ToString(), parent.OtherWtxid);
        var child = Assert.Single(result.Transactions, t => t.Wtxid == s_child.GetWitHash().ToString());
        Assert.Equal("min relay fee not met", child.Error);
        Assert.Equal([s_parent.GetHash().ToString()], result.ReplacedTransactions);
    }

    [Fact]
    public void Given_NoResult_When_Parsed_Then_FormatException()
    {
        // Act / Assert
        Assert.Throws<FormatException>(() => BitcoinChainService.ParseRawSubmitPackageResponse(null));
    }

    private static JObject ToJson(NBitcoin.RPC.RPCRequest request)
    {
        using var writer = new StringWriter();
        request.WriteJSON(writer);
        return JObject.Parse(writer.ToString());
    }

    private static Transaction CreateTransaction(byte seed)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256(Enumerable.Repeat(seed, 32).ToArray()), 0)));
        tx.Outputs.Add(Money.Satoshis(10_000), new Key(Enumerable.Repeat(seed, 32).ToArray()).PubKey.WitHash);
        return tx;
    }
}
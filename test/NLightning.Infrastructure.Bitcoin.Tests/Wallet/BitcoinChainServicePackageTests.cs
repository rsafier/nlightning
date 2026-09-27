using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json.Linq;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;
using Bitcoin.Wallet.Models;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Options;

/// <summary>
/// NL-380: <see cref="BitcoinChainService.SubmitPackageAsync"/> (<c>submitpackage</c>, Bitcoin Core 28+ 1p1c): the
/// request, the reading of Core 28 and Core 26/27 answers (accepted, refused for fee, already known) and the feature
/// detection against a node without the method (asked once, then answered from memory).
/// </summary>
public class BitcoinChainServicePackageTests
{
    private static readonly Transaction s_parent = CreateTransaction(1);
    private static readonly Transaction s_child = CreateTransaction(2);

    [Fact]
    public void Given_ParentAndChild_When_RequestBuilt_Then_OneArrayParameterWithParentFirst()
    {
        // Act
        var request = BitcoinChainService.CreateSubmitPackageRequest(s_parent, s_child);
        using var writer = new StringWriter();
        request.WriteJSON(writer);
        var json = JObject.Parse(writer.ToString());

        // Assert
        Assert.Equal("submitpackage", json["method"]!.Value<string>());
        var parameters = Assert.IsType<JArray>(json["params"]);
        var package = Assert.IsType<JArray>(Assert.Single(parameters));
        Assert.Equal([s_parent.ToHex(), s_child.ToHex()], package.Select(t => t.Value<string>()));
    }

    [Fact]
    public void Given_Core28Success_When_Parsed_Then_AcceptedWithEachTransactionAndItsFeerate()
    {
        // Arrange
        var answer = JObject.Parse($$"""
            {
              "package_msg": "success",
              "tx-results": {
                "{{s_parent.GetWitHash()}}": { "txid": "{{s_parent.GetHash()}}", "vsize": 200,
                  "fees": { "base": 0.00000300, "effective-feerate": 0.00012000,
                            "effective-includes": ["{{s_parent.GetWitHash()}}", "{{s_child.GetWitHash()}}"] } },
                "{{s_child.GetWitHash()}}": { "txid": "{{s_child.GetHash()}}", "vsize": 150,
                  "fees": { "base": 0.00003900, "effective-feerate": 0.00012000,
                            "effective-includes": ["{{s_parent.GetWitHash()}}", "{{s_child.GetWitHash()}}"] } }
              },
              "replaced-transactions": []
            }
            """);

        // Act
        var result = BitcoinChainService.ParseSubmitPackageResponse(answer);

        // Assert
        Assert.Equal(PackageSubmitStatus.Accepted, result.Status);
        Assert.Equal("success", result.Message);
        Assert.False(result.IsFeeRefusal);
        Assert.Equal([s_parent.GetHash(), s_child.GetHash()], result.Transactions.Select(t => t.TxId));
        Assert.All(result.Transactions, t => Assert.True(t.Accepted));
        Assert.Equal(0.00012000m, result.PackageFeerateBtcPerKvb);
    }

    [Theory]
    [InlineData("mempool min fee not met, 300 < 1200")]
    [InlineData("min relay fee not met, 0 < 110")]
    public void Given_Core28PackageRefusedForFee_When_Parsed_Then_RejectedAsAFeeRefusal(string parentError)
    {
        // Arrange: the parent refused, the child not evaluated
        var answer = JObject.Parse($$"""
            {
              "package_msg": "transaction failed",
              "tx-results": {
                "{{s_parent.GetWitHash()}}": { "txid": "{{s_parent.GetHash()}}", "error": "{{parentError}}" },
                "{{s_child.GetWitHash()}}": { "txid": "{{s_child.GetHash()}}", "error": "unevaluated" }
              },
              "replaced-transactions": []
            }
            """);

        // Act
        var result = BitcoinChainService.ParseSubmitPackageResponse(answer);

        // Assert
        Assert.Equal(PackageSubmitStatus.Rejected, result.Status);
        Assert.True(result.IsFeeRefusal);
        var parent = result.Transactions.Single(t => t.TxId == s_parent.GetHash());
        Assert.False(parent.Accepted);
        Assert.True(parent.IsFeeRefusal);
        Assert.Equal(parentError, parent.Error);
        var child = result.Transactions.Single(t => t.TxId == s_child.GetHash());
        Assert.False(child.Accepted);
        Assert.False(child.IsFeeRefusal);
        Assert.Contains("unevaluated", result.Describe());
    }

    [Fact]
    public void Given_Core28PackageRefusedForAnotherReason_When_Parsed_Then_RejectedButNotAFeeRefusal()
    {
        // Arrange: the funding output is already spent (the peer's commitment won)
        var answer = JObject.Parse($$"""
            {
              "package_msg": "transaction failed",
              "tx-results": {
                "{{s_parent.GetWitHash()}}": { "txid": "{{s_parent.GetHash()}}",
                                               "error": "bad-txns-inputs-missingorspent" },
                "{{s_child.GetWitHash()}}": { "txid": "{{s_child.GetHash()}}", "error": "unevaluated" }
              }
            }
            """);

        // Act
        var result = BitcoinChainService.ParseSubmitPackageResponse(answer);

        // Assert
        Assert.Equal(PackageSubmitStatus.Rejected, result.Status);
        Assert.False(result.IsFeeRefusal);
    }

    [Fact]
    public void Given_ParentAlreadyInMempoolAndChildAdded_When_Parsed_Then_Accepted()
    {
        // Arrange: a parent bitcoind already had comes back as a mempool entry, or with an "already known" error
        var answer = JObject.Parse($$"""
            {
              "package_msg": "success",
              "tx-results": {
                "{{s_parent.GetWitHash()}}": { "txid": "{{s_parent.GetHash()}}", "error": "txn-already-in-mempool" },
                "{{s_child.GetWitHash()}}": { "txid": "{{s_child.GetHash()}}", "vsize": 150,
                  "fees": { "base": 0.00001000, "effective-feerate": 0.00006666 } }
              }
            }
            """);

        // Act
        var result = BitcoinChainService.ParseSubmitPackageResponse(answer);

        // Assert
        Assert.Equal(PackageSubmitStatus.Accepted, result.Status);
        Assert.All(result.Transactions, t => Assert.Null(t.Error));
    }

    [Fact]
    public void Given_Core27Success_When_Parsed_Then_AcceptedWithThePackageFeerate()
    {
        // Arrange: no package_msg; the package feerate at the top
        var answer = JObject.Parse($$"""
            {
              "tx-results": {
                "{{s_parent.GetWitHash()}}": { "txid": "{{s_parent.GetHash()}}", "vsize": 200,
                  "fees": { "base": 0.00000300 } },
                "{{s_child.GetWitHash()}}": { "txid": "{{s_child.GetHash()}}", "vsize": 150,
                  "fees": { "base": 0.00003900 } }
              },
              "package-feerate": 0.00012000,
              "replaced-transactions": []
            }
            """);

        // Act
        var result = BitcoinChainService.ParseSubmitPackageResponse(answer);

        // Assert
        Assert.Equal(PackageSubmitStatus.Accepted, result.Status);
        Assert.Equal(0.00012000m, result.PackageFeerateBtcPerKvb);
    }

    [Fact]
    public void Given_NoResult_When_Parsed_Then_Rejected()
    {
        // Act
        var result = BitcoinChainService.ParseSubmitPackageResponse(null);

        // Assert
        Assert.Equal(PackageSubmitStatus.Rejected, result.Status);
        Assert.Empty(result.Transactions);
    }

    [Theory]
    [InlineData(RPCErrorCode.RPC_METHOD_NOT_FOUND, "Method not found", true)]
    [InlineData(RPCErrorCode.RPC_MISC_ERROR, "submitpackage is for regression testing (-regtest mode) only", true)]
    [InlineData(RPCErrorCode.RPC_VERIFY_ERROR, "package-fee-too-low", false)]
    [InlineData(RPCErrorCode.RPC_MISC_ERROR, "Block not found", false)]
    public void Given_RpcError_When_Classified_Then_OnlyAMissingOrRegtestOnlyMethodIsUnavailable(
        RPCErrorCode code, string message, bool unavailable)
    {
        // Act / Assert
        Assert.Equal(unavailable, BitcoinChainService.IsPackageRelayUnavailable(code, message));
    }

    [Fact]
    public async Task Given_NodeWithoutSubmitPackage_When_SubmittedTwice_Then_UnsupportedAndAskedOnlyOnce()
    {
        // Arrange: a JSON-RPC endpoint that knows getblockchaininfo only
        using var node = new FakeRpcNode(
            _ => """{"result":null,"error":{"code":-32601,"message":"Method not found"},"id":1}""");
        var service = node.CreateService();

        // Act
        var first = await service.SubmitPackageAsync(s_parent, s_child);
        var second = await service.SubmitPackageAsync(s_parent, s_child);

        // Assert
        Assert.Equal(PackageSubmitStatus.Unsupported, first.Status);
        Assert.Equal(PackageSubmitStatus.Unsupported, second.Status);
        Assert.Equal(1, node.Calls("submitpackage"));
    }

    [Fact]
    public async Task Given_NodeRefusingThePackageForFee_When_Submitted_Then_RejectedAsAFeeRefusalAndAskedAgainLater()
    {
        // Arrange
        var refused = $$"""
            { "result": { "package_msg": "transaction failed", "tx-results": {
              "{{s_parent.GetWitHash()}}": { "txid": "{{s_parent.GetHash()}}",
                                             "error": "mempool min fee not met, 300 < 1200" },
              "{{s_child.GetWitHash()}}": { "txid": "{{s_child.GetHash()}}", "error": "unevaluated" } },
              "replaced-transactions": [] }, "error": null, "id": 1 }
            """;
        using var node = new FakeRpcNode(_ => refused);
        var service = node.CreateService();

        // Act
        var first = await service.SubmitPackageAsync(s_parent, s_child);
        var second = await service.SubmitPackageAsync(s_parent, s_child);

        // Assert
        Assert.Equal(PackageSubmitStatus.Rejected, first.Status);
        Assert.True(first.IsFeeRefusal);
        Assert.Equal(PackageSubmitStatus.Rejected, second.Status);
        Assert.Equal(2, node.Calls("submitpackage"));
    }

    [Fact]
    public async Task Given_NodeThrowingAValidationError_When_Submitted_Then_RejectedWithItsMessage()
    {
        // Arrange: Core 26/27 throw for a package that fails validation
        using var node = new FakeRpcNode(
            _ => """{"result":null,"error":{"code":-25,"message":"package-fee-too-low"},"id":1}""");
        var service = node.CreateService();

        // Act
        var result = await service.SubmitPackageAsync(s_parent, s_child);

        // Assert
        Assert.Equal(PackageSubmitStatus.Rejected, result.Status);
        Assert.True(result.IsFeeRefusal);
    }

    [Theory]
    [InlineData("0.00001000", 250u)] // 1 sat/vB, the default minrelaytxfee
    [InlineData("0.00012345", 3087u)] // 12.345 sat/vB -> 3086.25 sat/kw, rounded up
    public void Given_GetMempoolInfoAnswer_When_MinFeeParsed_Then_SatPerKwRoundedUp(string btcPerKvb, uint expected)
    {
        // Arrange
        var answer = JObject.Parse($$"""{ "loaded": true, "size": 3, "mempoolminfee": {{btcPerKvb}} }""");

        // Act
        var rate = BitcoinChainService.ParseMempoolMinFeeRatePerKw(answer);

        // Assert
        Assert.Equal(expected, rate);
    }

    [Fact]
    public void Given_GetMempoolInfoWithoutMinFee_When_Parsed_Then_Null()
    {
        // Arrange
        var answer = JObject.Parse("""{ "loaded": false }""");

        // Act
        var rate = BitcoinChainService.ParseMempoolMinFeeRatePerKw(answer);

        // Assert
        Assert.Null(rate);
    }

    private static Transaction CreateTransaction(byte tag)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat(tag, 32).ToArray()), 0));
        tx.Outputs.Add(Money.Satoshis(10_000 + tag), new Key(Enumerable.Repeat(tag, 32).ToArray()).PubKey.WitHash);
        return tx;
    }
}
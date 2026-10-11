using System.Text;
using Newtonsoft.Json.Linq;

namespace NLightning.Testing.Cluster.Tests.Nodes.BitcoinCore;

using Cluster.Kube;
using Cluster.Nodes.BitcoinCore.Rpc;

public class BitcoinCliTests
{
    private static ExecResult Ok(string stdOut) => new(0, Encoding.UTF8.GetBytes(stdOut), []);

    private static ExecResult Failed(int exitCode, string stdErr) => new(exitCode, [], Encoding.UTF8.GetBytes(stdErr));

    [Fact]
    public void Given_ACall_When_BuildingTheCommand_Then_ItIsNamedWithTheNodesCredentialsAndWallet()
    {
        // Act
        var command = BitcoinCli.BuildCommand("regtest", 18443, "nltg", "pw", "miner", "sendtoaddress",
                                              new Dictionary<string, object?>
                                              {
                                                  ["address"] = "bcrt1q",
                                                  ["amount"] = 0.01m,
                                                  ["fee_rate"] = null,
                                                  ["replaceable"] = true
                                              });

        // Assert
        Assert.Equal(
        [
            "bitcoin-cli", "-regtest", "-rpcport=18443", "-rpcuser=nltg", "-rpcpassword=pw", "-rpcwallet=miner",
            "-named", "sendtoaddress", "address=bcrt1q", "amount=0.01", "replaceable=true"
        ], command);
    }

    [Fact]
    public void Given_NoWalletAndNoArguments_When_BuildingTheCommand_Then_OnlyTheMethodFollowsNamed()
    {
        // Act
        var command = BitcoinCli.BuildCommand("regtest", 1, "u", "p", null, "getblockcount", null);

        // Assert
        Assert.DoesNotContain(command, c => c.StartsWith("-rpcwallet", StringComparison.Ordinal));
        Assert.Equal(["-named", "getblockcount"], command.TakeLast(2));
    }

    [Theory]
    [InlineData("text", "text")]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(101, "101")]
    [InlineData(-5L, "-5")]
    public void Given_AScalar_When_Encoded_Then_StringsStayRawAndTheRestIsJson(object value, string expected)
    {
        // Act / Assert
        Assert.Equal(expected, BitcoinCli.EncodeValue(value));
    }

    [Fact]
    public void Given_DecimalsAndJson_When_Encoded_Then_TheyAreInvariantAndCompact()
    {
        // Act / Assert
        Assert.Equal("0.00001", BitcoinCli.EncodeValue(0.00001m));
        Assert.Equal("0.00004000", BitcoinCli.EncodeValue(0.00004000m));
        Assert.Equal("[]", BitcoinCli.EncodeValue(new JArray()));
        Assert.Equal("{\"a\":0.1}", BitcoinCli.EncodeValue(new JObject { ["a"] = 0.1m }));
        Assert.Equal("[\"x\",\"y\"]", BitcoinCli.EncodeValue(new[] { "x", "y" }));
        Assert.Throws<ArgumentException>(() => BitcoinCli.EncodeValue(0.1d));
    }

    [Fact]
    public void Given_JsonOutput_When_Parsed_Then_ItIsJson()
    {
        // Act
        var info = BitcoinCli.ParseResult("getblockchaininfo", Ok("{\n  \"chain\": \"regtest\",\n  \"blocks\": 101\n}\n"));
        var hashes = BitcoinCli.ParseResult("generatetoaddress", Ok("[\n  \"aa\"\n]\n"));

        // Assert
        Assert.Equal("regtest", info.Value<string>("chain"));
        Assert.Equal(101, info.Value<long>("blocks"));
        Assert.Equal("aa", Assert.Single(hashes.Values<string>()));
    }

    [Fact]
    public void Given_ScalarOutput_When_Parsed_Then_NumbersBooleansAndNullAreJsonAndStringsRaw()
    {
        // Arrange
        var allDigits = new string('1', 64);

        // Act / Assert
        Assert.Equal(101, BitcoinCli.ParseResult("getblockcount", Ok("101\n")).Value<long>());
        Assert.Equal(0.0002m, BitcoinCli.ParseResult("x", Ok("0.0002\n")).Value<decimal>());
        Assert.True(BitcoinCli.ParseResult("settxfee", Ok("true\n")).Value<bool>());
        Assert.Equal(JTokenType.Null, BitcoinCli.ParseResult("invalidateblock", Ok(string.Empty)).Type);
        Assert.Equal("0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206",
                     BitcoinCli.ParseResult("getblockhash", Ok("0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206\n"))
                               .Value<string>());
        Assert.Equal(allDigits, BitcoinCli.ParseResult("getblockhash", Ok(allDigits + "\n")).Value<string>());
        Assert.Equal("bcrt1qxyz", BitcoinCli.ParseResult("getnewaddress", Ok("bcrt1qxyz\n")).Value<string>());
    }

    [Fact]
    public void Given_AnRpcError_When_Parsed_Then_ItCarriesTheCodeAndMessage()
    {
        // Act
        var e = Assert.Throws<BitcoinRpcException>(() => BitcoinCli.ParseResult(
            "getmempoolentry",
            Failed(5, "error code: -5\nerror message:\nTransaction not in mempool\n")));

        // Assert
        Assert.Equal(BitcoinRpcErrorCodes.InvalidAddressOrKey, e.Code);
        Assert.Equal("Transaction not in mempool", e.RpcMessage);
        Assert.Equal("getmempoolentry", e.Method);
        Assert.Contains("error -5", e.Message);
    }

    [Fact]
    public void Given_NoRpcAnswer_When_Parsed_Then_TheErrorHasNoCode()
    {
        // Act
        var e = Assert.Throws<BitcoinRpcException>(() => BitcoinCli.ParseResult(
            "getblockcount", Failed(1, "error: Could not connect to the server 127.0.0.1:18443\n")));
        var silent = Assert.Throws<BitcoinRpcException>(() => BitcoinCli.ParseResult("x", Failed(137, string.Empty)));

        // Assert
        Assert.Null(e.Code);
        Assert.Contains("Could not connect", e.RpcMessage);
        Assert.Equal("bitcoin-cli exited with 137", silent.RpcMessage);
    }
}
using Newtonsoft.Json.Linq;

namespace NLightning.Testing.Cluster.Tests.Nodes.BitcoinCore;

using Cluster.Chain;
using Cluster.Nodes.BitcoinCore.Rpc;

public class BitcoinCoreRpcClientTests
{
    private const string TxId = "ab";

    [Fact]
    public async Task Given_GetBlockchainInfo_When_Read_Then_TheTipComesFromOneAnswer()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var info = JObject.Parse(
            """{"chain":"regtest","blocks":101,"headers":102,"bestblockhash":"ff","initialblockdownload":false,"warnings":[]}""");
        var transport = new FakeRpcTransport().Returns("getblockchaininfo", info).Returns("getblockchaininfo", info);
        var rpc = new BitcoinCoreRpcClient(transport);

        // Act
        var chainInfo = await rpc.GetChainInfoAsync(ct);
        var tip = await rpc.GetTipAsync(ct);

        // Assert
        Assert.Equal(new ChainInfo("regtest", 101, 102, "ff", false), chainInfo);
        Assert.Equal(new ChainTip(101, "ff"), tip);
        Assert.Equal("fake transport", rpc.Description);
    }

    [Fact]
    public async Task Given_AMissingField_When_Read_Then_TheErrorNamesIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var rpc = new BitcoinCoreRpcClient(new FakeRpcTransport().Returns("getblockchaininfo", new JObject { ["chain"] = "regtest" }));

        // Act / Assert
        var e = await Assert.ThrowsAsync<BitcoinRpcException>(() => rpc.GetChainInfoAsync(ct));
        Assert.Contains("'blocks'", e.Message);
    }

    [Fact]
    public async Task Given_ATransactionInTheMempool_When_ItsStatusIsRead_Then_ItIsInMempool()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport().Returns("getmempoolentry", new JObject { ["vsize"] = 141 });

        // Act
        var status = await new BitcoinCoreRpcClient(transport).GetTransactionStatusAsync(TxId, ct);

        // Assert
        Assert.Equal(TxStatus.Mempool(TxId), status);
        Assert.Equal(TxId, transport.ArgsOf("getmempoolentry")["txid"]);
    }

    [Fact]
    public async Task Given_AConfirmedTransaction_When_ItsStatusIsRead_Then_ItHasItsBlockAndHeight()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport()
                       .Fails("getmempoolentry", BitcoinRpcErrorCodes.InvalidAddressOrKey)
                       .Returns("getrawtransaction", new JObject { ["blockhash"] = "bb", ["confirmations"] = 3 })
                       .Returns("getblockheader", JObject.Parse("""{"hash":"bb","height":102,"confirmations":3,"previousblockhash":"aa"}"""));

        // Act
        var status = await new BitcoinCoreRpcClient(transport).GetTransactionStatusAsync(TxId, ct);

        // Assert
        Assert.Equal(new TxStatus(TxId, TxState.Confirmed, "bb", 102, 3), status);
        Assert.Equal(true, transport.ArgsOf("getrawtransaction")["verbose"]);
    }

    [Fact]
    public async Task Given_ATransactionOnlyInAStaleBlock_When_ItsStatusIsRead_Then_ItIsNotFound()
    {
        // Arrange: -txindex still finds it, with 0 confirmations
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport()
                       .Fails("getmempoolentry", BitcoinRpcErrorCodes.InvalidAddressOrKey)
                       .Returns("getrawtransaction", new JObject { ["blockhash"] = "stale", ["confirmations"] = 0 });

        // Act
        var status = await new BitcoinCoreRpcClient(transport).GetTransactionStatusAsync(TxId, ct);

        // Assert
        Assert.Equal(TxStatus.NotFound(TxId), status);
    }

    [Fact]
    public async Task Given_AnUnknownTransaction_When_ItsStatusIsRead_Then_ItIsNotFound()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport()
                       .Fails("getmempoolentry", BitcoinRpcErrorCodes.InvalidAddressOrKey)
                       .Fails("getrawtransaction", BitcoinRpcErrorCodes.InvalidAddressOrKey);

        // Act
        var status = await new BitcoinCoreRpcClient(transport).GetTransactionStatusAsync(TxId, ct);

        // Assert
        Assert.Equal(TxState.NotFound, status.State);
    }

    [Fact]
    public async Task Given_ATransactionWithoutABlockHash_When_ItsStatusIsRead_Then_ItIsInTheMempool()
    {
        // Arrange: it entered the mempool between the two calls
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport()
                       .Fails("getmempoolentry", BitcoinRpcErrorCodes.InvalidAddressOrKey)
                       .Returns("getrawtransaction", new JObject { ["txid"] = TxId });

        // Act / Assert
        Assert.Equal(TxState.InMempool, (await new BitcoinCoreRpcClient(transport).GetTransactionStatusAsync(TxId, ct)).State);
    }

    [Fact]
    public async Task Given_AnotherError_When_ItsStatusIsRead_Then_ItIsThrown()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport().Fails("getmempoolentry", null, "not reachable");

        // Act / Assert
        await Assert.ThrowsAsync<BitcoinRpcException>(
            () => new BitcoinCoreRpcClient(transport).GetTransactionStatusAsync(TxId, ct));
    }

    [Fact]
    public async Task Given_ASend_When_Called_Then_TheAmountIsBitcoinAndTheRateIsSatPerVb()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport().Returns("sendtoaddress", "tx1").Returns("sendtoaddress", "tx2");
        var rpc = new BitcoinCoreRpcClient(transport);

        // Act
        var pinned = await rpc.SendToAddressAsync("bcrt1q", 1_000_000, 3.5m, ct);
        var walletRate = await rpc.SendToAddressAsync("bcrt1q", 1, null, ct);

        // Assert
        Assert.Equal("tx1", pinned);
        Assert.Equal("tx2", walletRate);
        Assert.Equal(0.01m, transport.Calls[0].Args["amount"]);
        Assert.Equal(3.5m, transport.Calls[0].Args["fee_rate"]);
        Assert.Equal(0.00000001m, transport.Calls[1].Args["amount"]);
        Assert.Null(transport.Calls[1].Args["fee_rate"]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rpc.SendToAddressAsync("a", 0, null, ct));
    }

    [Fact]
    public async Task Given_AFanOut_When_Sent_Then_EveryAddressGetsItsAmountInOneTransaction()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport().Returns("sendmany", "fan");

        // Act
        var txId = await new BitcoinCoreRpcClient(transport).SendManyAsync(
            new Dictionary<string, long> { ["a"] = 200_000, ["b"] = 100_000_000 }, 12m, ct);

        // Assert
        Assert.Equal("fan", txId);
        var amounts = Assert.IsType<JObject>(transport.ArgsOf("sendmany")["amounts"]);
        Assert.Equal(0.002m, amounts.Value<decimal>("a"));
        Assert.Equal(1m, amounts.Value<decimal>("b"));
        Assert.Equal(12m, transport.ArgsOf("sendmany")["fee_rate"]);
    }

    [Fact]
    public async Task Given_ARate_When_SettingTheWalletFee_Then_SetTxFeeGetsBtcPerKvB()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport().Returns("settxfee", true);

        // Act
        await new BitcoinCoreRpcClient(transport).SetTxFeeAsync(4m, ct);

        // Assert
        Assert.Equal(0.00004m, transport.ArgsOf("settxfee")["amount"]);
    }

    [Fact]
    public async Task Given_AnEstimate_When_Read_Then_ItIsInSatPerVb()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport()
                       .Returns("estimatesmartfee", JObject.Parse("""{"feerate":0.00012,"blocks":2}"""))
                       .Returns("estimatesmartfee", JObject.Parse("""{"errors":["Insufficient data or no feerate found"],"blocks":0}"""));
        var rpc = new BitcoinCoreRpcClient(transport);

        // Act
        var estimate = await rpc.EstimateSmartFeeAsync(2, FeeEstimateMode.Economical, ct);
        var none = await rpc.EstimateSmartFeeAsync(6, FeeEstimateMode.Conservative, ct);

        // Assert
        Assert.Equal(new FeeEstimate(12m, 2), estimate);
        Assert.Null(none);
        Assert.Equal("economical", transport.Calls[0].Args["estimate_mode"]);
        Assert.Equal("conservative", transport.Calls[1].Args["estimate_mode"]);
        Assert.Equal(6, transport.Calls[1].Args["conf_target"]);
    }

    [Fact]
    public async Task Given_AnEmptyBlock_When_Generated_Then_GenerateBlockGetsAnEmptyList()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport().Returns("generateblock", new JObject { ["hash"] = "cc" });

        // Act
        var hash = await new BitcoinCoreRpcClient(transport).GenerateBlockAsync("addr", [], ct);

        // Assert
        Assert.Equal("cc", hash);
        Assert.Equal("addr", transport.ArgsOf("generateblock")["output"]);
        Assert.Empty(Assert.IsType<JArray>(transport.ArgsOf("generateblock")["transactions"]));
    }

    [Fact]
    public async Task Given_Blocks_When_Generated_Then_TheirHashesComeBackInOrder()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport().Returns("generatetoaddress", new JArray("a", "b"));

        // Act
        var hashes = await new BitcoinCoreRpcClient(transport).GenerateToAddressAsync(2, "addr", ct);

        // Assert
        Assert.Equal(["a", "b"], hashes);
        Assert.Equal(2, transport.ArgsOf("generatetoaddress")["nblocks"]);
    }

    [Fact]
    public async Task Given_AnExistingWallet_When_Ensured_Then_ItIsLoadedAndAnAlreadyLoadedOneIsFine()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport()
                       .Returns("createwallet", new JObject { ["name"] = "miner" })
                       .Fails("createwallet", BitcoinRpcErrorCodes.WalletError, "Database already exists")
                       .Returns("loadwallet", new JObject { ["name"] = "miner" })
                       .Fails("createwallet", BitcoinRpcErrorCodes.WalletAlreadyLoaded)
                       .Fails("loadwallet", BitcoinRpcErrorCodes.WalletAlreadyLoaded);
        var rpc = new BitcoinCoreRpcClient(transport);

        // Act
        await rpc.EnsureWalletAsync("miner", ct);
        await rpc.EnsureWalletAsync("miner", ct);
        await rpc.EnsureWalletAsync("miner", ct);

        // Assert
        Assert.Equal(["createwallet", "createwallet", "loadwallet", "createwallet", "loadwallet"],
                     transport.Calls.Select(c => c.Method));
        Assert.Equal(true, transport.Calls[0].Args["load_on_startup"]);
        Assert.Equal(true, transport.Calls[2].Args["load_on_startup"]);
    }

    [Fact]
    public async Task Given_AWalletError_When_Ensured_Then_ItIsThrown()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport().Fails("createwallet", null, "not reachable");

        // Act / Assert
        await Assert.ThrowsAsync<BitcoinRpcException>(() => new BitcoinCoreRpcClient(transport).EnsureWalletAsync("w", ct));
    }

    [Fact]
    public async Task Given_Balances_When_Read_Then_TheTrustedOneIsInSatoshis()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport().Returns("getbalances",
            JObject.Parse("""{"mine":{"trusted":50.00012345,"untrusted_pending":0,"immature":5000}}"""));

        // Act / Assert
        Assert.Equal(5_000_012_345, await new BitcoinCoreRpcClient(transport).GetTrustedBalanceSatAsync(ct));
    }

    [Fact]
    public async Task Given_NodeCalls_When_Made_Then_TheirArgumentsAreNamed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var transport = new FakeRpcTransport()
                       .Returns("getblockhash", "h5")
                       .Returns("invalidateblock", JValue.CreateNull())
                       .Returns("reconsiderblock", JValue.CreateNull())
                       .Returns("addnode", JValue.CreateNull())
                       .Returns("getconnectioncount", 2)
                       .Returns("getrawmempool", new JArray("t1"))
                       .Returns("getnewaddress", "bcrt1q");
        var rpc = new BitcoinCoreRpcClient(transport);

        // Act
        var hash = await rpc.GetBlockHashAsync(5, ct);
        await rpc.InvalidateBlockAsync("h5", ct);
        await rpc.ReconsiderBlockAsync("h5", ct);
        await rpc.AddNodeAsync("miner:18444", "onetry", ct);
        var connections = await rpc.GetConnectionCountAsync(ct);
        var mempool = await rpc.GetRawMempoolAsync(ct);
        var address = await rpc.GetNewAddressAsync(ct);

        // Assert
        Assert.Equal("h5", hash);
        Assert.Equal(5L, transport.ArgsOf("getblockhash")["height"]);
        Assert.Equal("h5", transport.ArgsOf("invalidateblock")["blockhash"]);
        Assert.Equal("h5", transport.ArgsOf("reconsiderblock")["blockhash"]);
        Assert.Equal("onetry", transport.ArgsOf("addnode")["command"]);
        Assert.Equal(2, connections);
        Assert.Equal(["t1"], mempool);
        Assert.Equal("bcrt1q", address);
    }

    [Theory]
    [InlineData(1L, "0.00000001")]
    [InlineData(100_000_000L, "1")]
    [InlineData(123_456_789L, "1.23456789")]
    public void Given_Satoshis_When_Converted_Then_TheBitcoinAmountRoundTrips(long satoshis, string bitcoin)
    {
        // Act
        var amount = BitcoinCoreRpcClient.ToBitcoin(satoshis);

        // Assert
        Assert.Equal(decimal.Parse(bitcoin, System.Globalization.CultureInfo.InvariantCulture), amount);
        Assert.Equal(satoshis, BitcoinCoreRpcClient.ToSatoshis(amount));
    }
}
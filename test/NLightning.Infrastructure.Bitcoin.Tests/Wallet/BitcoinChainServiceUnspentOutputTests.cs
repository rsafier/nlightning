using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

/// <summary>
/// NL-413: <see cref="Bitcoin.Wallet.BitcoinChainService.GetUnspentOutputAsync(OutPoint)"/> takes the output's height
/// from <c>gettxout</c>'s own <c>bestblock</c>, so a block connected between the two RPCs no longer shifts it.
/// </summary>
public class BitcoinChainServiceUnspentOutputTests
{
    private const string BestBlock = "0000000000000000000123456789abcdef0123456789abcdef0123456789abcd";
    private const uint BestHeight = 110;
    private const int Confirmations = 3;
    private static readonly OutPoint s_outPoint = new(uint256.One, 1);

    [Fact]
    public async Task Given_ABlockConnectedAfterGetTxOut_When_GetUnspentOutput_Then_HeightIsFromTheAnswersBestBlock()
    {
        // Arrange: gettxout answered at block 110 (3 confirmations); bitcoind is at 111 when asked afterwards
        using var node = new FakeRpcNode(Answer);
        var service = node.CreateService();

        // Act
        var unspent = await service.GetUnspentOutputAsync(s_outPoint);
        var confirmed = await service.GetConfirmedUnspentOutputAsync(s_outPoint);

        // Assert: 110 - 3 + 1, not 111 - 3 + 1 (the old getblockcount answer), and no getblockcount at all
        Assert.NotNull(unspent);
        Assert.Equal(BestHeight - Confirmations + 1, unspent.Value.Height);
        Assert.Equal(Money.Satoshis(1_000_000), unspent.Value.Output.Value);
        Assert.NotNull(confirmed);
        Assert.Equal(BestHeight - Confirmations + 1, confirmed.Value.Height);
        Assert.Equal(0, node.Calls("getblockcount"));
        Assert.Equal(2, node.Calls("getblockheader"));
    }

    [Fact]
    public async Task Given_AnUnconfirmedOutput_When_GetUnspentOutput_Then_NullWithoutAHeaderLookup()
    {
        // Arrange: an output of a mempool transaction (gettxout with the mempool, 0 confirmations)
        using var node = new FakeRpcNode(method => method == "gettxout" ? TxOut(0) : Answer(method));
        var service = node.CreateService();

        // Act
        var unspent = await service.GetUnspentOutputAsync(s_outPoint);

        // Assert
        Assert.Null(unspent);
        Assert.Equal(0, node.Calls("getblockheader"));
    }

    [Fact]
    public void Given_RbitcoinsAsmNotation_When_TheAnswerIsParsed_Then_TheScriptComesFromItsHex()
    {
        // Arrange (NL-1097): rbitcoin writes rust-bitcoin's asm, which NBitcoin's GetTxOutAsync could not parse
        var script = new Key().PubKey.WitHash.ScriptPubKey;
        var answer = Newtonsoft.Json.Linq.JObject.Parse($$"""
            {"bestblock":"{{BestBlock}}","confirmations":2,"value":0.01,
             "scriptPubKey":{"hex":"{{script.ToHex()}}","asm":"OP_0 OP_PUSHBYTES_20 {{script.ToHex()[4..]}}"},
             "coinbase":false}
            """);

        // Act
        var parsed = Bitcoin.Wallet.BitcoinChainService.ParseTxOutResponse(answer);

        // Assert
        Assert.NotNull(parsed);
        Assert.Equal(uint256.Parse(BestBlock), parsed.Value.BestBlock);
        Assert.Equal(2, parsed.Value.Confirmations);
        Assert.Equal(Money.Satoshis(1_000_000), parsed.Value.Output.Value);
        Assert.Equal(script, parsed.Value.Output.ScriptPubKey);
        Assert.Null(Bitcoin.Wallet.BitcoinChainService.ParseTxOutResponse(Newtonsoft.Json.Linq.JValue.CreateNull()));
    }

    private static string Answer(string method) => method switch
    {
        "gettxout" => TxOut(Confirmations),
        "getblockheader" => $$"""
                              {"result":{"hash":"{{BestBlock}}","confirmations":2,"height":{{BestHeight}},
                                "version":536870912,"merkleroot":"{{BestBlock}}","time":1790000000,
                                "mediantime":1790000000,"nonce":0,"bits":"207fffff","difficulty":1,
                                "chainwork":"00","nTx":1},"error":null,"id":1}
                              """,
        "getblockcount" => $$"""{"result":{{BestHeight + 1}},"error":null,"id":1}""",
        _ => """{"result":null,"error":{"code":-32601,"message":"Method not found"},"id":1}"""
    };

    private static string TxOut(int confirmations) =>
        $$"""
          {"result":{"bestblock":"{{BestBlock}}","confirmations":{{confirmations}},"value":0.01000000,
            "scriptPubKey":{"asm":"0 1111111111111111111111111111111111111111",
              "hex":"00141111111111111111111111111111111111111111","type":"witness_v0_keyhash"},
            "coinbase":false},"error":null,"id":1}
          """;
}
using System.Net;
using NBitcoin;
using NBitcoin.RPC;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;

/// <summary>
/// NL-416: <see cref="BitcoinChainService.GetBlockTxIdsAsync"/> reads a <c>getblock &lt;hash&gt; 1</c> answer with a
/// streaming parser (a 100-400 KB answer parsed as a JToken tree lands on the large object heap and peaked a verified
/// sync at 630 MB RSS): the txids come out in order, a JSON-RPC error is thrown as <see cref="RPCException"/> (the
/// pruned-block answer then reads as <c>null</c>) and an answer without a tx array fails.
/// </summary>
public class BitcoinChainServiceBlockTxIdsTests
{
    // Bitcoin Core's getblock <hash> 1 shape (mainnet block 100,000's header and its first two transactions; the last
    // two txids are placeholders of the same shape)
    private const string BlockAnswer = """
                                       {"result":{"hash":"000000000003ba27aa200b1cecaad478d2b00432346c3f1f3986da1afd33e506",
                                         "confirmations":911111,"height":100000,"version":1,"versionHex":"00000001",
                                         "merkleroot":"f3e94742aca4b5ef85488dc37c06c3282295ffec960994b2c0d5ac2a25a95766",
                                         "tx":["8c14f0db3df150123e6f3dbbf30f8b955a8249b62ac1d1ff16284aefa3d06d87",
                                               "fff2525b8931402dd09222c50775608f75787bd2b87e56995a7bdd30f79702c4",
                                               "1111111111111111111111111111111111111111111111111111111111111111",
                                               "2222222222222222222222222222222222222222222222222222222222222222"],
                                         "time":1293623863,"mediantime":1293620133,"nonce":2248465136,
                                         "bits":"1b04864c","difficulty":14484.1623612254,
                                         "chainwork":"000000000000000000000000000000000000000000000000000ee4e1cefd4c59",
                                         "previousblockhash":"000000000002c865e2c2e0c4c8e9b8b6f0c6f4d1e0a9b8c7d6e5f4a3b2c1d0e9",
                                         "nextblockhash":"00000000000001c1d7dc1c8f8b1c8d3e2f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c"},
                                        "error":null,"id":1}
                                       """;

    private static readonly uint256 s_blockHash =
        uint256.Parse("000000000003ba27aa200b1cecaad478d2b00432346c3f1f3986da1afd33e506");

    [Fact]
    public void Given_RepresentativeGetBlockAnswer_When_Parsed_Then_TxIdsInOrder()
    {
        // Act
        var txIds = BitcoinChainService.ParseGetBlockAnswer(new StringReader(BlockAnswer), s_blockHash, HttpStatusCode.OK);

        // Assert: every entry of the tx array, in order; the header fields around it (before and after) are skipped
        var expected = new[]
        {
            "8c14f0db3df150123e6f3dbbf30f8b955a8249b62ac1d1ff16284aefa3d06d87",
            "fff2525b8931402dd09222c50775608f75787bd2b87e56995a7bdd30f79702c4",
            "1111111111111111111111111111111111111111111111111111111111111111",
            "2222222222222222222222222222222222222222222222222222222222222222"
        };
        Assert.Equal(expected.Select(uint256.Parse), txIds);
    }

    [Fact]
    public void Given_PrunedBlockAnswer_When_Parsed_Then_MiscErrorThrown()
    {
        // Arrange: bitcoind's answer for a pruned block (HTTP 500)
        const string answer = """{"result":null,"error":{"code":-1,"message":"Block not available (pruned data)"},"id":1}""";

        // Act
        var exception = Assert.Throws<RPCException>(() => BitcoinChainService.ParseGetBlockAnswer(
            new StringReader(answer), s_blockHash, HttpStatusCode.InternalServerError));

        // Assert: the same classification the JToken-based reading gave, so the caller reads it as BlockUnavailable
        Assert.Equal(RPCErrorCode.RPC_MISC_ERROR, exception.RPCCode);
        Assert.Equal("Block not available (pruned data)", exception.Message);
        Assert.True(BitcoinChainService.IsPrunedBlockError(exception.RPCCode, exception.Message));
    }

    [Fact]
    public void Given_AnswerWithoutTxArray_When_Parsed_Then_Fails()
    {
        // Arrange: a result that carries the header fields but no tx array
        const string answer = """{"result":{"hash":"00","height":1},"error":null,"id":1}""";

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => BitcoinChainService.ParseGetBlockAnswer(
            new StringReader(answer), s_blockHash, HttpStatusCode.OK));
    }

    [Fact]
    public void Given_ErrorWithoutMessage_When_Parsed_Then_ThrowsWithTheStatus()
    {
        // Arrange
        const string answer = """{"result":null,"error":{"code":-8},"id":1}""";

        // Act
        var exception = Assert.Throws<RPCException>(() => BitcoinChainService.ParseGetBlockAnswer(
            new StringReader(answer), s_blockHash, HttpStatusCode.InternalServerError));

        // Assert
        Assert.Equal(RPCErrorCode.RPC_INVALID_PARAMETER, exception.RPCCode);
        Assert.Contains("500", exception.Message);
    }

    [Fact]
    public async Task Given_FakeBitcoind_When_GetBlockTxIds_Then_TxIdsReadFromTheStreamingAnswer()
    {
        // Arrange: the fake answers getblockhash and the verbose getblock above
        using var node = new FakeRpcNode(method => method == "getblock" ? BlockAnswer : BlockHashAnswer());
        var service = node.CreateService();

        // Act
        var block = await service.GetBlockTxIdsAsync(100_000);

        // Assert: the block hash of the answer (not of the asked height) and its txids; one streamed getblock
        Assert.NotNull(block);
        Assert.Equal(s_blockHash, block.Value.BlockHash);
        Assert.Equal(4, block.Value.TxIds.Count);
        Assert.Equal(uint256.Parse("8c14f0db3df150123e6f3dbbf30f8b955a8249b62ac1d1ff16284aefa3d06d87"),
                     block.Value.TxIds[0]);
        Assert.Equal(1, node.Calls("getblock"));
    }

    [Fact]
    public async Task Given_PrunedBlockAtBitcoind_When_GetBlockTxIds_Then_Null()
    {
        // Arrange: getblock answers the pruned-block error (HTTP 500 at the fake, like bitcoind)
        const string pruned = """{"result":null,"error":{"code":-1,"message":"Block not available (pruned data)"},"id":1}""";
        using var node = new FakeRpcNode(method => method == "getblock" ? pruned : BlockHashAnswer());
        var service = node.CreateService();

        // Act / Assert
        Assert.Null(await service.GetBlockTxIdsAsync(100_000));
    }

    [Fact]
    public async Task Given_HeightBeyondTheTip_When_GetBlockTxIds_Then_Null()
    {
        // Arrange: getblockhash answers "Block height out of range" (-8)
        const string outOfRange = """{"result":null,"error":{"code":-8,"message":"Block height out of range"},"id":1}""";
        using var node = new FakeRpcNode(_ => outOfRange);
        var service = node.CreateService();

        // Act / Assert
        Assert.Null(await service.GetBlockTxIdsAsync(1_000_000));
        Assert.Equal(0, node.Calls("getblock"));
    }

    private static string BlockHashAnswer() =>
        $$"""{"result":"{{s_blockHash}}","error":null,"id":1}""";
}
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json.Linq;

namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

using Bitcoin.Wallet;

/// <summary>
/// Which <c>getblock</c> errors <see cref="BitcoinChainService.GetBlockTxIdsAsync"/> reads as a pruned block (null,
/// <c>BlockUnavailable</c>) rather than a failure (<c>ChainUnavailable</c>).
/// </summary>
public class BitcoinChainServicePrunedBlockTests
{
    [Theory]
    [InlineData("Block not available (pruned data)")]
    [InlineData("Block not available (PRUNED data)")]
    public void Given_MiscErrorAboutPrunedData_When_Classified_Then_Pruned(string message)
    {
        // Act
        var pruned = BitcoinChainService.IsPrunedBlockError(RPCErrorCode.RPC_MISC_ERROR, message);

        // Assert
        Assert.True(pruned);
    }

    [Theory]
    [InlineData("Block not found on disk")]
    [InlineData("Block not available (not fully downloaded)")]
    [InlineData("")]
    [InlineData(null)]
    public void Given_OtherMiscError_When_Classified_Then_NotPruned(string? message)
    {
        // Act
        var pruned = BitcoinChainService.IsPrunedBlockError(RPCErrorCode.RPC_MISC_ERROR, message);

        // Assert
        Assert.False(pruned);
    }

    [Fact]
    public void Given_OtherErrorCodeMentioningPruned_When_Classified_Then_NotPruned()
    {
        // Act
        var pruned = BitcoinChainService.IsPrunedBlockError(RPCErrorCode.RPC_INVALID_PARAMETER, "pruned");

        // Assert
        Assert.False(pruned);
    }

    [Fact]
    public void Given_GetBlockHeaderAnswer_When_Parsed_Then_MerkleRootAndTxCount()
    {
        // Arrange: Bitcoin Core's getblockheader <hash> true (mainnet block 100,000)
        var result = JObject.Parse("""
                                   {"hash":"000000000003ba27aa200b1cecaad478d2b00432346c3f1f3986da1afd33e506",
                                    "height":100000,"nTx":4,
                                    "merkleroot":"f3e94742aca4b5ef85488dc37c06c3282295ffec960994b2c0d5ac2a25a95766"}
                                   """);

        // Act
        var (merkleRoot, txCount) = BitcoinChainService.ParseBlockHeaderSummary(result);

        // Assert
        Assert.Equal(uint256.Parse("f3e94742aca4b5ef85488dc37c06c3282295ffec960994b2c0d5ac2a25a95766"), merkleRoot);
        Assert.Equal(4, txCount);
    }

    [Fact]
    public void Given_HeaderWithoutTxCount_When_Parsed_Then_CountUnknown()
    {
        // Arrange: a header whose block was never downloaded may carry no usable nTx
        var result = JObject.Parse("""{"merkleroot":"f3e94742aca4b5ef85488dc37c06c3282295ffec960994b2c0d5ac2a25a95766"}""");

        // Act
        var (_, txCount) = BitcoinChainService.ParseBlockHeaderSummary(result);

        // Assert
        Assert.Equal(0, txCount);
        Assert.Throws<InvalidOperationException>(() => BitcoinChainService.ParseBlockHeaderSummary(new JObject()));
    }
}
using NBitcoin.RPC;

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
}
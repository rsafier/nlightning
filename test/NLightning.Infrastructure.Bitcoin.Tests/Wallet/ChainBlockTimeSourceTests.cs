using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;
using Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-623: the time of a block for the accounting channel report (<see cref="BitcoinChainService.GetBlockTimeAsync"/>
/// over <c>getblockhash</c> + <c>getblockheader</c>, and <see cref="ChainBlockTimeSource"/>'s cache).
/// </summary>
public class ChainBlockTimeSourceTests
{
    private const string BlockHash = "0f9188f13cb7b2c71f2a335e3a4fc328bf5beb436012afca590b1a11466e2206";

    [Fact]
    public async Task Given_AHeightInTheChain_When_ItsTimeIsAsked_Then_TheHeaderTimeIsReturned()
    {
        // Arrange
        using var node = new FakeRpcNode(Answer);
        var service = node.CreateService();

        // Act
        var time = await service.GetBlockTimeAsync(800_000);

        // Assert
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), time);
        Assert.Equal(1, node.Calls("getblockhash"));
        Assert.Equal(1, node.Calls("getblockheader"));
    }

    [Fact]
    public async Task Given_AHeightAboveTheTip_When_ItsTimeIsAsked_Then_Null()
    {
        // Arrange: bitcoind's "Block height out of range"
        using var node = new FakeRpcNode(method => method == "getblockhash"
                                                       ? """{"result":null,"error":{"code":-8,"message":"Block height out of range"},"id":1}"""
                                                       : Answer(method));
        var service = node.CreateService();

        // Act
        var time = await service.GetBlockTimeAsync(9_999_999);

        // Assert
        Assert.Null(time);
        Assert.Equal(0, node.Calls("getblockheader"));
    }

    [Fact]
    public async Task Given_ATimeAlreadyRead_When_ItIsAskedAgain_Then_TheChainIsNotAskedTwice()
    {
        // Arrange
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetBlockTimeAsync(800_000)).ReturnsAsync(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        chain.Setup(c => c.GetBlockTimeAsync(900_000)).ReturnsAsync((DateTimeOffset?)null);
        var source = new ChainBlockTimeSource(chain.Object);

        // Act
        var first = await source.GetBlockTimeAsync(800_000, TestContext.Current.CancellationToken);
        var second = await source.GetBlockTimeAsync(800_000, TestContext.Current.CancellationToken);
        var unknown = await source.GetBlockTimeAsync(900_000, TestContext.Current.CancellationToken);

        // Assert: an unknown height is not kept
        Assert.Equal(first, second);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), first);
        Assert.Null(unknown);
        chain.Verify(c => c.GetBlockTimeAsync(800_000), Times.Once);
    }

    [Fact]
    public async Task Given_TheDefaultImplementation_When_ATimeIsAsked_Then_TheBlockHeaderGivesIt()
    {
        // Arrange: a chain service that only implements the required members
        var block = Network.RegTest.GetGenesis();
        var chain = new Mock<IBitcoinChainService> { CallBase = true };
        chain.Setup(c => c.GetCurrentBlockHeightAsync()).ReturnsAsync(10u);
        chain.Setup(c => c.GetBlockAsync(5u)).ReturnsAsync(block);

        // Act
        var time = await chain.Object.GetBlockTimeAsync(5);
        var aboveTip = await chain.Object.GetBlockTimeAsync(11);

        // Assert
        Assert.Equal(block.Header.BlockTime, time);
        Assert.Null(aboveTip);
    }

    private static string Answer(string method) => method switch
    {
        "getblockhash" => $$"""{"result":"{{BlockHash}}","error":null,"id":1}""",
        "getblockheader" => $$"""
                              {"result":{"hash":"{{BlockHash}}","confirmations":2,"height":800000,
                                "version":536870912,"merkleroot":"{{BlockHash}}","time":1790000000,
                                "mediantime":1789999000,"nonce":0,"bits":"207fffff","difficulty":1,
                                "chainwork":"00","nTx":1},"error":null,"id":1}
                              """,
        _ => """{"result":null,"error":{"code":-32601,"message":"Method not found"},"id":1}"""
    };
}
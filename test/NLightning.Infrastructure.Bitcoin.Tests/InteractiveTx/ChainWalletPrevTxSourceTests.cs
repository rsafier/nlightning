using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Protocol.InteractiveTx.Interfaces;
using Infrastructure.Bitcoin.InteractiveTx;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class ChainWalletPrevTxSourceTests
{
    [Fact]
    public async Task Given_BitcoindServesTheTransaction_When_Reading_Then_ItsBytesAreReturned()
    {
        // Arrange
        var tx = CreateTx();
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetTransactionAsync(tx.GetHash())).ReturnsAsync(tx);
        var source = new ChainWalletPrevTxSource(chain.Object);

        // Act
        var bytes = await source.GetTransactionAsync(new TxId(tx.GetHash().ToBytes()), 10,
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(tx.ToBytes(), bytes);
        chain.Verify(c => c.GetBlockAsync(It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public async Task Given_NoTxIndex_When_Reading_Then_TheTransactionIsTakenFromItsBlock()
    {
        // Arrange
        var tx = CreateTx();
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        block.Transactions.Add(CreateTx(7));
        block.Transactions.Add(tx);
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetTransactionAsync(It.IsAny<uint256>()))
             .ThrowsAsync(new InvalidOperationException("No such mempool or blockchain transaction"));
        chain.Setup(c => c.GetBlockAsync(42u)).ReturnsAsync(block);
        var source = new ChainWalletPrevTxSource(chain.Object);

        // Act
        var bytes = await source.GetTransactionAsync(new TxId(tx.GetHash().ToBytes()), 42,
                                                     TestContext.Current.CancellationToken);
        var unknownHeight = await source.GetTransactionAsync(new TxId(tx.GetHash().ToBytes()), 0,
                                                             TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(tx.ToBytes(), bytes);
        Assert.Null(unknownHeight);
    }

    [Fact]
    public async Task Given_TheBlockIsGone_When_Reading_Then_NullIsReturned()
    {
        // Arrange
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetTransactionAsync(It.IsAny<uint256>())).ReturnsAsync((Transaction?)null);
        chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>())).ThrowsAsync(new InvalidOperationException("pruned"));
        var source = new ChainWalletPrevTxSource(chain.Object);

        // Act
        var bytes = await source.GetTransactionAsync(TxId.One, 42, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(bytes);
    }

    [Fact]
    public void Given_TheServiceCollection_When_AddingInteractiveTxServices_Then_ThePortsResolve()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);

        // Act
        services.AddInteractiveTxBitcoinServices();
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.IsType<PrevTxInspector>(provider.GetRequiredService<IPrevTxInspector>());
        Assert.IsType<InteractiveTxBuilder>(provider.GetRequiredService<IInteractiveTxBuilder>());
        Assert.IsType<ChainWalletPrevTxSource>(provider.GetRequiredService<IWalletPrevTxSource>());
        Assert.IsType<InteractiveTxTransactionParser>(provider.GetRequiredService<IInteractiveTxTransactionParser>());
    }

    private static Transaction CreateTx(byte marker = 3)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256(Enumerable.Repeat(marker, 32).ToArray()), 0)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey));
        return tx;
    }
}
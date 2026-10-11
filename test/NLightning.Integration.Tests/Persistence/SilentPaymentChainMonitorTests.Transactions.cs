namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Events;

public sealed partial class SilentPaymentChainMonitorTests
{
    [Fact]
    public async Task Given_ASilentReceiptAndAnEmptyObservationCacheAfterRestart_When_Reorged_Then_ItsExactTransactionIsPublished()
    {
        // Arrange: join after restart so the new monitor has no cached confirmation observation.
        using var keys = new ReceiverKeys();
        await using var harness = CreateHarness(keys.Manager, EnabledOptions());
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 20, [AmountSat]);
        await harness.MineAndDeliverAsync(receipt);
        await harness.RestartAsync();
        var observed = new List<WalletTransactionEventArgs>();
        harness.Monitor.OnWalletTransactionObserved += (_, transaction) => observed.Add(transaction);

        // Act
        harness.Chain.Reorg(100, 2);
        await harness.DeliverTipAsync();

        // Assert: the SubscribeTransactions source reconstructs ownership from durable SP metadata.
        var disconnected = Assert.Single(observed, o => o.IsReorg);
        Assert.Equal(receipt.GetHash().ToString(), disconnected.TxHash);
        Assert.Equal(receipt.ToHex(), disconnected.RawTransactionHex);
        Assert.Equal(AmountSat, disconnected.AmountSat);
        Assert.Equal([0u], disconnected.OurOutputs);
        Assert.Empty(disconnected.OurInputs);
        Assert.Equal(0u, disconnected.BlockHeight);
        Assert.Equal("", disconnected.BlockHash);
        Assert.False(harness.Monitor.IsChainProcessingHalted);
    }

    [Fact]
    public async Task Given_ASilentCoinSpendAndAnEmptyObservationCacheAfterRestart_When_Reorged_Then_TheExactSpendIsPublished()
    {
        // Arrange
        using var keys = new ReceiverKeys();
        await using var harness = CreateHarness(keys.Manager, EnabledOptions());
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 21, [AmountSat]);
        await harness.MineAndDeliverAsync(receipt);
        var spender = Spend(receipt, 0);
        await harness.MineAndDeliverAsync(spender);
        await harness.RestartAsync();
        var observed = new List<WalletTransactionEventArgs>();
        harness.Monitor.OnWalletTransactionObserved += (_, transaction) => observed.Add(transaction);

        // Act
        harness.Chain.Reorg(101, 2);
        await harness.DeliverTipAsync();

        // Assert: spent ownership is retained even though the UTXO row was deleted before restart.
        var disconnected = Assert.Single(observed, o => o.IsReorg);
        Assert.Equal(spender.GetHash().ToString(), disconnected.TxHash);
        Assert.Equal(spender.ToHex(), disconnected.RawTransactionHex);
        Assert.Equal(-AmountSat, disconnected.AmountSat);
        Assert.Equal(500, disconnected.FeeSat);
        Assert.Empty(disconnected.OurOutputs);
        Assert.Equal([0u], disconnected.OurInputs);
        Assert.Equal(0u, disconnected.BlockHeight);
        Assert.False(harness.Monitor.IsChainProcessingHalted);
    }
}
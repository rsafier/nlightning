using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Repositories.Database.Accounting;
using Infrastructure.Repositories.Database.Bitcoin;
using Infrastructure.Repositories.Database.Onchain;

public sealed partial class SilentPaymentChainMonitorTests
{
    [Fact]
    public async Task Given_OurSilentPaymentSpendReturnsToMempoolAfterAReorg_When_Rewound_Then_CustodyIsRestoredButThePendingSpendExcludesSelection()
    {
        // Arrange: three confirmations make this receipt eligible for selection before its spend.
        using var keys = new ReceiverKeys();
        await using var harness = CreateHarness(keys.Manager, EnabledOptions());
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 50, [AmountSat]);
        await harness.MineAndDeliverAsync(receipt);
        await harness.MineAndDeliverAsync();
        await harness.MineAndDeliverAsync();
        var spender = Spend(receipt, 0);
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(
            new SignedTransaction(Id(spender), spender.ToBytes()), BroadcastPurpose.WalletSend, null,
            harness.Monitor.LastProcessedBlockHeight, fee: LightningMoney.Satoshis(500)));
        await harness.MineAndDeliverAsync();
        Assert.Equal(0, await WalletBalanceAsync(harness));

        // Act: Core's confirmed UTXO set restores the receipt, while the disconnected spend is back in its mempool.
        harness.Chain.Reorg(103, 1);
        harness.Chain.Mempool.Add(spender);
        var outpoint = new OutPoint(receipt.GetHash(), 0);
        Assert.Null(await harness.Chain.GetUnspentOutputAsync(outpoint));
        Assert.NotNull(await harness.Chain.GetConfirmedUnspentOutputAsync(outpoint));
        await harness.DeliverTipAsync();
        await harness.RestartAsync();

        // Assert: custody and both accounting legs recover; the persisted pending broadcast prevents a conflicting spend.
        Assert.False(harness.Monitor.IsChainProcessingHalted);
        Assert.True(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));
        await using var read = harness.Context();
        Assert.NotNull(await new UtxoDbRepository(read).GetByIdAsync(Id(receipt), 0));
        var metadata = await new SilentPaymentDbRepository(read).GetOutputAsync(Id(receipt), 0,
            TestContext.Current.CancellationToken);
        Assert.Null(metadata!.SpentByTransactionId);
        Assert.Null(metadata.SpentAtHeight);
        var pending = Assert.Single(await new BroadcastTransactionDbRepository(read).GetPendingAsync());
        Assert.Equal(Id(spender), pending.TransactionId);
        Assert.Equal(BroadcastState.Pending, pending.State);
        Assert.Equal(AmountSat * 1_000, await WalletBalanceAsync(harness));
        var balances = await new AccountingBooksDbRepository(read).GetBalancesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, balances.GetValueOrDefault(AccountRole.Clearing));
        Assert.Equal(2, (await EventsAsync(harness)).Count(e => e.Kind == AccountingEventKind.Reversal));
        var selector = new FeeInputSelector(Memory(harness), harness.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }), NullLogger<FeeInputSelector>.Instance);
        await Assert.ThrowsAsync<InsufficientFundsException>(() => selector.ReserveAsync(
            LightningMoney.Satoshis(1_000), LightningMoney.Satoshis(1_000), 100, "reorg selection proof",
            TestContext.Current.CancellationToken));
    }
}
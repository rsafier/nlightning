using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Application.Accounting;
using Application.Accounting.Books;
using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.SilentPayments;
using Infrastructure.Repositories.Database.Accounting;
using Infrastructure.Repositories.Database.Bitcoin;

public sealed partial class SilentPaymentChainMonitorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ACollaborativeSpendWithOwnedSilentChange_When_ConfirmedAndReplayed_Then_NetFlowClearsAndCustodyAndHistoryReconcile(bool metadataOnly)
    {
        // Arrange: collaborative accounting treats our external payment and fee as one net flow, without guessing
        // which part of a shared transaction's fee the wallet paid. Both owned SP custody legs must enter that delta.
        using var keys = new ReceiverKeys();
        var previous = new SenderPrevoutSource();
        await using var harness = CreateHarness(keys.Manager, EnabledOptions(), previous);
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 39, [AmountSat]);
        await harness.MineAndDeliverAsync(receipt);
        SilentPaymentOutputModel metadata;
        await using (var context = harness.Context())
            metadata = (await new SilentPaymentDbRepository(context).GetOutputAsync(Id(receipt), 0,
                TestContext.Current.CancellationToken))!;
        if (metadataOnly)
        {
            // Historical recovery may have retained custody and SP metadata without a selectable UTXO yet.
            using var scope = harness.Services.CreateScope();
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            work.TrySpendUtxo(Id(receipt), 0);
            await work.SaveChangesAsync();
            Assert.False(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));
        }
        var collaborative = SilentChangeWithdrawal(keys.Manager, receipt, metadata, externalSat: 30_000, feeSat: 1_000);
        previous.Add(collaborative, new BitcoinPrevout((ulong)AmountSat,
            new BitcoinScript(receipt.Outputs[0].ScriptPubKey.ToBytes())));
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(
            new SignedTransaction(Id(collaborative), collaborative.ToBytes()), BroadcastPurpose.WalletCollaborative, null,
            harness.Monitor.LastProcessedBlockHeight));

        // Act
        await harness.MineAndDeliverAsync();
        await harness.RestartAsync();
        await harness.DeliverTipAsync();

        // Assert: 75k spent minus 44k owned change is 31k net out, and replay keeps every fact exactly once.
        var events = await EventsAsync(harness);
        Assert.Equal(4, events.Count);
        var flow = Assert.Single(events, fact => fact.Kind == AccountingEventKind.WalletSent);
        Assert.Equal(-31_000_000L, flow.AmountMsat);
        Assert.Equal(0L, flow.FeeMsat);
        Assert.Equal("true", flow.Details["collaborative"]);
        Assert.Equal("true", flow.Details["feeUnknown"]);
        var change = Assert.Single(events, fact => fact.Kind == AccountingEventKind.WalletReceived && fact.TxId == Id(collaborative));
        Assert.Equal(44_000_000L, change.AmountMsat);
        var scopes = harness.Services.GetRequiredService<IServiceScopeFactory>();
        using var sealer = new AccountingEventSealerService(scopes, NullLogger<AccountingEventSealerService>.Instance);
        var channels = new Mock<IChannelMemoryRepository>();
        channels.Setup(repository => repository.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        var snapshot = new NodeSnapshotSource(channels.Object, Memory(harness), blockchainMonitor: harness.Monitor);
        await using var books = new AccountingBooksService(scopes, NullLogger<AccountingBooksService>.Instance,
            sealer: sealer, snapshotSource: snapshot);
        await books.ProjectNowAsync(TestContext.Current.CancellationToken);
        var reconcile = await books.ReconcileAsync(TestContext.Current.CancellationToken);
        Assert.True(reconcile.IsClean);
        Assert.Equal(0L, reconcile.OutstandingMsat);
        Assert.All(reconcile.Lines, line => Assert.Equal(0L, line.DriftMsat));
        await using var read = harness.Context();
        var balances = await new AccountingBooksDbRepository(read).GetBalancesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(44_000_000L, balances.GetValueOrDefault(AccountRole.Wallet));
        Assert.Equal(0L, balances.GetValueOrDefault(AccountRole.Clearing));
        Assert.Equal(31_000_000L, balances.GetValueOrDefault(AccountRole.TransfersOut));
        var coin = Assert.Single(await new UtxoDbRepository(read).GetUnspentAsync());
        Assert.Equal(Id(collaborative), coin.TxId);
        Assert.Equal(1u, coin.Index);
        Assert.Equal(44_000L, coin.Amount.Satoshi);
        Assert.NotNull(coin.SilentPayment);
        var history = await new WalletTransactionDbRepository(read).GetHistoryAsync(0, uint.MaxValue, true,
            TestContext.Current.CancellationToken);
        Assert.Equal(2, history.Count);
        var record = Assert.Single(history, item => item.TxId == Id(collaborative));
        Assert.Equal(collaborative.ToBytes(), record.RawTransaction);
        Assert.Equal(new uint[] { 1 }, record.OurOutputs);
        Assert.Equal(new WalletTransactionInput(0, AmountSat), Assert.Single(record.OurInputs));
        Assert.False(harness.Monitor.IsChainProcessingHalted);
    }

    [Fact]
    public async Task Given_IgnoredCollaborativeSilentChange_When_ReceivePolicyPromotesIt_Then_TheOldFlowIsImmutablyReplacedAndReplayStaysBalanced()
    {
        // Arrange: 44k change is deliberately excluded under the initial threshold, so the recorded custody delta is 75k.
        using var keys = new ReceiverKeys();
        var previous = new SenderPrevoutSource();
        var options = EnabledOptions();
        options.MinReceiveSat = 50_000;
        await using var harness = CreateHarness(keys.Manager, options, previous);
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 40, [AmountSat]);
        await harness.MineAndDeliverAsync(receipt);
        SilentPaymentOutputModel metadata;
        await using (var context = harness.Context())
            metadata = (await new SilentPaymentDbRepository(context).GetOutputAsync(Id(receipt), 0,
                TestContext.Current.CancellationToken))!;
        var collaborative = SilentChangeWithdrawal(keys.Manager, receipt, metadata, externalSat: 30_000, feeSat: 1_000);
        previous.Add(collaborative, new BitcoinPrevout((ulong)AmountSat,
            new BitcoinScript(receipt.Outputs[0].ScriptPubKey.ToBytes())));
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(
            new SignedTransaction(Id(collaborative), collaborative.ToBytes()), BroadcastPurpose.WalletCollaborative, null,
            harness.Monitor.LastProcessedBlockHeight)
        { Label = "payjoin", Tags = "purpose=trial" });
        await harness.MineAndDeliverAsync();
        var original = Assert.Single(await EventsAsync(harness), fact => fact.Kind == AccountingEventKind.WalletSent);
        Assert.Equal(-75_000_000L, original.AmountMsat);
        Assert.Equal(0L, await WalletBalanceAsync(harness));

        // Act: a historical replay discovers accepted ownership that the earlier receive policy ignored.
        options.MinReceiveSat = 0;
        var scanner = harness.Services.GetRequiredService<SilentPaymentScanner>();
        var block = harness.Chain[102];
        var serialized = new BitcoinBlock(block.ToBytes(), new Domain.Crypto.ValueObjects.Hash(block.GetHash().ToBytes()),
            block.Transactions.Count);
        using (var held = await scanner.EnterAsync(TestContext.Current.CancellationToken))
        {
            var matches = await scanner.PrepareAsync(serialized, 102, [], TestContext.Current.CancellationToken);
            using var scope = harness.Services.CreateScope();
            var work = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var promoted = await scanner.StageReceiptsAsync(matches, work, materializeUtxos: false,
                cancellationToken: TestContext.Current.CancellationToken);
            foreach (var coin in promoted)
                await SilentPaymentAccounting.StageFactAsync(work, coin.SilentPayment!, null, block, 102, [],
                    Network.RegTest, TestContext.Current.CancellationToken);
            await SilentPaymentAccounting.StageSettlementsAsync(work, block, 102, [], Network.RegTest,
                TimeProvider.System, TestContext.Current.CancellationToken);
            await WalletRecoveryAccounting.StageWalletHistoryAsync(work, block, 102, TestContext.Current.CancellationToken);
            await work.SaveChangesAsync();
            // Finalization requires current-chain proof before this historical custody becomes selectable.
            var metadataChange = Assert.Single(matches, match => match.TransactionId == Id(collaborative));
            var proof = await harness.Chain.GetConfirmedUnspentOutputAsync(new OutPoint(collaborative.GetHash(), 1));
            Assert.NotNull(proof);
            var (output, _) = proof!.Value;
            Assert.Equal(metadataChange.AmountSats, output.Value.Satoshi);
            Assert.Equal(collaborative.Outputs[1].ScriptPubKey, output.ScriptPubKey);
            work.AddUtxo(new UtxoModel(metadataChange));
            await work.SaveChangesAsync();
            // A second recovery round observes the standing corrected flow and appends nothing.
            await SilentPaymentAccounting.StageSettlementsAsync(work, block, 102, [], Network.RegTest,
                TimeProvider.System, TestContext.Current.CancellationToken);
            await work.SaveChangesAsync();
        }
        await harness.RestartAsync();
        await harness.DeliverTipAsync();

        // Assert: the immutable original and its reversal remain; precisely one corrected flow stands.
        var events = await EventsAsync(harness);
        Assert.Equal(6, events.Count);
        var flows = events.Where(fact => fact.Kind == AccountingEventKind.WalletSent).ToArray();
        Assert.Equal(2, flows.Length);
        Assert.Equal(-75_000_000L, Assert.Single(flows, fact => fact.EventKey == original.EventKey).AmountMsat);
        var reversal = Assert.Single(events, fact => fact.Kind == AccountingEventKind.Reversal);
        Assert.Equal(original.EventKey, reversal.Details[AccountingConfirmations.ReversesDetail]);
        Assert.Equal(75_000_000L, reversal.AmountMsat);
        var corrected = Assert.Single(flows, fact => fact.EventKey != original.EventKey);
        Assert.Equal(-31_000_000L, corrected.AmountMsat);
        Assert.Equal("payjoin", corrected.Details[Domain.Accounting.Constants.AccountingDetailKeys.Label]);
        Assert.Equal("true", corrected.Details["feeUnknown"]);
        Assert.Equal(44_000_000L, await WalletBalanceAsync(harness));
        await using var read = harness.Context();
        var balances = await new AccountingBooksDbRepository(read).GetBalancesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0L, balances.GetValueOrDefault(AccountRole.Clearing));
        Assert.Equal(31_000_000L, balances.GetValueOrDefault(AccountRole.TransfersOut));
        Assert.Equal(44_000L, Assert.Single(await new UtxoDbRepository(read).GetUnspentAsync()).Amount.Satoshi);
        Assert.False(harness.Monitor.IsChainProcessingHalted);
    }

}
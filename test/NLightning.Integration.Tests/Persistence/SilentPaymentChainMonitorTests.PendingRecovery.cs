using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Application.Accounting;
using Application.Accounting.Books;
using Application.Accounting.Financial;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Infrastructure.Bitcoin.Wallet.SilentPayments;
using Infrastructure.Repositories.Database.Accounting;
using Infrastructure.Repositories.Database.Bitcoin;

public sealed partial class SilentPaymentChainMonitorTests
{
    [Fact]
    public async Task Given_MetadataOnlySilentCustodyAndPendingRecovery_When_OwnChangeConfirmsAndReorgs_Then_SettlementAndBothBooksCommitAndReplayTogether()
    {
        // Arrange: live monitoring has already passed the receipt while receive was off; recovery retained custody.
        var ct = TestContext.Current.CancellationToken;
        using var keys = new ReceiverKeys();
        var options = EnabledOptions();
        var previous = new SenderPrevoutSource();
        await using var harness = CreateHarness(keys.Manager, options, previous);
        await harness.StartAsync(95);
        options.Receive = false;
        var receipt = Receipt(keys.Manager, 61, [AmountSat]);
        await harness.MineAndDeliverAsync(receipt);
        options.Receive = true;
        var block = harness.Chain[101];
        var scanner = harness.Services.GetRequiredService<SilentPaymentScanner>();
        var metadata = Assert.Single(await scanner.PrepareAsync(new BitcoinBlock(block.ToBytes(),
            new Domain.Crypto.ValueObjects.Hash(block.GetHash().ToBytes()), block.Transactions.Count), 101, [], ct));
        SilentPaymentScanState pending;
        await using (var context = harness.Context())
        {
            var repository = new SilentPaymentDbRepository(context);
            await repository.UpsertOutputAsync(metadata, ct);
            var state = (await repository.GetScanStateAsync(ct))!;
            pending = state with
            {
                RescanCursorHeight = 100, RescanCursorHash = new Domain.Crypto.ValueObjects.Hash(harness.Chain[100].GetHash().ToBytes()),
                RescanTargetHeight = 101, LiveCursorHeight = 101, LiveCursorHash = new Domain.Crypto.ValueObjects.Hash(block.GetHash().ToBytes())
            };
            await repository.SetScanStateAsync(pending, ct);
            new AccountingEventDbRepository(context).Add(new AccountingEventModel
            {
                EventKey = AccountingEventKeys.WalletReceived(Id(receipt), 0), Kind = AccountingEventKind.WalletReceived,
                OccurredAt = block.Header.BlockTime, BlockHeight = 101, TxId = Id(receipt), OutputIndex = 0,
                AmountMsat = AmountSat * 1_000, Finality = AccountingFinality.Confirmed,
                Details = AccountingDetailsCodec.Create((AccountingDetailKeys.Source, "external"), ("receiptSource", "silent_payment"))
            });
            await context.SaveChangesAsync(ct);
            Assert.Null(await new UtxoDbRepository(context).GetByIdAsync(Id(receipt), 0));
        }
        Assert.False(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));
        var withdrawal = SilentChangeWithdrawal(keys.Manager, receipt, metadata, externalSat: 20_000, feeSat: 500);
        previous.Add(withdrawal, new BitcoinPrevout((ulong)AmountSat,
            new BitcoinScript(receipt.Outputs[0].ScriptPubKey.ToBytes())));
        var scopes = harness.Services.GetRequiredService<IServiceScopeFactory>();
        using var sealer = new AccountingEventSealerService(scopes, NullLogger<AccountingEventSealerService>.Instance);
        await using var books = new AccountingBooksService(scopes, NullLogger<AccountingBooksService>.Instance, sealer: sealer);
        await using var financial = new FinancialBooksProjector(scopes, NullLogger<FinancialBooksProjector>.Instance,
            options: Options.Create(new AccountingOptions { Profile = AccountingProfile.Financial }));

        // Act: reject the complete SQL transaction, then restart and replay the exact same block.
        harness.FailCommits = true;
        await harness.MineAndDeliverAsync(withdrawal);
        Assert.True(harness.Monitor.IsChainProcessingHalted);
        Assert.True(harness.FailedCommits > 0);
        await using (var context = harness.Context())
        {
            var repository = new SilentPaymentDbRepository(context);
            Assert.Equal(pending, await repository.GetScanStateAsync(ct));
            Assert.Null((await repository.GetOutputAsync(Id(receipt), 0, ct))!.SpentByTransactionId);
            Assert.Null(await repository.GetOutputAsync(Id(withdrawal), 1, ct));
        }
        Assert.Single(await EventsAsync(harness));
        Assert.False(Memory(harness).TryGetUtxo(Id(withdrawal), 1, out _));
        harness.FailCommits = false;
        await harness.RestartAsync();
        await harness.DeliverTipAsync();
        await AssertRecoveredBooksAsync(54_500_000, -55_000_000, 500_000);
        await using (var context = harness.Context())
        {
            var repository = new SilentPaymentDbRepository(context);
            Assert.Equal(Id(withdrawal), (await repository.GetOutputAsync(Id(receipt), 0, ct))!.SpentByTransactionId);
            var state = (await repository.GetScanStateAsync(ct))!;
            Assert.Equal(100u, state.RescanCursorHeight);
            Assert.Equal(101u, state.RescanTargetHeight);
        }
        var facts = await EventsAsync(harness);
        Assert.Equal(4, facts.Count);
        var change = Assert.Single(facts, e => e.Kind == AccountingEventKind.WalletReceived && e.TxId == Id(withdrawal));
        Assert.Equal("wallet", change.Details[AccountingDetailKeys.Source]);
        Assert.Equal(54_500_000, change.AmountMsat);
        Assert.Equal(-75_000_000, Assert.Single(facts, e => e.Kind == AccountingEventKind.WalletOutputSpent).AmountMsat);
        var sent = Assert.Single(facts, e => e.Kind == AccountingEventKind.WalletSent);
        Assert.Equal(-20_000_000, sent.AmountMsat);
        Assert.Equal(500_000, sent.FeeMsat);

        // Act: disconnect the settlement, then confirm it on another branch and replay after restart.
        harness.Chain.Reorg(101, 2);
        await harness.DeliverTipAsync();
        await AssertRecoveredBooksAsync(75_000_000, -75_000_000, 0);
        Assert.Equal(3, (await EventsAsync(harness)).Count(e => e.Kind == AccountingEventKind.Reversal));
        await harness.MineAndDeliverAsync(withdrawal);
        await harness.RestartAsync();
        await harness.DeliverTipAsync();
        await AssertRecoveredBooksAsync(54_500_000, -55_000_000, 500_000);
        Assert.Equal(10, (await EventsAsync(harness)).Count);
        Assert.False(harness.Monitor.IsChainProcessingHalted);

        async Task AssertRecoveredBooksAsync(long walletMsat, long equityMsat, long feeMsat)
        {
            await books.ProjectNowAsync(ct);
            await financial.ProjectAsync(ct);
            await using var context = harness.Context();
            var repository = new AccountingBooksDbRepository(context);
            var operational = await repository.GetBalancesAsync(ct);
            Assert.Equal(walletMsat, operational.GetValueOrDefault(AccountRole.Wallet));
            Assert.Equal(0, operational.GetValueOrDefault(AccountRole.Clearing));
            var balances = await repository.GetAccountBalancesAsync(AccountingBook.Financial, ct);
            Assert.Equal(walletMsat, Assert.Single(balances, balance => balance.Account == AccountRole.Wallet).BalanceMsat);
            Assert.Equal(0, balances.Where(balance => balance.Account == AccountRole.Clearing).Sum(balance => balance.BalanceMsat));
            Assert.Equal(0, balances.Where(balance => balance.AccountName?.StartsWith("income:", StringComparison.Ordinal) == true).Sum(balance => balance.BalanceMsat));
            Assert.Equal(equityMsat, balances.Where(balance => balance.AccountName?.StartsWith("equity:", StringComparison.Ordinal) == true).Sum(balance => balance.BalanceMsat));
            Assert.Equal(feeMsat, balances.Where(balance => balance.AccountName == "expenses:fees:withdraw").Sum(balance => balance.BalanceMsat));
            var lots = await new AccountingLotDbRepository(context).ListOpenLotsAsync(AccountingLotBucket.Wallet, ct);
            Assert.Equal(walletMsat, lots.Where(lot => lot.Origin != AccountingLotOrigin.Debt).Sum(lot => lot.RemainingMsat));
        }
    }
}
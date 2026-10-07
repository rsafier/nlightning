using System.Buffers.Binary;
using System.Security.Cryptography;
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
using Domain.Accounting.Prices;
using Domain.Accounting.Services;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Crypto.SilentPayments;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Repositories.Database.Accounting;
using Infrastructure.Repositories.Database.Bitcoin;

public sealed partial class SilentPaymentChainMonitorTests
{
    [Fact]
    public async Task Given_AClassifiedSilentPaymentIncome_When_SpentWithSilentChange_Then_BothBooksPreserveIncomeAndOnlyExpenseTheActualWithdrawalFee()
    {
        // Arrange: wallet deposits default to equity transfers; the operator classifies this receipt as salary.
        using var keys = new ReceiverKeys();
        var previous = new SenderPrevoutSource();
        await using var harness = CreateHarness(keys.Manager, EnabledOptions(), previous);
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 30, [AmountSat]);
        await harness.MineAndDeliverAsync(receipt);
        var incomeKey = AccountingEventKeys.WalletReceived(Id(receipt), 0);
        var at = harness.Chain[101].Header.BlockTime;
        await using (var context = harness.Context())
        {
            await new AccountingOverrideDbRepository(context).SetAsync(
                new AccountingOverride(incomeKey, "income:salary", "SP salary", at), TestContext.Current.CancellationToken);
            Assert.True(await new AccountingPriceDbRepository(context).TryAddAsync(
                new AccountingPrice(0, "USD", at.AddHours(-1), 100_000m, AccountingPriceSource.Import, at),
                TestContext.Current.CancellationToken));
            // WalletSent/spent timestamps use the observation clock; SP receipts keep block time.
            Assert.True(await new AccountingPriceDbRepository(context).TryAddAsync(
                new AccountingPrice(0, "USD", DateTimeOffset.UtcNow.AddHours(-1), 100_000m,
                    AccountingPriceSource.Import, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var scopes = harness.Services.GetRequiredService<IServiceScopeFactory>();
        using var sealer = new AccountingEventSealerService(scopes, NullLogger<AccountingEventSealerService>.Instance);
        await using var books = new AccountingBooksService(scopes, NullLogger<AccountingBooksService>.Instance, sealer: sealer);
        await using var financial = new FinancialBooksProjector(scopes, NullLogger<FinancialBooksProjector>.Instance,
            options: Options.Create(new AccountingOptions { Profile = AccountingProfile.Financial }),
            priceOptions: Options.Create(new AccountingPriceOptions { Source = AccountingPriceSourceMode.None }));
        await books.ProjectNowAsync(TestContext.Current.CancellationToken);
        await financial.ProjectAsync(TestContext.Current.CancellationToken);
        await using (var context = harness.Context())
        {
            var balances = await new AccountingBooksDbRepository(context).GetAccountBalancesAsync(
                AccountingBook.Financial, TestContext.Current.CancellationToken);
            Assert.Equal(-AmountSat * 1_000, Assert.Single(balances, b => b.AccountName == "income:salary").BalanceMsat);
            Assert.Equal(-75m, Assert.Single(balances, b => b.AccountName == "income:salary").FiatAmount);
        }
        Domain.Bitcoin.Wallet.Models.SilentPaymentOutputModel metadata;
        await using (var context = harness.Context())
            metadata = (await new SilentPaymentDbRepository(context).GetOutputAsync(Id(receipt), 0,
                TestContext.Current.CancellationToken))!;
        var withdrawal = SilentChangeWithdrawal(keys.Manager, receipt, metadata, externalSat: 20_000, feeSat: 500);
        previous.Add(withdrawal, new BitcoinPrevout((ulong)AmountSat, new BitcoinScript(receipt.Outputs[0].ScriptPubKey.ToBytes())));
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(
            new SignedTransaction(Id(withdrawal), withdrawal.ToBytes()), BroadcastPurpose.WalletSend, null,
            harness.Monitor.LastProcessedBlockHeight, fee: LightningMoney.Satoshis(500)));

        // Act: the input moves to clearing, only 20k leaves, and 54.5k comes back as our SP change.
        await harness.MineAndDeliverAsync();
        await books.ProjectNowAsync(TestContext.Current.CancellationToken);
        await financial.ProjectAsync(TestContext.Current.CancellationToken);
        await harness.RestartAsync();
        await harness.DeliverTipAsync();
        await books.ProjectNowAsync(TestContext.Current.CancellationToken);
        await financial.ProjectAsync(TestContext.Current.CancellationToken);

        // Assert: source identity, withdrawal amount and fee are independent of the change address encoding.
        var events = await EventsAsync(harness);
        var external = Assert.Single(events, e => e.Kind == AccountingEventKind.WalletReceived && e.TxId == Id(receipt));
        Assert.Equal("external", external.Details[AccountingDetailKeys.Source]);
        var change = Assert.Single(events, e => e.Kind == AccountingEventKind.WalletReceived && e.TxId == Id(withdrawal));
        Assert.Equal(54_500_000, change.AmountMsat);
        Assert.Equal("broadcast", change.Details[AccountingDetailKeys.Source]);
        Assert.Equal("silent_payment", change.Details["receiptSource"]);
        Assert.Equal("0", change.Details["silentPaymentLabel"]);
        Assert.Equal("true", change.Details["change"]);
        var sent = Assert.Single(events, e => e.Kind == AccountingEventKind.WalletSent);
        Assert.Equal(-20_000_000, sent.AmountMsat);
        Assert.Equal(500_000, sent.FeeMsat);
        await using var read = harness.Context();
        var operational = await new AccountingBooksDbRepository(read).GetBalancesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(54_500_000, operational.GetValueOrDefault(AccountRole.Wallet));
        Assert.Equal(0, operational.GetValueOrDefault(AccountRole.Clearing));
        var projected = await new AccountingBooksDbRepository(read).GetAccountBalancesAsync(
            AccountingBook.Financial, TestContext.Current.CancellationToken);
        Assert.Equal(-75_000_000, Assert.Single(projected, b => b.AccountName == "income:salary").BalanceMsat);
        Assert.Equal(-75m, Assert.Single(projected, b => b.AccountName == "income:salary").FiatAmount);
        Assert.Equal(54_500_000, Assert.Single(projected, b => b.AccountName == "assets:onchain:wallet").BalanceMsat);
        Assert.Equal(500_000, Assert.Single(projected, b => b.AccountName == "expenses:fees:withdraw").BalanceMsat);
        Assert.Equal(0.5m, Assert.Single(projected, b => b.AccountName == "expenses:fees:withdraw").FiatAmount);
        Assert.Equal(0, projected.Where(b => b.Account == AccountRole.Clearing).Sum(b => b.BalanceMsat));
        var open = await new AccountingLotDbRepository(read).ListOpenLotsAsync(AccountingLotBucket.Wallet,
            TestContext.Current.CancellationToken);
        Assert.Equal(54_500_000, open.Where(l => l.Origin != AccountingLotOrigin.Debt).Sum(l => l.RemainingMsat));
    }

    [Fact]
    public async Task Given_AHistoricalSilentReceiptWithNoSelectableCoin_When_AnActualSpendConfirms_Then_AccountingAndTheSubscriptionIncludeItOnce()
    {
        // Arrange: the historical rescan recorded a receipt while its output was not selectable.
        using var keys = new ReceiverKeys();
        await using var harness = CreateHarness(keys.Manager, EnabledOptions());
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 31, [AmountSat]);
        var block = harness.Chain.Mine(receipt);
        var scanner = harness.Services.GetRequiredService<Infrastructure.Bitcoin.Wallet.SilentPayments.SilentPaymentScanner>();
        var matches = await scanner.PrepareAsync(new BitcoinBlock(block.ToBytes(),
            new Domain.Crypto.ValueObjects.Hash(block.GetHash().ToBytes()), block.Transactions.Count), 101, [],
            TestContext.Current.CancellationToken);
        await using (var context = harness.Context())
        {
            await new SilentPaymentDbRepository(context).UpsertOutputAsync(Assert.Single(matches), TestContext.Current.CancellationToken);
            new AccountingEventDbRepository(context).Add(new AccountingEventModel
            {
                EventKey = AccountingEventKeys.WalletReceived(Id(receipt), 0),
                Kind = AccountingEventKind.WalletReceived,
                OccurredAt = block.Header.BlockTime,
                BlockHeight = 101,
                TxId = Id(receipt),
                OutputIndex = 0,
                AmountMsat = AmountSat * 1_000,
                Finality = AccountingFinality.Confirmed,
                Details = AccountingDetailsCodec.Create((AccountingDetailKeys.Source, "external"), ("receiptSource", "silent_payment"))
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.Null(await new UtxoDbRepository(context).GetByIdAsync(Id(receipt), 0));
        }
        var spender = Spend(receipt, 0);
        var observed = new List<WalletTransactionEventArgs>();
        harness.Monitor.OnWalletTransactionObserved += (_, transaction) => observed.Add(transaction);

        // Act: live catch-up reaches the receipt and the confirming spend, then the tip is replayed.
        await harness.MineAndDeliverAsync(spender);
        await harness.DeliverTipAsync();

        // Assert: metadata-only custody still emits the real negative movement and clears the accounting balance.
        await using var read = harness.Context();
        var metadata = await new SilentPaymentDbRepository(read).GetOutputAsync(Id(receipt), 0, TestContext.Current.CancellationToken);
        Assert.Equal(Id(spender), metadata!.SpentByTransactionId);
        Assert.Equal(102u, metadata.SpentAtHeight);
        Assert.Null(await new UtxoDbRepository(read).GetByIdAsync(Id(receipt), 0));
        var events = await EventsAsync(harness);
        Assert.Equal(3, events.Count);
        Assert.Equal(-AmountSat * 1_000, Assert.Single(events, e => e.Kind == AccountingEventKind.WalletOutputSpent).AmountMsat);
        Assert.Equal(0, await WalletBalanceAsync(harness));
        var spent = Assert.Single(observed, o => o.TxHash == spender.GetHash().ToString());
        Assert.False(spent.IsReorg);
        Assert.Equal(-AmountSat, spent.AmountSat);
        Assert.Equal([0u], spent.OurInputs);
        Assert.Empty(spent.OurOutputs);
        Assert.Equal(spender.ToHex(), spent.RawTransactionHex);
        await AssertSimpleSpendSettlementAsync(harness, 0, 0);
        Assert.False(harness.Monitor.IsChainProcessingHalted);
    }

    private static Transaction SilentChangeWithdrawal(SecureKeyManager keys, Transaction receipt,
        Domain.Bitcoin.Wallet.Models.SilentPaymentOutputModel output, long externalSat, long feeSat)
    {
        var secret = keys.GetSilentPaymentSpendKey(output.Tweak, output.Label);
        var labelTweak = new byte[32];
        try
        {
            keys.GetLabelTweak(0, labelTweak);
            var spendKey = new CompactPubKey(Bip352.AddPublicTweak(keys.SpendPubKey, labelTweak));
            var previous = new OutPoint(receipt.GetHash(), output.Index);
            var serialized = new byte[36];
            previous.Hash.ToBytes().CopyTo(serialized, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(serialized.AsSpan(32), previous.N);
            var derived = Assert.Single(new SilentPaymentCrypto().DeriveOutputs(
                [new SilentPaymentSenderInput(serialized, secret, true)], [new SilentPaymentRecipient(keys.ScanPubKey, spendKey)]));
            var transaction = Network.RegTest.CreateTransaction();
            transaction.Inputs.Add(new TxIn(previous) { WitScript = new WitScript([new byte[64]]) });
            using var destination = new Key(Enumerable.Repeat((byte)0x22, 32).ToArray());
            transaction.Outputs.Add(new TxOut(Money.Satoshis(externalSat), destination.PubKey.WitHash.ScriptPubKey));
            transaction.Outputs.Add(new TxOut(Money.Satoshis(output.AmountSats - externalSat - feeSat),
                new Script([0x51, 0x20, .. derived.OutputKey32])));
            return transaction;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(labelTweak);
        }
    }
}
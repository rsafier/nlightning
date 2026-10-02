using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Repositories.Database.Accounting;
using static ChainMonitorPersistenceTests;
using static ChainWatchSchemaRoundTrip;

/// <summary>
/// The chain monitor's accounting feed writers (NL-602 A1-T2, wallet history NL-603) on the real SQLite schema: wallet
/// deposits and spends, the confirmations of our withdrawals, anchor CPFP children and bumped sweeps, each recorded
/// once in its block's save (never again for a replayed or retried block), and a reorg's reversals in the rewind's
/// save, with the facts recorded again under their next confirmation key when they confirm on the new branch.
/// </summary>
public class ChainMonitorAccountingTests
{
    private const long DepositSat = 100_000;
    private const long SentSat = 60_000;
    private const long ChangeSat = 39_000;
    private const long WithdrawFeeSat = 1_000;

    private static readonly AccountingEventKind[] s_walletKinds =
        [AccountingEventKind.WalletReceived, AccountingEventKind.WalletOutputSpent];

    [Fact]
    public async Task Given_AnExternalDeposit_When_ItsBlockIsProcessedAndReplayed_Then_OneWalletReceivedIsRecorded()
    {
        // Arrange
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 1);
        await harness.StartAsync(95);
        var deposit = CreateDeposit(0x01, wallet[0], DepositSat);
        var depositTxId = TxIdOf(deposit);

        // Act
        await harness.MineAndDeliverAsync(deposit);

        // Assert
        var received = Assert.Single(await LoadEventsAsync(harness));
        Assert.Equal(AccountingEventKeys.WalletReceived(depositTxId, 1), received.EventKey);
        Assert.Equal(AccountingEventKind.WalletReceived, received.Kind);
        Assert.Equal(DepositSat * 1_000, received.AmountMsat);
        Assert.Equal(0, received.FeeMsat);
        Assert.Equal(depositTxId, received.TxId);
        Assert.Equal(1u, received.OutputIndex);
        Assert.Equal(101u, received.BlockHeight);
        Assert.Equal(AccountingFinality.Confirmed, received.Finality);
        Assert.Null(received.ChannelId);
        Assert.Equal("external", received.Details["source"]);
        Assert.Equal(wallet[0].Model.Address, received.Details["address"]);
        Assert.Equal(nameof(AddressType.P2Wpkh), received.Details["addressType"]);
        Assert.Equal("false", received.Details["change"]);
        Assert.False(received.IsSealed);

        // Act: a restart replays the last processed block, and ZMQ announces the tip again
        await harness.RestartAsync();
        await harness.DeliverTipAsync();

        // Assert: nothing more
        Assert.Single(await LoadEventsAsync(harness));
    }

    [Fact]
    public async Task Given_ABlockWhoseSaveFails_When_ItIsProcessedAgain_Then_ItsDepositIsRecordedOnce()
    {
        // Arrange
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 1);
        await harness.StartAsync(95);
        harness.FailSaves = true;

        // Act: every attempt of block 101 fails inside its save
        await harness.MineAndDeliverAsync(CreateDeposit(0x02, wallet[0], DepositSat));

        // Assert: nothing staged by the failed attempts survived
        Assert.True(harness.Monitor.IsChainProcessingHalted);
        Assert.Empty(await LoadEventsAsync(harness));

        // Act: the failure clears and block 102 arrives
        harness.FailSaves = false;
        await harness.MineAndDeliverAsync();

        // Assert
        var received = Assert.Single(await LoadEventsAsync(harness));
        Assert.Equal(101u, received.BlockHeight);
    }

    [Fact]
    public async Task Given_OurWithdrawal_When_ItConfirms_Then_TheSpendTheChangeAndTheSendAreRecordedOnce()
    {
        // Arrange
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 2);
        await harness.StartAsync(95);
        var deposit = CreateDeposit(0x03, wallet[0], DepositSat);
        await harness.MineAndDeliverAsync(deposit);
        var external = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var withdrawal = CreateWithdrawal(deposit, 1, wallet[0], external, wallet[1]);
        var withdrawalTxId = TxIdOf(withdrawal);
        await harness.Monitor.SaveAndPublishAsync(WalletSendRow(withdrawal));

        // Act
        await harness.MineAndDeliverAsync();

        // Assert
        var inBlock = (await LoadEventsAsync(harness)).Where(e => e.BlockHeight == 102).ToList();
        Assert.Equal(3, inBlock.Count);

        var spent = Assert.Single(inBlock, e => e.Kind == AccountingEventKind.WalletOutputSpent);
        Assert.Equal(AccountingEventKeys.WalletOutputSpent(TxIdOf(deposit), 1), spent.EventKey);
        Assert.Equal(-DepositSat * 1_000, spent.AmountMsat);
        Assert.Equal(TxIdOf(deposit), spent.TxId);
        Assert.Equal(1u, spent.OutputIndex);
        Assert.Equal(withdrawalTxId.ToString(), spent.Details["spentBy"]);
        Assert.Equal("broadcast", spent.Details["source"]);
        Assert.Equal(nameof(BroadcastPurpose.WalletSend), spent.Details["purpose"]);

        var change = Assert.Single(inBlock, e => e.Kind == AccountingEventKind.WalletReceived);
        Assert.Equal(AccountingEventKeys.WalletReceived(withdrawalTxId, 1), change.EventKey);
        Assert.Equal(ChangeSat * 1_000, change.AmountMsat);
        Assert.Equal("broadcast", change.Details["source"]);
        Assert.Equal(nameof(BroadcastPurpose.WalletSend), change.Details["purpose"]);
        Assert.Equal("true", change.Details["change"]);

        var sent = Assert.Single(inBlock, e => e.Kind == AccountingEventKind.WalletSent);
        Assert.Equal(AccountingEventKeys.WalletSent(withdrawalTxId), sent.EventKey);
        Assert.Equal(-SentSat * 1_000, sent.AmountMsat);
        Assert.Equal(WithdrawFeeSat * 1_000, sent.FeeMsat);
        Assert.Equal(withdrawalTxId, sent.TxId);
        Assert.Equal(external.ToString(), sent.Details["destination"]);
        Assert.False(sent.Details.ContainsKey("feeUnknown"));

        // The wallet events sum to the wallet's outputs
        Assert.Equal(await SumUtxosMsatAsync(harness), SumOf(await LoadEventsAsync(harness), s_walletKinds));
        Assert.Equal(ChangeSat * 1_000, await SumUtxosMsatAsync(harness));

        // Act: replayed after a restart, and the next block
        await harness.RestartAsync();
        await harness.MineAndDeliverAsync();

        // Assert: nothing more
        Assert.Equal(4, (await LoadEventsAsync(harness)).Count);
    }

    [Fact]
    public async Task Given_AnAnchorCpfpChild_When_ItConfirms_Then_ItsFeeIsRecordedOnce()
    {
        // Arrange
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var child = CreateTransaction(0x04);
        var channelId = ChannelIdOf(0x04);
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(
                                                      ToSigned(child), BroadcastPurpose.AnchorCpfp, channelId, 100,
                                                      2_500, fee: LightningMoney.Satoshis(2_000)));

        // Act
        await harness.MineAndDeliverAsync();
        await harness.RestartAsync();
        await harness.MineAndDeliverAsync();

        // Assert
        var cpfp = Assert.Single(await LoadEventsAsync(harness));
        Assert.Equal(AccountingEventKeys.AnchorCpfpFee(TxIdOf(child)), cpfp.EventKey);
        Assert.Equal(AccountingEventKind.AnchorCpfpFee, cpfp.Kind);
        Assert.Equal(0, cpfp.AmountMsat);
        Assert.Equal(2_000_000, cpfp.FeeMsat);
        Assert.Equal(channelId, cpfp.ChannelId);
        Assert.Equal(TxIdOf(child), cpfp.TxId);
        Assert.Equal(101u, cpfp.BlockHeight);
    }

    [Fact]
    public async Task Given_ABumpedSweep_When_TheReplacementConfirms_Then_ItsExtraFeeOverTheOriginalIsRecorded()
    {
        // Arrange: a sweep (500 sat), bumped twice (900 sat, then 1,500 sat), and a sweep never bumped
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var channelId = ChannelIdOf(0x05);
        var original = CreateTransaction(0x05);
        var firstBump = CreateTransaction(0x06);
        var secondBump = CreateTransaction(0x07);
        var plain = CreateTransaction(0x08);
        await harness.Monitor.SaveAndPublishAsync(SweepRow(original, channelId, 500, null));
        await ReplaceAsync(harness, original, SweepRow(firstBump, channelId, 900, TxIdOf(original)));
        await ReplaceAsync(harness, firstBump, SweepRow(secondBump, channelId, 1_500, TxIdOf(firstBump)));
        await harness.Monitor.SaveAndPublishAsync(SweepRow(plain, channelId, 400, null));

        // Act: the last replacement and the plain sweep confirm
        var block = harness.Chain.Mine(false, secondBump, plain);
        await harness.Monitor.ProcessNewBlockAsync(block, harness.Chain.TipHeight);

        // Assert: only the bump is recorded, for 1,500 - 500 sat
        var bump = Assert.Single(await LoadEventsAsync(harness));
        Assert.Equal(AccountingEventKeys.SweepFeeBump(TxIdOf(secondBump)), bump.EventKey);
        Assert.Equal(AccountingEventKind.SweepFeeBump, bump.Kind);
        Assert.Equal(0, bump.AmountMsat);
        Assert.Equal(1_000_000, bump.FeeMsat);
        Assert.Equal(channelId, bump.ChannelId);
        Assert.Equal(TxIdOf(firstBump).ToString(), bump.Details["replaces"]);
        Assert.Equal(TxIdOf(original).ToString(), bump.Details["originalTxId"]);
        Assert.Equal("1500", bump.Details["feeSat"]);
        Assert.Equal("500", bump.Details["originalFeeSat"]);
    }

    [Fact]
    public async Task Given_AReorgOfOurWithdrawal_When_ItConfirmsAgain_Then_ItsEventsAreReversedAndRecordedAgain()
    {
        // Arrange: 101 holds a deposit, 102 our withdrawal spending it (with change)
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 2);
        await harness.StartAsync(95);
        var deposit = CreateDeposit(0x09, wallet[0], DepositSat);
        await harness.MineAndDeliverAsync(deposit);
        var external = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var withdrawal = CreateWithdrawal(deposit, 1, wallet[0], external, wallet[1]);
        var withdrawalTxId = TxIdOf(withdrawal);
        await harness.Monitor.SaveAndPublishAsync(WalletSendRow(withdrawal));
        await harness.MineAndDeliverAsync();
        Assert.Equal(4, (await LoadEventsAsync(harness)).Count);

        // Act: a branch from 100 whose first block holds the deposit alone becomes the active chain
        harness.Chain.Reorg(100, 3, deposit);
        await harness.DeliverTipAsync();

        // Assert: the spend, the change and the send of 102 are reversed in the rewind; the deposit stands (its output
        // is back in the wallet, found again at 101 on the new branch)
        Assert.Equal(103u, harness.Monitor.LastProcessedBlockHeight);
        var events = await LoadEventsAsync(harness);
        var reversals = events.Where(e => e.Kind == AccountingEventKind.Reversal).ToList();
        Assert.Equal(3, reversals.Count);
        Assert.All(reversals, r =>
        {
            Assert.Equal(102u, r.BlockHeight);
            Assert.Equal("100", r.Details[AccountingConfirmations.ForkHeightDetail]);
        });
        var reversedKeys = reversals.Select(r => r.Details[AccountingConfirmations.ReversesDetail]).ToHashSet();
        Assert.Equal(new HashSet<string>
                     {
                         AccountingEventKeys.WalletOutputSpent(TxIdOf(deposit), 1),
                         AccountingEventKeys.WalletReceived(withdrawalTxId, 1),
                         AccountingEventKeys.WalletSent(withdrawalTxId)
                     }, reversedKeys);
        var sentReversal = Assert.Single(reversals, r => r.EventKey
                                                       == AccountingEventKeys.Reversal(
                                                              AccountingEventKeys.WalletSent(withdrawalTxId), 102));
        Assert.Equal(SentSat * 1_000, sentReversal.AmountMsat);
        Assert.Equal(-WithdrawFeeSat * 1_000, sentReversal.FeeMsat);
        Assert.Equal(DepositSat * 1_000, await SumUtxosMsatAsync(harness));
        Assert.Equal(DepositSat * 1_000, SumOf(events, s_walletKinds));

        // Act: the withdrawal (sent again after the rewind) confirms on the new branch
        Assert.Contains(harness.Chain.Mempool, t => t.GetHash() == withdrawal.GetHash());
        await harness.MineAndDeliverAsync();

        // Assert: recorded again under the second confirmation keys, and the books match the wallet again
        events = await LoadEventsAsync(harness);
        var again = events.Where(e => e.BlockHeight == 104).ToList();
        Assert.Equal(new HashSet<string>
                     {
                         AccountingEventKeys.Reconfirmed(AccountingEventKeys.WalletOutputSpent(TxIdOf(deposit), 1), 2),
                         AccountingEventKeys.Reconfirmed(AccountingEventKeys.WalletReceived(withdrawalTxId, 1), 2),
                         AccountingEventKeys.Reconfirmed(AccountingEventKeys.WalletSent(withdrawalTxId), 2)
                     }, again.Select(e => e.EventKey).ToHashSet());
        Assert.Equal(ChangeSat * 1_000, await SumUtxosMsatAsync(harness));
        Assert.Equal(ChangeSat * 1_000, SumOf(events, s_walletKinds));
        Assert.Equal(-SentSat * 1_000, SumOf(events, [AccountingEventKind.WalletSent]));
        Assert.Equal(events.Count, events.Select(e => e.EventKey).Distinct().Count());
    }

    [Fact]
    public async Task Given_ADepositAndItsSpendBothReorgedOut_When_TheyConfirmAgain_Then_BothAreReversedAndRecordedAgain()
    {
        // Arrange: 101 holds a deposit, 102 a foreign spend of it (our key signed it; not one of our rows)
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 1);
        await harness.StartAsync(95);
        var deposit = CreateDeposit(0x0a, wallet[0], DepositSat);
        await harness.MineAndDeliverAsync(deposit);
        var spend = CreateWithdrawal(deposit, 1, wallet[0],
                                     new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest), null);
        await harness.MineAndDeliverAsync(spend);

        // Act: a branch from 100 with neither transaction
        harness.Chain.Reorg(100, 3);
        await harness.DeliverTipAsync();

        // Assert: both are reversed (the wallet holds nothing either way)
        var events = await LoadEventsAsync(harness);
        Assert.Equal(2, events.Count(e => e.Kind == AccountingEventKind.Reversal));
        Assert.Equal(0, SumOf(events, s_walletKinds));
        Assert.Equal(0, await SumUtxosMsatAsync(harness));

        // Act: both confirm on the new branch
        await harness.MineAndDeliverAsync(deposit);
        await harness.MineAndDeliverAsync(spend);

        // Assert
        events = await LoadEventsAsync(harness);
        Assert.Contains(events, e => e.EventKey == AccountingEventKeys.Reconfirmed(
                                         AccountingEventKeys.WalletReceived(TxIdOf(deposit), 1), 2));
        var spent = Assert.Single(events, e => e.EventKey == AccountingEventKeys.Reconfirmed(
                                                   AccountingEventKeys.WalletOutputSpent(TxIdOf(deposit), 1), 2));
        Assert.Equal("wallet", spent.Details["source"]);
        Assert.Equal(0, SumOf(events, s_walletKinds));
    }

    [Fact]
    public async Task Given_ADepositFromBeforeTheFeed_When_ReorgedOut_Then_AnUnrecordedReversalKeepsTheBooksInLine()
    {
        // Arrange: a deposit whose event does not exist (the node ran without the feed then)
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 1);
        await harness.StartAsync(95);
        var deposit = CreateDeposit(0x0b, wallet[0], DepositSat);
        await harness.MineAndDeliverAsync(deposit);
        await using (var context = harness.Context())
        {
            context.AccountingEvents.RemoveRange(context.AccountingEvents);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act: a branch from 100 without it, then the deposit confirms again
        harness.Chain.Reorg(100, 3);
        await harness.DeliverTipAsync();
        var afterRewind = await LoadEventsAsync(harness);
        await harness.MineAndDeliverAsync(deposit);

        // Assert: the removal is reversed under a key of its own, and the new confirmation takes the first key
        var baseKey = AccountingEventKeys.WalletReceived(TxIdOf(deposit), 1);
        var reversal = Assert.Single(afterRewind);
        Assert.Equal(AccountingEventKeys.Reversal(baseKey, 101) + ":unrecorded", reversal.EventKey);
        Assert.Equal(-DepositSat * 1_000, reversal.AmountMsat);
        Assert.Equal("true", reversal.Details[AccountingConfirmations.UnrecordedDetail]);
        var received = Assert.Single(await LoadEventsAsync(harness), e => e.Kind == AccountingEventKind.WalletReceived);
        Assert.Equal(baseKey, received.EventKey);
        Assert.Equal(104u, received.BlockHeight);
    }

    private static async Task<List<(WalletAddressModel Model, Key Key)>> SeedWalletAsync(ChainMonitorHarness harness,
                                                                                       int count)
    {
        var wallet = new List<(WalletAddressModel, Key)>();
        for (var i = 0; i < count; i++)
        {
            var key = new Key();
            var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();
            wallet.Add((new WalletAddressModel(AddressType.P2Wpkh, (uint)i, i > 0, address), key));
        }

        using var scope = harness.Services.CreateScope();
        using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        uow.WalletAddressesDbRepository.AddRange(wallet.Select(w => w.Item1).ToList());
        await uow.SaveChangesAsync();
        return wallet;
    }

    /// <summary>A foreign transaction paying <paramref name="amountSat"/> to the wallet address at output 1.</summary>
    private static Transaction CreateDeposit(byte seed, (WalletAddressModel Model, Key Key) to, long amountSat)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat(seed, 32).ToArray()), 7));
        transaction.Outputs.Add(Money.Satoshis(25_000), new Key().PubKey.WitHash.ScriptPubKey);
        transaction.Outputs.Add(Money.Satoshis(amountSat), BitcoinAddress.Create(to.Model.Address, Network.RegTest));
        return transaction;
    }

    /// <summary>
    /// A spend of the wallet output (a P2WPKH witness with the wallet key, as the rollback recognizes our inputs):
    /// <see cref="SentSat"/> to <paramref name="external"/>, and <see cref="ChangeSat"/> to <paramref name="change"/>.
    /// </summary>
    private static Transaction CreateWithdrawal(Transaction deposit, uint vout,
                                                (WalletAddressModel Model, Key Key) from, BitcoinAddress external,
                                                (WalletAddressModel Model, Key Key)? change)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(deposit.GetHash(), vout));
        transaction.Inputs[0].WitScript = new WitScript(Op.GetPushOp(new byte[71]),
                                                        Op.GetPushOp(from.Key.PubKey.ToBytes()));
        transaction.Outputs.Add(Money.Satoshis(SentSat), external);
        if (change is { } changeAddress)
            transaction.Outputs.Add(Money.Satoshis(ChangeSat),
                                    BitcoinAddress.Create(changeAddress.Model.Address, Network.RegTest));
        return transaction;
    }

    private static BroadcastTransactionModel WalletSendRow(Transaction withdrawal) =>
        new(ToSigned(withdrawal), BroadcastPurpose.WalletSend, null, 101, 253,
            fee: LightningMoney.Satoshis(WithdrawFeeSat));

    private static BroadcastTransactionModel SweepRow(Transaction sweep, ChannelId channelId,
                                                      long feeSat, TxId? replaces) =>
        new(ToSigned(sweep), BroadcastPurpose.Sweep, channelId, 100, 1_000, replaces,
            fee: LightningMoney.Satoshis(feeSat));

    /// <summary>What the sweep scheduler does: the old row replaced and the new one stored in one save, then sent.</summary>
    private static async Task ReplaceAsync(ChainMonitorHarness harness, Transaction replaced,
                                           BroadcastTransactionModel replacement)
    {
        using (var scope = harness.Services.CreateScope())
        {
            using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await uow.BroadcastTransactionDbRepository.MarkReplacedAsync(TxIdOf(replaced));
            uow.BroadcastTransactionDbRepository.Add(replacement);
            await uow.SaveChangesAsync();
        }

        await harness.Monitor.PublishAsync(replacement);
    }

    private static async Task<List<AccountingEventModel>> LoadEventsAsync(ChainMonitorHarness harness)
    {
        await using var context = harness.Context();
        return (await new AccountingEventDbRepository(context)
                   .GetAtOrAboveHeightAsync(0, Enum.GetValues<AccountingEventKind>(),
                                            TestContext.Current.CancellationToken))
              .ToList();
    }

    private static async Task<long> SumUtxosMsatAsync(ChainMonitorHarness harness)
    {
        await using var context = harness.Context();
        return await context.Utxos.AsNoTracking().SumAsync(u => u.AmountSats, TestContext.Current.CancellationToken)
             * 1_000;
    }

    /// <summary>The sum of the events of <paramref name="kinds"/> and of their reversals.</summary>
    private static long SumOf(IEnumerable<AccountingEventModel> events, AccountingEventKind[] kinds) =>
        events.Where(e => kinds.Contains(e.Kind)
                       || (e.Kind == AccountingEventKind.Reversal
                        && kinds.Any(k => e.Details[AccountingConfirmations.OriginalKindDetail] == k.ToString())))
              .Sum(e => e.AmountMsat);

    private static TxId TxIdOf(Transaction transaction) => new(transaction.GetHash().ToBytes());

    private static SignedTransaction ToSigned(Transaction transaction) => new(TxIdOf(transaction), transaction.ToBytes());
}
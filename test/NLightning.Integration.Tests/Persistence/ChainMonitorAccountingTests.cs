using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Tests.Utils.Accounting;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Labels;
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
using Infrastructure.Repositories.Database.Onchain;
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

        // NL-602 A2 (the books): the wallet account equals the UTXO table, the clearing account nets to zero (the
        // spent output against the change and the withdrawal), and the deposit and the withdrawal are transfers
        var books = BooksSimulator.Of(await LoadEventsAsync(harness));
        Assert.Equal(await SumUtxosMsatAsync(harness), books[AccountRole.Wallet]);
        Assert.Equal(0, books[AccountRole.Clearing]);
        Assert.Equal(-DepositSat * 1_000, books[AccountRole.TransfersIn]);
        Assert.Equal(SentSat * 1_000, books[AccountRole.TransfersOut]);
        Assert.Equal(WithdrawFeeSat * 1_000, books[AccountRole.FeeWithdraw]);

        // Act: replayed after a restart, and the next block
        await harness.RestartAsync();
        await harness.MineAndDeliverAsync();

        // Assert: nothing more
        Assert.Equal(4, (await LoadEventsAsync(harness)).Count);
    }

    [Fact]
    public async Task Given_OurSplice_When_ItConfirms_Then_ItsWalletEventsCarryTheSplicePurpose()
    {
        // Arrange (NL-626): a splice row spending a wallet output (the new funding output stands in for the external
        // output), saved by SpliceService with the Splice purpose
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 2);
        await harness.StartAsync(95);
        var deposit = CreateDeposit(0x0d, wallet[0], DepositSat);
        await harness.MineAndDeliverAsync(deposit);
        var newFunding = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var splice = CreateWithdrawal(deposit, 1, wallet[0], newFunding, wallet[1]);
        var channelId = ChannelIdOf(0x0d);
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(
                                                       ToSigned(splice), BroadcastPurpose.Splice, channelId, 101, 253,
                                                       fee: LightningMoney.Satoshis(WithdrawFeeSat)));

        // Act
        await harness.MineAndDeliverAsync();

        // Assert: the spend and the change say Splice, and a splice is no withdrawal (its lock books it)
        var inBlock = (await LoadEventsAsync(harness)).Where(e => e.BlockHeight == 102).ToList();
        var spent = Assert.Single(inBlock, e => e.Kind == AccountingEventKind.WalletOutputSpent);
        Assert.Equal("broadcast", spent.Details["source"]);
        Assert.Equal(nameof(BroadcastPurpose.Splice), spent.Details["purpose"]);
        Assert.Equal(channelId, spent.ChannelId);
        var change = Assert.Single(inBlock, e => e.Kind == AccountingEventKind.WalletReceived);
        Assert.Equal(nameof(BroadcastPurpose.Splice), change.Details["purpose"]);
        Assert.DoesNotContain(inBlock, e => e.Kind == AccountingEventKind.WalletSent);
    }

    [Fact]
    public async Task Given_ALabelledWithdrawal_When_ItConfirms_Then_WalletSentCarriesTheLabelAndTags()
    {
        // Arrange (NL-602 A3-T1): withdraw --label/--tag stores them on the WalletSend row
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 2);
        await harness.StartAsync(95);
        var deposit = CreateDeposit(0x03, wallet[0], DepositSat);
        await harness.MineAndDeliverAsync(deposit);
        var external = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var withdrawal = CreateWithdrawal(deposit, 1, wallet[0], external, wallet[1]);
        var labels = SourceLabels.Create("cold storage", ["category=savings", "vault=b"]);
        var row = WalletSendRow(withdrawal);
        row.Label = labels.Label;
        row.Tags = labels.CanonicalTags;
        await harness.Monitor.SaveAndPublishAsync(row);

        // Act
        await harness.MineAndDeliverAsync();

        // Assert: the send carries them; the wallet movements of the same transaction do not (they are transfers)
        var events = await LoadEventsAsync(harness);
        var sent = Assert.Single(events, e => e.Kind == AccountingEventKind.WalletSent);
        Assert.Equal("cold storage", sent.Details[AccountingDetailKeys.Label]);
        Assert.Equal("savings", sent.Details[AccountingDetailKeys.TagPrefix + "category"]);
        Assert.Equal("b", sent.Details["tag.vault"]);
        Assert.Equal("cold storage", SourceLabels.FromDetails(sent.Details).Label);
        Assert.Equal(labels.CanonicalTags, SourceLabels.FromDetails(sent.Details).CanonicalTags);
        Assert.All(events.Where(e => e.Kind != AccountingEventKind.WalletSent),
                   e => Assert.False(e.Details.ContainsKey(AccountingDetailKeys.Label)));
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
    public async Task Given_ABumpedSweep_When_ItsOriginalConfirmsInstead_Then_TheOriginalIsConfirmedAndNoBumpRecorded()
    {
        // Arrange (NL-606): a sweep (500 sat) bumped once (900 sat); the original confirms anyway
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var channelId = ChannelIdOf(0x15);
        var original = CreateTransaction(0x15);
        var bump = CreateTransaction(0x16);
        await harness.Monitor.SaveAndPublishAsync(SweepRow(original, channelId, 500, null));
        await ReplaceAsync(harness, original, SweepRow(bump, channelId, 900, TxIdOf(original)));

        // Act: the original confirms (the mempool's replacement left out), then another block
        await MineOnlyAsync(harness, original);
        var sentBefore = harness.Chain.SendAttempts.Count;
        await MineOnlyAsync(harness);

        // Assert: the original's row is Confirmed, its replacement Replaced (never sent again), and no fee bump is
        // recorded (the original's fee is the resolution's whole fee)
        var originalRow = await LoadBroadcastAsync(harness, TxIdOf(original));
        Assert.Equal(BroadcastState.Confirmed, originalRow.State);
        Assert.Equal(101u, originalRow.ConfirmedHeight);
        Assert.Equal(BroadcastState.Replaced, (await LoadBroadcastAsync(harness, TxIdOf(bump))).State);
        Assert.Empty(await LoadEventsAsync(harness));
        Assert.DoesNotContain(harness.Chain.SendAttempts.Skip(sentBefore), t => t.GetHash() == bump.GetHash());
    }

    [Theory]
    [InlineData(BroadcastPurpose.Splice)]
    [InlineData(BroadcastPurpose.Funding)]
    public async Task Given_ASpliceRbfAttempt_When_TheAttemptItBumpsConfirms_Then_TheBumpStaysPendingAndIsSentAgain(
        BroadcastPurpose purpose)
    {
        // Arrange (NL-736): a splice RBF keeps every attempt Pending on purpose (wave SPR): the bump names the attempt
        // it bumps in ReplacesTransactionId but never marks it Replaced, and the splice lock abandons the losers. The
        // NL-606 voiding marked the bump Replaced when the first attempt confirmed, so after a reorg of that block the
        // splice had no attempt left to send
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var channelId = ChannelIdOf(0x1C);
        var first = CreateTransaction(0x1C);
        var bump = CreateTransaction(0x1D);
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(
                                                      ToSigned(first), purpose, channelId, 100, 1_000,
                                                      fee: LightningMoney.Satoshis(500)));
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(
                                                      ToSigned(bump), purpose, channelId, 100, 1_500, TxIdOf(first),
                                                      fee: LightningMoney.Satoshis(900)));

        // Act: the first attempt confirms, then another block
        await MineOnlyAsync(harness, first);
        var sentBefore = harness.Chain.SendAttempts.Count;
        await MineOnlyAsync(harness);

        // Assert: the bump is still Pending and still sent each round, until the splice lock abandons it
        Assert.Equal(BroadcastState.Confirmed, (await LoadBroadcastAsync(harness, TxIdOf(first))).State);
        Assert.Equal(BroadcastState.Pending, (await LoadBroadcastAsync(harness, TxIdOf(bump))).State);
        Assert.Contains(harness.Chain.SendAttempts.Skip(sentBefore), t => t.GetHash() == bump.GetHash());
    }

    [Fact]
    public async Task Given_ASweepBumpedTwice_When_TheMiddleAttemptConfirms_Then_ItsBumpOverTheOriginalIsRecorded()
    {
        // Arrange (NL-606): 500 sat, bumped to 900, then to 1,500; the 900 sat attempt confirms
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var channelId = ChannelIdOf(0x17);
        var original = CreateTransaction(0x17);
        var firstBump = CreateTransaction(0x18);
        var secondBump = CreateTransaction(0x19);
        await harness.Monitor.SaveAndPublishAsync(SweepRow(original, channelId, 500, null));
        await ReplaceAsync(harness, original, SweepRow(firstBump, channelId, 900, TxIdOf(original)));
        await ReplaceAsync(harness, firstBump, SweepRow(secondBump, channelId, 1_500, TxIdOf(firstBump)));

        // Act
        await MineOnlyAsync(harness, firstBump);

        // Assert
        Assert.Equal(BroadcastState.Confirmed, (await LoadBroadcastAsync(harness, TxIdOf(firstBump))).State);
        Assert.Equal(BroadcastState.Replaced, (await LoadBroadcastAsync(harness, TxIdOf(original))).State);
        Assert.Equal(BroadcastState.Replaced, (await LoadBroadcastAsync(harness, TxIdOf(secondBump))).State);
        var fee = Assert.Single(await LoadEventsAsync(harness));
        Assert.Equal(AccountingEventKeys.SweepFeeBump(TxIdOf(firstBump)), fee.EventKey);
        Assert.Equal(400_000, fee.FeeMsat);
        Assert.Equal(TxIdOf(original).ToString(), fee.Details["originalTxId"]);
    }

    [Fact]
    public async Task Given_AReplacedAnchorCpfpChild_When_ItConfirmsInstead_Then_ItsFeeIsRecorded()
    {
        // Arrange (NL-606): a CPFP child (2,000 sat) replaced by one paying 3,000 sat; the first one confirms
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        var channelId = ChannelIdOf(0x1A);
        var child = CreateTransaction(0x1A);
        var replacement = CreateTransaction(0x1B);
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(
                                                      ToSigned(child), BroadcastPurpose.AnchorCpfp, channelId, 100,
                                                      2_500, fee: LightningMoney.Satoshis(2_000)));
        await ReplaceAsync(harness, child,
                           new BroadcastTransactionModel(ToSigned(replacement), BroadcastPurpose.AnchorCpfp, channelId,
                                                         100, 4_000, TxIdOf(child),
                                                         fee: LightningMoney.Satoshis(3_000)));

        // Act
        await MineOnlyAsync(harness, child);
        await harness.RestartAsync();
        await MineOnlyAsync(harness);

        // Assert
        var cpfp = Assert.Single(await LoadEventsAsync(harness));
        Assert.Equal(AccountingEventKeys.AnchorCpfpFee(TxIdOf(child)), cpfp.EventKey);
        Assert.Equal(2_000_000, cpfp.FeeMsat);
        Assert.Equal(BroadcastState.Confirmed, (await LoadBroadcastAsync(harness, TxIdOf(child))).State);
        Assert.Equal(BroadcastState.Replaced, (await LoadBroadcastAsync(harness, TxIdOf(replacement))).State);
    }

    [Fact]
    public async Task Given_AnAnchorCpfpChildWithAWalletInput_When_ItConfirms_Then_TheBooksClearingNetsWithItsAnchor()
    {
        // Arrange: a deposit, then our CPFP child spending our anchor (330 sat) and that deposit, its change back to
        // the wallet; its fee is the anchor plus the input minus the change (NL-604, what AnchorCpfpService stores)
        await using var harness = new ChainMonitorHarness();
        var wallet = await SeedWalletAsync(harness, 2);
        await harness.StartAsync(95);
        var deposit = CreateDeposit(0x0c, wallet[0], DepositSat);
        await harness.MineAndDeliverAsync(deposit);
        const long anchorSat = 330;
        const long childChangeSat = DepositSat - 1_000;
        var anchor = new OutPoint(new uint256(Enumerable.Repeat((byte)0x0d, 32).ToArray()), 2);
        var child = Network.RegTest.CreateTransaction();
        child.Inputs.Add(anchor);
        child.Inputs.Add(new OutPoint(deposit.GetHash(), 1));
        child.Inputs[1].WitScript = new WitScript(Op.GetPushOp(new byte[71]), Op.GetPushOp(wallet[0].Key.PubKey.ToBytes()));
        child.Outputs.Add(Money.Satoshis(childChangeSat), BitcoinAddress.Create(wallet[1].Model.Address, Network.RegTest));
        var channelId = ChannelIdOf(0x0c);
        await harness.Monitor.SaveAndPublishAsync(new BroadcastTransactionModel(
                                                      ToSigned(child), BroadcastPurpose.AnchorCpfp, channelId, 101,
                                                      2_500,
                                                      fee: LightningMoney.Satoshis(anchorSat + DepositSat
                                                                                 - childChangeSat)));

        // Act
        await harness.MineAndDeliverAsync();

        // Assert: the wallet events and the CPFP fee leave the anchor's value owed to the clearing account
        var events = await LoadEventsAsync(harness);
        Assert.Contains(events, e => e.Kind == AccountingEventKind.AnchorCpfpFee);
        var books = BooksSimulator.Of(events);
        Assert.Equal(await SumUtxosMsatAsync(harness), books[AccountRole.Wallet]);
        Assert.Equal(childChangeSat * 1_000, books[AccountRole.Wallet]);
        Assert.Equal((anchorSat + DepositSat - childChangeSat) * 1_000, books[AccountRole.FeeCpfp]);
        Assert.Equal(-anchorSat * 1_000, books[AccountRole.Clearing]);

        // Act: the executor's resolution of the anchor merged into the child (OnchainAccounting's format: a counted
        // anchor of a close we funded, "merged" note)
        books.Apply(new AccountingEventModel
        {
            EventKey = AccountingEventKeys.OutputResolved(TxIdOf(deposit), 7),
            Kind = AccountingEventKind.OutputResolved,
            OccurredAt = DateTimeOffset.UnixEpoch,
            BlockHeight = 102,
            ChannelId = channelId,
            AmountMsat = -anchorSat * 1_000,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create(("pendingOutMsat", "330000"), ("pendingInMsat", "0"),
                                                    ("walletMsat", "0"), ("counted", "true"), ("resolvedBy", "us"),
                                                    ("valueMsat", "330000"), ("descriptor", "OurAnchor"),
                                                    ("note", AccountingDetailKeys.MergedNote))
        });

        // Assert: the clearing account nets to zero; the anchor left the pending bucket the close had put it in
        Assert.Equal(0, books[AccountRole.Clearing]);
        Assert.Equal(-anchorSat * 1_000, books[AccountRole.Pending]);
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

        // NL-602 A2 (the books): the reversals undid the withdrawal's postings: the deposit alone stands
        var rewound = BooksSimulator.Of(events);
        Assert.Equal(DepositSat * 1_000, rewound[AccountRole.Wallet]);
        Assert.Equal(0, rewound[AccountRole.Clearing]);
        Assert.Equal(0, rewound[AccountRole.TransfersOut]);
        Assert.Equal(0, rewound[AccountRole.FeeWithdraw]);

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

        // NL-602 A2 (the books): after the second confirmation the books match the wallet again
        var books = BooksSimulator.Of(events);
        Assert.Equal(await SumUtxosMsatAsync(harness), books[AccountRole.Wallet]);
        Assert.Equal(0, books[AccountRole.Clearing]);
        Assert.Equal(SentSat * 1_000, books[AccountRole.TransfersOut]);
        Assert.Equal(WithdrawFeeSat * 1_000, books[AccountRole.FeeWithdraw]);
    }

    [Fact]
    public async Task Given_AMutualCloseConfirmedAboveTheFork_When_TheChainRewinds_Then_ItIsReversedInTheRewind()
    {
        // Arrange (NL-607): the channel manager recorded a mutual close confirmed at 102 (and one at 100, below the
        // fork); then a branch from 100 becomes the active chain
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        await harness.MineAndDeliverAsync();
        await harness.MineAndDeliverAsync();
        var above = MutualClose(ChannelIdOf(0x31), CreateTransaction(0x31), 102);
        var below = MutualClose(ChannelIdOf(0x32), CreateTransaction(0x32), 100);
        using (var scope = harness.Services.CreateScope())
        {
            using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            uow.AccountingEventDbRepository.Add(above);
            uow.AccountingEventDbRepository.Add(below);
            await uow.SaveChangesAsync();
        }

        // Act
        harness.Chain.Reorg(100, 3);
        await harness.DeliverTipAsync();

        // Assert: the close above the fork is reversed (its closing watch is pending again), the one below stands
        var events = await LoadEventsAsync(harness);
        var reversal = Assert.Single(events, e => e.Kind == AccountingEventKind.Reversal);
        Assert.Equal(AccountingEventKeys.Reversal(above.EventKey, 102), reversal.EventKey);
        Assert.Equal(above.EventKey, reversal.Details[AccountingConfirmations.ReversesDetail]);
        Assert.Equal(-above.AmountMsat, reversal.AmountMsat);
        Assert.Equal(-above.FeeMsat, reversal.FeeMsat);
        var books = BooksSimulator.Of(events.Where(e => e.EventKey != below.EventKey));
        Assert.All(books.Balances.Values, balance => Assert.Equal(0, balance));
    }

    [Fact]
    public async Task Given_AMemoMutualCloseAboveTheFork_When_TheChainRewinds_Then_ItIsNotReversed()
    {
        // Arrange (NL-737): the backfill's memo close of a channel closed before the cutover, confirmed at 102
        await using var harness = new ChainMonitorHarness();
        await harness.StartAsync(95);
        await harness.MineAndDeliverAsync();
        await harness.MineAndDeliverAsync();
        var closing = CreateTransaction(0x33);
        var memo = new AccountingEventModel
        {
            EventKey = AccountingEventKeys.ChannelClosedMutual(ChannelIdOf(0x33), TxIdOf(closing)),
            Kind = AccountingEventKind.ChannelClosedMutual,
            OccurredAt = DateTimeOffset.UnixEpoch,
            BlockHeight = 102,
            ChannelId = ChannelIdOf(0x33),
            TxId = TxIdOf(closing),
            AmountMsat = -500_000_000,
            FeeMsat = 1_000_000,
            Finality = AccountingFinality.Confirmed,
            Details = new Dictionary<string, string> { [AccountingDetailKeys.Memo] = "true" }
        };
        using (var scope = harness.Services.CreateScope())
        {
            using var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            uow.AccountingEventDbRepository.Add(memo);
            await uow.SaveChangesAsync();
        }

        // Act
        harness.Chain.Reorg(100, 3);
        await harness.DeliverTipAsync();

        // Assert: nothing reversed, so the close is never recorded again as a real, posting event
        Assert.DoesNotContain(await LoadEventsAsync(harness), e => e.Kind == AccountingEventKind.Reversal);
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

        // NL-602 A2 (the books): the wallet is empty; the spend is no stored broadcast of ours, so nothing books where
        // its output went and the clearing account keeps it (a reconcile finding, plan §6.1)
        var books = BooksSimulator.Of(events);
        Assert.Equal(0, books[AccountRole.Wallet]);
        Assert.Equal(DepositSat * 1_000, books[AccountRole.Clearing]);
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

    /// <summary>A mutual close of 500,000 sat (1,000 sat closing fee paid by us), as the channel manager records it.
    /// </summary>
    private static AccountingEventModel MutualClose(ChannelId channelId, Transaction closing, uint height) => new()
    {
        EventKey = AccountingEventKeys.ChannelClosedMutual(channelId, TxIdOf(closing)),
        Kind = AccountingEventKind.ChannelClosedMutual,
        OccurredAt = DateTimeOffset.UnixEpoch,
        BlockHeight = height,
        ChannelId = channelId,
        TxId = TxIdOf(closing),
        AmountMsat = -500_000_000,
        FeeMsat = 1_000_000,
        Finality = AccountingFinality.Confirmed
    };

    /// <summary>Mines a block holding only <paramref name="transactions"/> (not the mempool) and delivers it.</summary>
    private static async Task MineOnlyAsync(ChainMonitorHarness harness, params Transaction[] transactions)
    {
        var block = harness.Chain.Mine(false, transactions);
        await harness.Monitor.ProcessNewBlockAsync(block, harness.Chain.TipHeight);
    }

    private static async Task<BroadcastTransactionModel> LoadBroadcastAsync(ChainMonitorHarness harness, TxId txId)
    {
        await using var context = harness.Context();
        return await new BroadcastTransactionDbRepository(context).GetByTransactionIdAsync(txId)
            ?? throw new InvalidOperationException($"No broadcast row {txId}");
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
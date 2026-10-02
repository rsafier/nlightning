using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Application.Accounting.Backfill;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Accounting;
using Infrastructure.Repositories.Memory;

/// <summary>
/// The accounting backfill (NL-602 A1-T6) on the real SQLite schema and unit of work: the cutover's opening balances
/// read from the database, its marker and idempotency, the skip on a node whose feed already has events, and the memo
/// history written with the live writers' keys, resumable after a cancellation.
/// </summary>
public sealed class AccountingBackfillTests : IAsyncLifetime
{
    private const uint TipHeight = 812;
    private const ulong ToLocalSat = 50_000;
    private const ulong OfferedHtlcSat = 20_000;
    private const ulong SecondLevelSat = 15_000;

    private static readonly DateTimeOffset s_cutoverAt = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualClock _clock = new(s_cutoverAt);
    private SqliteDbTestContext? _db;
    private ServiceProvider? _provider;

    private SqliteDbTestContext Db => _db ?? throw new InvalidOperationException("Not initialized");

    private ServiceProvider Provider => _provider ?? throw new InvalidOperationException("Not initialized");

    public async ValueTask InitializeAsync()
    {
        _db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(_ => CreateUnitOfWork());
        _provider = services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        if (_db is not null)
            await _db.DisposeAsync();
    }

    [Fact]
    public async Task Given_AFreshNode_When_TheCutoverRuns_Then_OnlyTheMarkerIsWritten()
    {
        // Arrange
        await using var backfill = CreateBackfill();

        // Act
        var result = await backfill.EnsureCutoverAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(AccountingCutoverOutcome.Written, result.Outcome);
        var marker = Assert.Single(await ReadEventsAsync());
        Assert.Equal(AccountingEventKeys.Cutover(), marker.EventKey);
        Assert.Equal(AccountingEventKind.OpeningBalance, marker.Kind);
        Assert.Equal(0, marker.AmountMsat);
        Assert.Equal(AccountingEventFlags.Backfilled, marker.Flags);
        Assert.Equal(s_cutoverAt, marker.OccurredAt);
        Assert.Equal(s_cutoverAt.ToString("O"), marker.Details["cutoverAt"]);
        Assert.Equal("0", marker.Details["channels"]);
        Assert.Equal("0", marker.Details["walletUtxos"]);
    }

    [Fact]
    public async Task Given_ChannelsWalletAndAResolvingClose_When_TheCutoverRuns_Then_OpeningBalancesAreWritten()
    {
        // Arrange
        var fixture = await SeedNodeAsync();
        await using var backfill = CreateBackfill();

        // Act
        var result = await backfill.EnsureCutoverAsync(TestContext.Current.CancellationToken);

        // Assert: the counts
        Assert.Equal(AccountingCutoverOutcome.Written, result.Outcome);
        Assert.Equal(TipHeight, result.BlockHeight);
        Assert.Equal(1, result.ChannelCount);
        Assert.Equal(1, result.PendingChannelCount);
        Assert.Equal(1, result.AwaitingFundingCount);
        Assert.Equal(2, result.WalletUtxoCount);

        var events = (await ReadEventsAsync()).ToDictionary(e => e.EventKey);
        Assert.Equal(5, events.Count);

        // The open channel: our gross local balance, nothing for the channel awaiting its funding
        var channel = events[AccountingEventKeys.OpeningBalance($"channel:{fixture.Open.ChannelId}")];
        Assert.Equal(AccountingEventKind.OpeningBalance, channel.Kind);
        Assert.Equal(600_000_000, channel.AmountMsat);
        Assert.Equal(AccountingEventFlags.Backfilled, channel.Flags);
        Assert.Equal(AccountingFinality.Final, channel.Finality);
        Assert.Equal(fixture.Open.ChannelId, channel.ChannelId);
        Assert.Equal(fixture.Open.ShortChannelId, channel.ShortChannelId);
        Assert.Equal(SqliteDbTestContext.RemoteNodeId, channel.Counterparty);
        Assert.Equal(TipHeight, channel.BlockHeight);
        Assert.Equal("channel", channel.Details["bucket"]);
        Assert.Equal("1000000", channel.Details["capacitySat"]);
        Assert.Equal(nameof(ChannelState.Open), channel.Details["state"]);
        Assert.DoesNotContain(events.Keys, k => k.Contains(fixture.AwaitingFunding.ChannelId.ToString(),
                                                           StringComparison.Ordinal));

        // The wallet: every UTXO row
        var wallet = events[AccountingEventKeys.OpeningBalance("wallet")];
        Assert.Equal(150_000_000, wallet.AmountMsat);
        Assert.Equal("2", wallet.Details["utxoCount"]);

        // The resolving close: the unresolved counted outputs, and the synthetic close the resolution writer reads
        var pending = events[AccountingEventKeys.OpeningBalance($"pending:{fixture.Resolving.ChannelId}")];
        Assert.Equal((long)(ToLocalSat + OfferedHtlcSat + SecondLevelSat) * 1_000, pending.AmountMsat);
        Assert.Equal(fixture.Close.CommitmentTransactionId, pending.TxId);
        Assert.Equal("onchain-pending", pending.Details["bucket"]);
        var close = events[AccountingEventKeys.ChannelForceClosed(fixture.Resolving.ChannelId,
                                                                  fixture.Close.CommitmentTransactionId)];
        Assert.Equal(AccountingEventKind.ChannelForceClosed, close.Kind);
        Assert.Equal(0, close.AmountMsat);
        Assert.Equal(0, close.FeeMsat);
        Assert.Equal(AccountingEventFlags.Backfilled, close.Flags);
        Assert.Equal(fixture.Close.SpentAtHeight, close.BlockHeight);
        Assert.Equal("0,1", close.Details["countedVouts"]);
        Assert.Equal("true", close.Details["openingBalance"]);
        Assert.Equal(nameof(ChannelCloseKind.LocalCommitment), close.Details["closeKind"]);

        // The marker carries the totals
        var marker = events[AccountingEventKeys.Cutover()];
        Assert.Equal("600000000", marker.Details["channelMsat"]);
        Assert.Equal("150000000", marker.Details["walletMsat"]);
        Assert.Equal("1", marker.Details["awaitingFunding"]);
        Assert.Equal(TipHeight.ToString(), marker.Details["blockHeight"]);
    }

    [Fact]
    public async Task Given_TheCutoverWritten_When_TheNodeStartsAgain_Then_NothingIsWritten()
    {
        // Arrange
        await SeedNodeAsync();
        await using var backfill = CreateBackfill();
        await backfill.EnsureCutoverAsync(TestContext.Current.CancellationToken);
        var before = (await ReadEventsAsync()).Count;
        await AddWalletUtxoAsync(9, 70_000);

        // Act
        var second = await backfill.EnsureCutoverAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(AccountingCutoverOutcome.AlreadyDone, second.Outcome);
        Assert.Equal(before, (await ReadEventsAsync()).Count);
    }

    [Fact]
    public async Task Given_AFeedWithEventsAndNoCutover_When_TheCutoverRuns_Then_OnlyTheMarkerIsWrittenAndSkipped()
    {
        // Arrange: a dev node that ran the feed before the backfill existed
        await SeedNodeAsync();
        using (var uow = CreateUnitOfWork())
        {
            uow.AccountingEventDbRepository.Add(new AccountingEventModel
            {
                EventKey = AccountingEventKeys.WalletReceived(new TxId(Fill(0x77)), 0),
                Kind = AccountingEventKind.WalletReceived,
                OccurredAt = s_cutoverAt.AddDays(-1),
                AmountMsat = 1_000,
                Finality = AccountingFinality.Confirmed
            });
            await uow.SaveChangesAsync();
        }

        await using var backfill = CreateBackfill();

        // Act
        var result = await backfill.EnsureCutoverAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(AccountingCutoverOutcome.SkippedOpening, result.Outcome);
        var events = await ReadEventsAsync();
        Assert.Equal(2, events.Count);
        var marker = Assert.Single(events, e => e.EventKey == AccountingEventKeys.Cutover());
        Assert.Equal("true", marker.Details["skippedOpening"]);
        Assert.DoesNotContain(events, e => e.EventKey.StartsWith("open:channel", StringComparison.Ordinal)
                                         || e.EventKey == AccountingEventKeys.OpeningBalance("wallet"));
    }

    [Fact]
    public async Task Given_AFailedCutover_When_LiveEventsFollowAndTheNodeStartsAgain_Then_TheOpeningBalancesAreWritten()
    {
        // Arrange: the cutover fails (NL-619), the node runs on and a live writer adds an event
        await SeedNodeAsync();
        var gate = new AccountingFeedGate();
        await using (var failing = new AccountingBackfillService(new FailingScopeFactory(),
                                                                 NullLogger<AccountingBackfillService>.Instance,
                                                                 _clock, feedGate: gate))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.EnsureCutoverAsync(
                                                                    TestContext.Current.CancellationToken));
            failing.StartMemoBackfill();
            await failing.MemoTask;
        }

        Assert.True(gate.IsHeld);
        using (var uow = new UnitOfWork(Db.CreateDbContext(), NullLogger<UnitOfWork>.Instance, Db.Sha256,
                                        new UtxoMemoryRepository(), _clock, accountingFeedGate: gate))
        {
            uow.AccountingEventDbRepository.Add(new AccountingEventModel
            {
                EventKey = AccountingEventKeys.WalletReceived(new TxId(Fill(0x78)), 0),
                Kind = AccountingEventKind.WalletReceived,
                OccurredAt = s_cutoverAt,
                AmountMsat = 1_000,
                Finality = AccountingFinality.Confirmed
            });
            await uow.SaveChangesAsync();
        }

        Assert.Equal(1, gate.DroppedCount);
        Assert.Empty(await ReadEventsAsync());
        await using var backfill = CreateBackfill();

        // Act: the next start
        var result = await backfill.EnsureCutoverAsync(TestContext.Current.CancellationToken);

        // Assert: the opening balances, not the dev-node path that writes none
        Assert.Equal(AccountingCutoverOutcome.Written, result.Outcome);
        var events = (await ReadEventsAsync()).ToDictionary(e => e.EventKey);
        Assert.Contains(AccountingEventKeys.OpeningBalance("wallet"), events.Keys);
        Assert.DoesNotContain(events.Values, e => e.Kind == AccountingEventKind.WalletReceived);
        Assert.False(events[AccountingEventKeys.Cutover()].Details.ContainsKey("skippedOpening"));
    }

    [Fact]
    public async Task Given_TheGateHeld_When_ACutoverSucceeds_Then_TheGateOpens()
    {
        // Arrange
        var gate = new AccountingFeedGate();
        gate.Hold("an earlier attempt failed");
        await using var backfill = new AccountingBackfillService(Provider.GetRequiredService<IServiceScopeFactory>(),
                                                                 NullLogger<AccountingBackfillService>.Instance, _clock,
                                                                 feedGate: gate);

        // Act
        await backfill.EnsureCutoverAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(gate.IsHeld);
        Assert.True(gate.Admits(AccountingEventKeys.WalletReceived(new TxId(Fill(0x79)), 0)));
    }

    [Fact]
    public async Task Given_HistoryBeforeTheCutover_When_TheMemoRuns_Then_EveryFactIsWrittenOnceUnderItsLiveKey()
    {
        // Arrange
        var fixture = await SeedNodeAsync();
        var history = await SeedHistoryAsync(fixture);
        await using var backfill = CreateBackfill();
        await backfill.EnsureCutoverAsync(TestContext.Current.CancellationToken);

        // A live event after the cutover for one of the invoices (the switch settled it before the memo pass)
        using (var uow = CreateUnitOfWork())
        {
            uow.AccountingEventDbRepository.Add(new AccountingEventModel
            {
                EventKey = AccountingEventKeys.InvoiceSettled(history.LiveInvoiceHash),
                Kind = AccountingEventKind.InvoiceSettled,
                OccurredAt = s_cutoverAt.AddMinutes(1),
                AmountMsat = 7_000,
                Finality = AccountingFinality.Final
            });
            await uow.SaveChangesAsync();
        }

        // Act
        var result = await backfill.RunMemoBackfillAsync(TestContext.Current.CancellationToken);

        // Assert: the counts
        Assert.True(result.Completed);
        Assert.Equal(2, result.Invoices);
        Assert.Equal(2, result.Payments);
        Assert.Equal(1, result.Forwards);
        Assert.Equal(4, result.Channels);
        Assert.Equal(1, result.Skipped);

        var events = await ReadEventsAsync();
        Assert.Equal(events.Count, events.Select(e => e.EventKey).Distinct().Count());
        var memo = events.Where(e => e.Details.ContainsKey("memo") && e.EventKey != AccountingEventKeys.MemoComplete())
                         .ToDictionary(e => e.EventKey);
        foreach (var e in memo.Values)
        {
            Assert.Equal("true", e.Details["memo"]);
            Assert.True(e.Flags.HasFlag(AccountingEventFlags.Backfilled));
        }

        // The live keys and amounts
        var invoice = memo[AccountingEventKeys.InvoiceSettled(history.InvoiceHash)];
        Assert.Equal(AccountingEventKind.InvoiceSettled, invoice.Kind);
        Assert.Equal(250_000, invoice.AmountMsat);
        Assert.Equal(history.SettledAt, invoice.OccurredAt);
        Assert.False(invoice.Details.ContainsKey("parts"));
        Assert.True(memo.ContainsKey(AccountingEventKeys.InvoiceSettled(history.KeysendHash)));
        Assert.False(memo.ContainsKey(AccountingEventKeys.InvoiceSettled(history.LiveInvoiceHash)));
        Assert.DoesNotContain(events, e => e.EventKey == AccountingEventKeys.InvoiceSettled(history.LateInvoiceHash));

        var succeeded = memo[AccountingEventKeys.PaymentSucceeded(history.SucceededHash)];
        Assert.Equal(-(100_000 + 1_000), succeeded.AmountMsat);
        Assert.Equal(1_000, succeeded.FeeMsat);
        var failed = memo[AccountingEventKeys.PaymentFailed(history.FailedHash, history.FailedCreatedAt.UtcTicks)];
        Assert.Equal(0, failed.AmountMsat);

        var forward = memo[AccountingEventKeys.ForwardSettled(fixture.Open.ChannelId, 3)];
        Assert.Equal(AccountingEventKind.ForwardSettled, forward.Kind);
        Assert.Equal(2_000, forward.AmountMsat);

        // The open channel's funding (it held an opening balance), the closed channel's funding and mutual close
        var funded = memo[AccountingEventKeys.ChannelFunded(fixture.Open.ChannelId,
                                                            fixture.Open.FundingOutput!.TransactionId!.Value)];
        Assert.Equal(1_000_000_000, funded.AmountMsat);
        Assert.True(memo.ContainsKey(AccountingEventKeys.ChannelFunded(
                                         history.Closed.ChannelId,
                                         history.Closed.FundingOutput!.TransactionId!.Value)));
        var mutual = memo[AccountingEventKeys.ChannelClosedMutual(history.Closed.ChannelId,
                                                                  history.Closed.ClosingTransaction!.TxId)];
        Assert.Equal(-600_000_000, mutual.AmountMsat);
        Assert.Equal(700u, mutual.BlockHeight);

        // The channel awaiting its funding at the cutover has no memo (its ChannelFunded is a live fact)
        Assert.DoesNotContain(memo.Keys, k => k.Contains(fixture.AwaitingFunding.ChannelId.ToString(),
                                                         StringComparison.Ordinal));
        Assert.Single(events, e => e.EventKey == AccountingEventKeys.MemoComplete());

        // Act: a second run, then a second start
        var again = await backfill.RunMemoBackfillAsync(TestContext.Current.CancellationToken);

        // Assert: nothing more
        Assert.True(again.Completed);
        Assert.Equal(0, again.Written);
        Assert.Equal(events.Count, (await ReadEventsAsync()).Count);
    }

    [Fact]
    public async Task Given_AMemoPassCancelledMidway_When_ItRunsAgain_Then_ItResumesWithoutDuplicates()
    {
        // Arrange: one invoice per page, cancelled when the third scope opens (the second page)
        var fixture = await SeedNodeAsync();
        await SeedHistoryAsync(fixture);
        await using (var first = CreateBackfill())
            await first.EnsureCutoverAsync(TestContext.Current.CancellationToken);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var cancellingFactory = new CancellingScopeFactory(Provider.GetRequiredService<IServiceScopeFactory>(),
                                                           cancelAtScope: 3, cancellation);
        await using var interrupted = new AccountingBackfillService(cancellingFactory,
                                                                    NullLogger<AccountingBackfillService>.Instance,
                                                                    _clock, batchSize: 1);

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => interrupted.RunMemoBackfillAsync(cancellation.Token));
        var afterCancel = await ReadEventsAsync();
        await using var resumed = CreateBackfill(batchSize: 1);
        var result = await resumed.RunMemoBackfillAsync(TestContext.Current.CancellationToken);

        // Assert: the first page was saved, the rest written by the second run, every key once
        Assert.Single(afterCancel, e => e.Kind == AccountingEventKind.InvoiceSettled);
        Assert.DoesNotContain(afterCancel, e => e.EventKey == AccountingEventKeys.MemoComplete());
        Assert.True(result.Completed);
        Assert.Equal(2, result.Invoices);
        Assert.Equal(1, result.Skipped);
        var events = await ReadEventsAsync();
        Assert.Equal(events.Count, events.Select(e => e.EventKey).Distinct().Count());
        Assert.Equal(3, events.Count(e => e.Kind == AccountingEventKind.InvoiceSettled));
    }

    [Fact]
    public async Task Given_NoCutover_When_TheMemoRuns_Then_NothingIsWritten()
    {
        // Arrange
        await using var backfill = CreateBackfill();

        // Act
        var result = await backfill.RunMemoBackfillAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Completed);
        Assert.Empty(await ReadEventsAsync());
    }

    private AccountingBackfillService CreateBackfill(int batchSize = AccountingBackfillService.DefaultBatchSize) =>
        new(Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AccountingBackfillService>.Instance, _clock,
            batchSize);

    private UnitOfWork CreateUnitOfWork() =>
        new(Db.CreateDbContext(), NullLogger<UnitOfWork>.Instance, Db.Sha256, new UtxoMemoryRepository(), _clock);

    private async Task<IReadOnlyList<AccountingEventModel>> ReadEventsAsync()
    {
        await using var context = Db.CreateDbContext();
        return await new AccountingEventDbRepository(context).GetUnsealedAsync(1_000,
                                                                              TestContext.Current.CancellationToken);
    }

    private sealed record NodeFixture(ChannelModel Open, ChannelModel AwaitingFunding, ChannelModel Resolving,
                                      ChannelCloseModel Close);

    /// <summary>
    /// An open channel we fund, a channel awaiting its funding, a channel resolving our commitment on chain (to_local
    /// and our offered HTLC unresolved, our received HTLC, a peer output and a resolved output not counted, a
    /// second-level output waiting) and two wallet outputs, at tip <see cref="TipHeight"/>.
    /// </summary>
    private async Task<NodeFixture> SeedNodeAsync()
    {
        var open = SqliteDbTestContext.CreateChannel(true, channelTag: 1);
        open.ShortChannelId = new ShortChannelId(800, 3, 1);
        open.FundingCreatedAtBlockHeight = 800;
        var awaiting = SqliteDbTestContext.CreateChannel(false, state: ChannelState.V1FundingSigned, channelTag: 2);
        var resolving = SqliteDbTestContext.CreateChannel(true, state: ChannelState.OnchainResolving, channelTag: 3);
        resolving.ShortChannelId = new ShortChannelId(790, 1, 1);

        var commitmentTxId = new TxId(Fill(0xC1));
        var secondLevelTxId = new TxId(Fill(0xC2));
        var close = new ChannelCloseModel(resolving.ChannelId, ChannelCloseKind.LocalCommitment, commitmentTxId, 5, 805,
                                          new Hash(Fill(0x55)), s_cutoverAt.AddHours(-2));

        using var uow = CreateUnitOfWork();
        await uow.ChannelDbRepository.AddAsync(open);
        await uow.ChannelDbRepository.AddAsync(awaiting);
        await uow.ChannelDbRepository.AddAsync(resolving);
        uow.BlockchainStateDbRepository.Add(new BlockchainState(TipHeight, new Hash(Fill(0x12)), DateTime.UtcNow));

        var onchain = uow.OnchainResolutionDbRepository;
        await onchain.UpsertCloseAsync(close);
        await onchain.UpsertOutputAsync(Row(resolving, commitmentTxId, 0, OutputDescriptorKind.DelayedToLocal,
                                            ToLocalSat, OutputResolutionState.Pending));
        await onchain.UpsertOutputAsync(Row(resolving, commitmentTxId, 1, OutputDescriptorKind.LocalOfferedHtlc,
                                            OfferedHtlcSat, OutputResolutionState.Broadcast, HtlcDirection.Outgoing));
        await onchain.UpsertOutputAsync(Row(resolving, commitmentTxId, 2, OutputDescriptorKind.LocalReceivedHtlc,
                                            30_000, OutputResolutionState.Pending, HtlcDirection.Incoming));
        await onchain.UpsertOutputAsync(Row(resolving, commitmentTxId, 3, OutputDescriptorKind.PeerOutput, 400_000,
                                            OutputResolutionState.Pending));
        await onchain.UpsertOutputAsync(Row(resolving, commitmentTxId, 4, OutputDescriptorKind.DelayedToLocal, 9_000,
                                            OutputResolutionState.Resolved));
        await onchain.UpsertOutputAsync(Row(resolving, secondLevelTxId, 0, OutputDescriptorKind.DelayedToLocal,
                                            SecondLevelSat, OutputResolutionState.Waiting, HtlcDirection.Outgoing));
        await uow.SaveChangesAsync();

        await AddWalletUtxoAsync(1, 100_000);
        await AddWalletUtxoAsync(2, 50_000);
        return new NodeFixture(open, awaiting, resolving, close);
    }

    private sealed record HistoryFixture(Hash InvoiceHash, Hash KeysendHash, Hash LiveInvoiceHash,
                                         Hash LateInvoiceHash, DateTimeOffset SettledAt, Hash SucceededHash,
                                         Hash FailedHash, DateTimeOffset FailedCreatedAt, ChannelModel Closed);

    /// <summary>
    /// Three invoices settled before the cutover (one also recorded live after it), one settled after it, a succeeded
    /// and a failed payment, a fulfilled forward and a channel closed mutually before the cutover.
    /// </summary>
    private async Task<HistoryFixture> SeedHistoryAsync(NodeFixture node)
    {
        var settledAt = s_cutoverAt.AddDays(-3);
        var invoiceHash = new Hash(Fill(0x01));
        var keysendHash = new Hash(Fill(0x02));
        var liveHash = new Hash(Fill(0x03));
        var lateHash = new Hash(Fill(0x04));
        var succeededHash = new Hash(Fill(0x05));
        var failedHash = new Hash(Fill(0x06));
        var failedCreatedAt = s_cutoverAt.AddDays(-2);

        var closed = SqliteDbTestContext.CreateChannel(true, state: ChannelState.Closed, channelTag: 4);
        closed.ShortChannelId = new ShortChannelId(600, 2, 1);
        closed.FundingCreatedAtBlockHeight = 600;
        var shutdownScript = new Key().PubKey.WitHash.ScriptPubKey;
        closed.SetLocalShutdownScript(new BitcoinScript(shutdownScript.ToBytes()));
        var closingTx = Network.RegTest.CreateTransaction();
        closingTx.Inputs.Add(new OutPoint(new uint256(closed.FundingOutput!.TransactionId!.Value), 1));
        closingTx.Outputs.Add(Money.Satoshis(599_000), shutdownScript);
        closingTx.Outputs.Add(Money.Satoshis(400_000), new Key().PubKey.WitHash.ScriptPubKey);
        var closingTxId = new TxId(closingTx.GetHash().ToBytes());
        closed.SetClosingTransaction(new SignedTransaction(closingTxId, closingTx.ToBytes()));

        using var uow = CreateUnitOfWork();
        await uow.InvoiceDbRepository.AddAsync(SettledInvoice(invoiceHash, 250_000, settledAt));
        await uow.InvoiceDbRepository.AddAsync(SettledInvoice(keysendHash, 3_000, settledAt.AddHours(1)));
        await uow.InvoiceDbRepository.AddAsync(SettledInvoice(liveHash, 7_000, settledAt.AddHours(2)));
        await uow.InvoiceDbRepository.AddAsync(SettledInvoice(lateHash, 9_000, s_cutoverAt.AddMinutes(5)));

        var succeeded = new PaymentModel(succeededHash, "lnbcrt1succeeded", SqliteDbTestContext.RemoteNodeId,
                                         LightningMoney.MilliSatoshis(100_000), LightningMoney.MilliSatoshis(1_000),
                                         s_cutoverAt.AddDays(-4));
        succeeded.AddOutgoingHtlc(node.Open.ChannelId, 0);
        succeeded.Succeed(new Secret(Fill(0x15)), s_cutoverAt.AddDays(-4).AddSeconds(3));
        await uow.PaymentDbRepository.AddAsync(succeeded);
        var failed = new PaymentModel(failedHash, "lnbcrt1failed", SqliteDbTestContext.RemoteNodeId,
                                      LightningMoney.MilliSatoshis(50_000), LightningMoney.MilliSatoshis(500),
                                      failedCreatedAt);
        failed.Fail(null, null, "no route", failedCreatedAt.AddSeconds(2));
        await uow.PaymentDbRepository.AddAsync(failed);

        var circuit = new ForwardCircuitModel(node.Open.ChannelId, 3, LightningMoney.MilliSatoshis(52_000), 600,
                                              new Hash(Fill(0x07)), new Secret(Fill(0x17)),
                                              new ShortChannelId(790, 1, 1), LightningMoney.MilliSatoshis(50_000), 560,
                                              s_cutoverAt.AddDays(-1));
        circuit.AddOutgoingHtlc(node.Resolving.ChannelId, 9);
        circuit.MarkFulfilled(s_cutoverAt.AddDays(-1).AddSeconds(1));
        await uow.ForwardCircuitDbRepository.AddAsync(circuit);

        await uow.ChannelDbRepository.AddAsync(closed);
        var watch = new WatchedTransactionModel(closed.ChannelId, closingTxId, 6);
        watch.SetHeightAndIndex(700, 4);
        uow.WatchedTransactionDbRepository.Add(watch);
        await uow.SaveChangesAsync();

        return new HistoryFixture(invoiceHash, keysendHash, liveHash, lateHash, settledAt, succeededHash, failedHash,
                                  failedCreatedAt, closed);
    }

    private async Task AddWalletUtxoAsync(byte seed, long amountSat)
    {
        using var uow = CreateUnitOfWork();
        var address = new WalletAddressModel(AddressType.P2Wpkh, seed, false, $"bcrt1qbackfill{seed}");
        uow.WalletAddressesDbRepository.AddRange([address]);
        uow.AddUtxo(new UtxoModel(new TxId(Fill(seed)), 0, LightningMoney.Satoshis(amountSat), 700, address));
        await uow.SaveChangesAsync();
    }

    private static InvoiceModel SettledInvoice(Hash hash, ulong amountMsat, DateTimeOffset settledAt) =>
        new(hash, new Secret(Fill(0x31)), new Secret(Fill(0x32)), LightningMoney.MilliSatoshis(amountMsat),
            "backfill", "lnbcrt1backfill", settledAt.AddMinutes(-1), 3_600, 40, InvoiceStatus.Settled,
            LightningMoney.MilliSatoshis(amountMsat), settledAt);

    private static OutputResolutionModel Row(ChannelModel channel, TxId txId, uint vout, OutputDescriptorKind kind,
                                             ulong amountSat, OutputResolutionState state,
                                             HtlcDirection? direction = null) =>
        new()
        {
            TransactionId = txId,
            OutputIndex = vout,
            ChannelId = channel.ChannelId,
            Descriptor = kind,
            DescriptorData = new OutputDescriptorData(amountSat, [0x00, 0x14, .. new byte[20]], null, 0, false, null,
                                                      null).Encode(),
            HtlcDirection = direction,
            HtlcId = direction is null ? null : vout,
            State = state
        };

    private static byte[] Fill(byte value)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, value);
        return bytes;
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A scope factory whose database is unreachable.</summary>
    private sealed class FailingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("The database is unreachable");
    }

    /// <summary>Cancels <paramref name="cancellation"/> when the <paramref name="cancelAtScope"/>th scope opens.
    /// </summary>
    private sealed class CancellingScopeFactory(IServiceScopeFactory inner, int cancelAtScope,
                                                CancellationTokenSource cancellation) : IServiceScopeFactory
    {
        private int _scopes;

        public IServiceScope CreateScope()
        {
            if (Interlocked.Increment(ref _scopes) == cancelAtScope)
                cancellation.Cancel();
            return inner.CreateScope();
        }
    }
}
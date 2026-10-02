using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Accounting;

namespace NLightning.Application.Tests.Onchain;

using Application.Accounting.Backfill;
using Application.Channels.Safety.Interfaces;
using Application.Channels.Services;
using Application.Onchain;
using Application.Onchain.Accounting;
using Application.Onchain.Interfaces;
using Application.Payments;
using Application.Protocol.Factories;
using Channels.Services;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Serialization;

/// <summary>
/// NL-602 A1-T2 (force close and on-chain resolution): the real <see cref="OnchainChannelWatcher"/> over real
/// commitments (<see cref="RealSigningCommitmentPair"/>, Alice's view, an HTLC each way) records
/// <see cref="AccountingEventKind.ChannelForceClosed"/> in the close's save, and the real
/// <see cref="OnchainResolutionExecutor"/> records one resolution event per output in the save of its move to
/// <see cref="OutputResolutionState.Resolved"/> (a scripted resolver stands in for the BOLT 5 resolvers: it only adds
/// the second-level row of our HTLC transaction and gives outputs up when told). Replays write nothing, reorgs write
/// reversals, and every scenario ends with the pending on-chain bucket of the close at zero
/// (<see cref="OnchainAccounting"/>).
/// </summary>
public sealed class OnchainAccountingTests : IDisposable
{
    private const uint SpendHeight = 600;
    private const ulong OurHtlcSat = 20_000;
    private const ulong TheirHtlcSat = 30_000;

    private static readonly DateTimeOffset s_now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: false);
    private readonly OnchainTestStore _store = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChannelErrorSender> _errorSender = new();
    private readonly Mock<IOnchainResolutionExecutor> _executorSeenByTheWatcher = new();
    private readonly Mock<IOutpointWatcher> _outpointWatcher = new();
    private readonly Mock<IChainBroadcaster> _broadcaster = new();
    private readonly Mock<ISecretStorageServiceFactory> _shachainFactory = new();
    private readonly ScriptedResolver _resolver = new();
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    public OnchainAccountingTests()
    {
        // Arrange (shared): an HTLC each way, committed on both sides
        _pair.Add(_pair.Alice, OurHtlcSat * 1_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Add(_pair.Bob, TheirHtlcSat * 1_000, RealSigningCommitmentPair.Preimage(2));
        _pair.Settle(_pair.Alice);
        _channel = _pair.Alice.Channel;
        _channel.UpdateCommitments(_pair.Alice.State);

        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = id == _channel.ChannelId ? _channel : null;
                    return channel is not null;
                }));
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => predicate(_channel) ? [_channel] : []);
        _errorSender.Setup(s => s.TrySendAsync(It.IsAny<CompactPubKey>(), It.IsAny<ErrorMessage>()))
                    .ReturnsAsync(true);
        _broadcaster.Setup(b => b.PublishAsync(It.IsAny<BroadcastTransactionModel>())).ReturnsAsync(true);

        var unitOfWork = _store.CreateUnitOfWork();
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(new Mock<IRemoteShachainDbRepository>().Object);
        _store.LoadChannel = id => id == _channel.ChannelId ? _pair.Bob.Channel : null;
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new Domain.Node.Options.NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSerializationInfrastructureServices();
        services.AddSingleton(_pair.Alice.Signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddOnchainBitcoinServices();
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        services.AddSingleton(_memory.Object);
        services.AddSingleton(_errorSender.Object);
        services.AddSingleton(_executorSeenByTheWatcher.Object);
        services.AddSingleton(_outpointWatcher.Object);
        services.AddSingleton(_shachainFactory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddSingleton<TimeProvider>(new FixedClock(s_now));
        services.AddScoped(_ => unitOfWork.Object);
        services.AddScoped<IOutputResolver>(_ => _resolver);
        services.AddSingleton<OnchainChannelWatcher>();
        _provider = services.BuildServiceProvider();
    }

    private OnchainChannelWatcher Watcher => _provider.GetRequiredService<OnchainChannelWatcher>();

    [Fact]
    public async Task Given_OurCommitment_When_FundingSpent_Then_ForceClosedEventInTheCloseSave()
    {
        // Arrange
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);

        // Act
        await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert: one event, in the save that recorded the close
        var (closed, save) = Assert.Single(_store.AccountingEvents);
        Assert.Contains("close", _store.Saves[save]);
        Assert.Contains("channel OnchainResolving", _store.Saves[save]);
        Assert.Equal(AccountingEventKind.ChannelForceClosed, closed.Kind);
        Assert.Equal(AccountingEventKeys.ChannelForceClosed(_channel.ChannelId, spend.TxId), closed.EventKey);
        Assert.Equal(AccountingFinality.Confirmed, closed.Finality);
        Assert.Equal(SpendHeight, closed.BlockHeight);
        Assert.Equal(spend.TxId, closed.TxId);
        Assert.Equal(_channel.ChannelId, closed.ChannelId);
        Assert.Equal(_channel.RemoteNodeId, closed.Counterparty);
        Assert.Equal(s_now, closed.OccurredAt);
        Assert.Equal(OnchainAccounting.ChannelBucket, closed.Details[OnchainAccounting.BucketFromKey]);
        Assert.Equal(OnchainAccounting.PendingBucket, closed.Details[OnchainAccounting.BucketToKey]);
        Assert.Equal(nameof(ChannelCloseKind.LocalCommitment), closed.Details[OnchainAccounting.CloseKindKey]);

        // Our balance per the commitment (net balance plus our offered HTLC) leaves the channel
        var balance = OurBalanceMsat(local.Spec);
        Assert.Equal(-balance, closed.AmountMsat);

        // to_local and our offered HTLC are pending; the peer's HTLC is not ours
        var counted = _store.Outputs.Values.Where(o => o.Descriptor is OutputDescriptorKind.DelayedToLocal
                                                                       or OutputDescriptorKind.LocalOfferedHtlc)
                            .ToList();
        Assert.Equal(2, counted.Count);
        var pending = counted.Sum(ValueMsat);
        Assert.Equal(pending, Msat(closed, OnchainAccounting.PendingKey));
        Assert.Equal(string.Join(",", counted.Select(o => o.OutputIndex).Order()),
                     closed.Details[OnchainAccounting.CountedVoutsKey]);
        Assert.Equal(0, Msat(closed, OnchainAccounting.TrimmedHtlcKey));

        // B = pending + fee + lost; the funder's balance paid the commitment fee
        Assert.Equal(balance, pending + closed.FeeMsat + Msat(closed, OnchainAccounting.LostKey));
        var outputsSat = Transaction.Load(spend.RawTxBytes, Network.RegTest).Outputs.Sum(o => o.Value.Satoshi);
        var commitmentFeeSat = (long)(_channel.FundingOutput!.Amount.MilliSatoshi / 1_000) - outputsSat;
        Assert.Equal(commitmentFeeSat, Msat(closed, OnchainAccounting.CommitmentFeeKey));
        if (_channel.IsInitiator)
        {
            Assert.True(closed.FeeMsat > 0);
            Assert.True(Math.Abs(closed.FeeMsat - commitmentFeeSat * 1_000) < 2_000,
                        $"fee {closed.FeeMsat} vs commitment fee {commitmentFeeSat} sat");
        }
        else
        {
            Assert.Equal(0, closed.FeeMsat);
        }
    }

    [Fact]
    public async Task Given_PeersCommitment_When_FundingSpent_Then_ToRemoteAndOurOfferedHtlcArePending()
    {
        // Arrange
        var remote = _pair.Alice.State.RemoteCommit;
        var spend = BuildCommitment(CommitmentSide.Remote, remote.Spec, remote.Number, remote.PerCommitmentPoint);

        // Act
        await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert: our balance on the peer's commitment; to_remote and our offered HTLC (a received HTLC there) pending
        var closed = Assert.Single(_store.Events);
        Assert.Equal(-OurBalanceMsat(remote.Spec), closed.AmountMsat);
        var counted = _store.Outputs.Values.Where(o => o.Descriptor is OutputDescriptorKind.PaymentToRemote
                                                                       or OutputDescriptorKind.RemoteReceivedHtlc)
                            .ToList();
        Assert.Equal(2, counted.Count);
        Assert.Equal(counted.Sum(ValueMsat), Msat(closed, OnchainAccounting.PendingKey));
        Assert.Equal(nameof(ChannelCloseKind.RemoteCommitment), closed.Details[OnchainAccounting.CloseKindKey]);
    }

    [Fact]
    public async Task Given_SpendAlreadyRecorded_When_ReplayedAndConfirmedElsewhere_Then_NoSecondCloseEvent()
    {
        // Arrange
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);
        var moved = new OutpointSpentEventArgs(_channel.ChannelId, spend, SpendHeight + 1, 1,
                                               _channel.FundingOutput!.TransactionId!.Value,
                                               _channel.FundingOutput.Index!.Value, OnchainTestStore.BlockHash(7));

        // Act: the same block replayed, then the same transaction in another block (a reorg)
        await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);
        await Watcher.HandleFundingSpentAsync(moved, TestContext.Current.CancellationToken);

        // Assert: still one fact
        Assert.Single(_store.Events);
    }

    [Fact]
    public async Task Given_OurCommitmentResolved_When_EveryOutputIsSpent_Then_ThePendingBucketNetsToZero()
    {
        // Arrange: our commitment confirmed
        var local = _pair.Alice.State.LocalCommit;
        var commitment = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        await Watcher.HandleFundingSpentAsync(SpentBy(commitment), TestContext.Current.CancellationToken);
        var toLocal = Row(OutputDescriptorKind.DelayedToLocal);
        var offered = Row(OutputDescriptorKind.LocalOfferedHtlc);
        var received = Row(OutputDescriptorKind.LocalReceivedHtlc);
        var toLocalSat = Sat(toLocal);
        var executor = CreateExecutor();

        // Act: our to_local sweep (500 sat fee)
        var sweep = Stored(Spend([(commitment.TxId, toLocal.OutputIndex)], toLocalSat - 500), BroadcastPurpose.Sweep);
        await MineSpendAsync(executor, sweep, commitment.TxId, toLocal.OutputIndex, SpendHeight + 150);

        // Assert: pending -> wallet, the sweep's fee paid out of the output, in the save that resolved it
        var (swept, sweptSave) = _store.AccountingEvents[^1];
        Assert.Contains($"output {toLocal.OutputIndex} Resolved", _store.Saves[sweptSave]);
        Assert.Equal(AccountingEventKind.OutputResolved, swept.Kind);
        Assert.Equal(AccountingEventKeys.OutputResolved(commitment.TxId, toLocal.OutputIndex), swept.EventKey);
        Assert.Equal(((long)toLocalSat - 500) * 1_000, swept.AmountMsat);
        Assert.Equal(500_000, swept.FeeMsat);
        Assert.Equal(SpendHeight + 150, swept.BlockHeight);
        Assert.Equal(AccountingFinality.Confirmed, swept.Finality);
        Assert.Equal(OnchainAccounting.WalletBucket, swept.Details[OnchainAccounting.BucketToKey]);
        Assert.Equal("us", swept.Details[OnchainAccounting.ResolvedByKey]);
        Assert.Equal(sweep.TxId.ToString(), swept.Details[OnchainAccounting.SpenderTxIdKey]);

        // Act: our HTLC-timeout (300 sat fee); the resolver records its second-level output
        var htlcTimeout = Stored(Spend([(commitment.TxId, offered.OutputIndex)], OurHtlcSat - 300),
                                 BroadcastPurpose.HtlcTransaction);
        _resolver.OnSpent = (_, row, spender) => row.OutputIndex == offered.OutputIndex
                                                 && row.TransactionId == commitment.TxId
                                                     ?
                                                     [
                                                         new UpsertOutputAction(SecondLevelRow(spender, offered)),
                                                         new WatchOutpointAction(new WatchedOutpointModel(
                                                             spender.TxId, 0, _channel.ChannelId,
                                                             WatchedOutpointPurpose.ResolutionOutput))
                                                     ]
                                                     : [];
        await MineSpendAsync(executor, htlcTimeout, commitment.TxId, offered.OutputIndex, SpendHeight + 160);

        // Assert: pending -> pending (the second-level output), its fee paid out of the HTLC (rule (c): an offered HTLC
        // back to us is an ordinary movement)
        var timedOut = _store.Events[^1];
        Assert.Equal(OnchainAccounting.OfferedHtlc, timedOut.Details[OnchainAccounting.HtlcDirectionKey]);
        Assert.False(timedOut.Details.ContainsKey(OnchainAccounting.ValueBookedByKey));
        Assert.Equal(0, timedOut.AmountMsat);
        Assert.Equal(300_000, timedOut.FeeMsat);
        Assert.Equal((long)OurHtlcSat * 1_000, Msat(timedOut, OnchainAccounting.PendingOutKey));
        Assert.Equal(((long)OurHtlcSat - 300) * 1_000, Msat(timedOut, OnchainAccounting.PendingInKey));
        Assert.Equal(RealSigningCommitmentPair.Hash(RealSigningCommitmentPair.Preimage(1)), timedOut.PaymentHash);

        // Act: the second-level output swept (200 sat fee), and the peer times its own HTLC out
        var secondSweep = Stored(Spend([(htlcTimeout.TxId, 0)], OurHtlcSat - 500), BroadcastPurpose.Sweep);
        await MineSpendAsync(executor, secondSweep, htlcTimeout.TxId, 0, SpendHeight + 310);
        var theirTimeout = Spend([(commitment.TxId, received.OutputIndex)], TheirHtlcSat - 300);
        await MineSpendAsync(executor, theirTimeout, commitment.TxId, received.OutputIndex, SpendHeight + 320);

        // Assert: the second level reached the wallet; the peer's HTLC was never ours (nothing lost)
        var secondLevel = _store.Events.Single(e => e.TxId == htlcTimeout.TxId);
        Assert.Equal(((long)OurHtlcSat - 500) * 1_000, secondLevel.AmountMsat);
        Assert.Equal(200_000, secondLevel.FeeMsat);
        var theirs = _store.Events.Single(e => e.OutputIndex == received.OutputIndex
                                            && e.TxId == commitment.TxId);
        Assert.Equal(0, theirs.AmountMsat);
        Assert.Equal("peer", theirs.Details[OnchainAccounting.ResolvedByKey]);
        Assert.Equal("false", theirs.Details[OnchainAccounting.CountedKey]);
        Assert.Equal(OnchainAccounting.IncomingHtlc, theirs.Details[OnchainAccounting.HtlcDirectionKey]);
        Assert.Equal((long)TheirHtlcSat * 1_000, Msat(theirs, OnchainAccounting.ValueKey));

        // Assert: one event per output, and the close's pending funds all reached the wallet or paid fees
        Assert.Equal(5, _store.Events.Count);
        Assert.Equal(0, PendingBalance());
        var close = _store.Events[0];
        Assert.Equal(Msat(close, OnchainAccounting.PendingKey),
                     _store.Events.Skip(1).Sum(e => e.AmountMsat + e.FeeMsat));

        // Act: every spend replayed (a block processed again)
        await MineSpendAsync(executor, sweep, commitment.TxId, toLocal.OutputIndex, SpendHeight + 150);
        await MineSpendAsync(executor, secondSweep, htlcTimeout.TxId, 0, SpendHeight + 310);
        await MineSpendAsync(executor, theirTimeout, commitment.TxId, received.OutputIndex, SpendHeight + 320);

        // Assert: nothing new
        Assert.Equal(5, _store.Events.Count);

        // Assert (NL-602 A2, the books): pending nets to zero, the channels lost B, the clearing account holds what the
        // two sweeps paid to the wallet and the sweep fees are the three spenders' fees
        var books = BooksSimulator.Of(_store.Events);
        Assert.Equal(0, books[AccountRole.Pending]);
        Assert.Equal(close.AmountMsat, books[AccountRole.Channels]);
        Assert.Equal(((long)toLocalSat - 500 + (long)OurHtlcSat - 500) * 1_000, books[AccountRole.Clearing]);
        Assert.Equal(1_000_000, books[AccountRole.FeeSweep]);
        Assert.Equal(close.FeeMsat, books[AccountRole.FeeCommitment]);
        Assert.Equal(0, books[AccountRole.OnchainGain]);
    }

    [Fact]
    public async Task Given_ACloseFromBeforeTheFeed_When_ResolvedAfterTheCutover_Then_ThePendingOpeningBalanceNetsToZero()
    {
        // Arrange: our commitment and our HTLC-timeout confirmed before the feed existed (no event of them is kept)
        var local = _pair.Alice.State.LocalCommit;
        var commitment = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        await Watcher.HandleFundingSpentAsync(SpentBy(commitment), TestContext.Current.CancellationToken);
        var toLocal = Row(OutputDescriptorKind.DelayedToLocal);
        var offered = Row(OutputDescriptorKind.LocalOfferedHtlc);
        var received = Row(OutputDescriptorKind.LocalReceivedHtlc);
        var executor = CreateExecutor();
        var htlcTimeout = Stored(Spend([(commitment.TxId, offered.OutputIndex)], OurHtlcSat - 300),
                                 BroadcastPurpose.HtlcTransaction);
        _resolver.OnSpent = (_, row, spender) => row.OutputIndex == offered.OutputIndex
                                                 && row.TransactionId == commitment.TxId
                                                     ?
                                                     [
                                                         new UpsertOutputAction(SecondLevelRow(spender, offered)),
                                                         new WatchOutpointAction(new WatchedOutpointModel(
                                                             spender.TxId, 0, _channel.ChannelId,
                                                             WatchedOutpointPurpose.ResolutionOutput))
                                                     ]
                                                     : [];
        await MineSpendAsync(executor, htlcTimeout, commitment.TxId, offered.OutputIndex, SpendHeight + 160);
        _store.AccountingEvents.Clear();

        // Act: the backfill's cutover (NL-602 A1-T6) from the stored close and rows
        var cutover = AccountingCutoverEvents.ResolvingChannel(_channel, _store.Closes[_channel.ChannelId],
                                                               _store.Outputs.Values.ToList(), s_now,
                                                               SpendHeight + 170);
        _store.AccountingEvents.Add((cutover.Opening, _store.Saves.Count - 1));
        _store.AccountingEvents.Add((cutover.Close, _store.Saves.Count - 1));

        // Assert: pending = to_local + our second-level output; only to_local is a counted vout of the commitment
        var secondLevelMsat = ((long)OurHtlcSat - 300) * 1_000;
        Assert.Equal(ValueMsat(toLocal) + secondLevelMsat, cutover.PendingMsat);
        Assert.Equal(cutover.PendingMsat, cutover.Opening.AmountMsat);
        Assert.Equal(toLocal.OutputIndex.ToString(CultureInfo.InvariantCulture),
                     cutover.Close.Details[OnchainAccounting.CountedVoutsKey]);
        Assert.Equal(AccountingEventKeys.ChannelForceClosed(_channel.ChannelId, commitment.TxId),
                     cutover.Close.EventKey);

        // Act: every output left is resolved after the cutover
        var toLocalSat = Sat(toLocal);
        var sweep = Stored(Spend([(commitment.TxId, toLocal.OutputIndex)], toLocalSat - 500), BroadcastPurpose.Sweep);
        await MineSpendAsync(executor, sweep, commitment.TxId, toLocal.OutputIndex, SpendHeight + 180);
        var secondSweep = Stored(Spend([(htlcTimeout.TxId, 0)], OurHtlcSat - 500), BroadcastPurpose.Sweep);
        await MineSpendAsync(executor, secondSweep, htlcTimeout.TxId, 0, SpendHeight + 310);
        var theirTimeout = Spend([(commitment.TxId, received.OutputIndex)], TheirHtlcSat - 300);
        await MineSpendAsync(executor, theirTimeout, commitment.TxId, received.OutputIndex, SpendHeight + 320);

        // Assert: three resolutions, the counted ones taken out of the pending bucket the opening balance filled
        var resolutions = _store.Events.Where(e => e.Kind == AccountingEventKind.OutputResolved).ToList();
        Assert.Equal(3, resolutions.Count);
        Assert.All(resolutions.Where(e => e.TxId != commitment.TxId || e.OutputIndex != received.OutputIndex),
                   e => Assert.Equal("true", e.Details[OnchainAccounting.CountedKey]));
        var flows = resolutions.Sum(e => Msat(e, OnchainAccounting.PendingInKey)
                                       - Msat(e, OnchainAccounting.PendingOutKey));
        Assert.Equal(0, cutover.Opening.AmountMsat + flows);

        // What left the bucket reached the wallet or paid fees
        Assert.Equal(cutover.Opening.AmountMsat, resolutions.Sum(e => e.AmountMsat + e.FeeMsat));
    }

    [Fact]
    public async Task Given_PeersCommitmentResolved_When_TheyClaimOurHtlcAndWeClaimTheirs_Then_LossGainAndZeroPending()
    {
        // Arrange: the peer's commitment confirmed
        var remote = _pair.Alice.State.RemoteCommit;
        var commitment = BuildCommitment(CommitmentSide.Remote, remote.Spec, remote.Number,
                                         remote.PerCommitmentPoint);
        await Watcher.HandleFundingSpentAsync(SpentBy(commitment), TestContext.Current.CancellationToken);
        var toRemote = Row(OutputDescriptorKind.PaymentToRemote);
        var ourHtlc = Row(OutputDescriptorKind.RemoteReceivedHtlc);
        var theirHtlc = Row(OutputDescriptorKind.RemoteOfferedHtlc);
        _store.Origins[(_channel.ChannelId, HtlcDirection.Outgoing, ourHtlc.HtlcId!.Value)] =
            HtlcOrigin.Local(RealSigningCommitmentPair.Hash(RealSigningCommitmentPair.Preimage(1)));
        var executor = CreateExecutor();

        // Act: our to_remote swept (by a replacement of an earlier sweep), the peer claims our HTLC with the
        // preimage, we claim theirs with ours
        var sweep = Stored(Spend([(commitment.TxId, toRemote.OutputIndex)], Sat(toRemote) - 400),
                           BroadcastPurpose.Sweep, new TxId(Enumerable.Repeat((byte)0x5E, 32).ToArray()));
        await MineSpendAsync(executor, sweep, commitment.TxId, toRemote.OutputIndex, SpendHeight + 1);
        var theirClaim = Spend([(commitment.TxId, ourHtlc.OutputIndex)], OurHtlcSat - 300,
                               HtlcSuccessWitness(RealSigningCommitmentPair.Preimage(1)));
        await MineSpendAsync(executor, theirClaim, commitment.TxId, ourHtlc.OutputIndex, SpendHeight + 2);
        var ourClaim = Stored(Spend([(commitment.TxId, theirHtlc.OutputIndex)], TheirHtlcSat - 400),
                              BroadcastPurpose.HtlcClaim);
        await MineSpendAsync(executor, ourClaim, commitment.TxId, theirHtlc.OutputIndex, SpendHeight + 3);

        // Assert: the swept to_remote's fee is the replacement's, marked for the books (the bump is booked apart)
        var swept = _store.Events.Single(e => e.OutputIndex == toRemote.OutputIndex && e.TxId == commitment.TxId);
        Assert.Equal(400_000, swept.FeeMsat);
        Assert.Equal("true", swept.Details[OnchainAccounting.IncludesFeeBumpKey]);

        // Assert: rule (b): our offered HTLC is written off the pending bucket, its value booked by the payment
        var lost = _store.Events.Single(e => e.OutputIndex == ourHtlc.OutputIndex);
        Assert.Equal(-(long)OurHtlcSat * 1_000, lost.AmountMsat);
        Assert.Equal(0, lost.FeeMsat);
        Assert.Equal("peer", lost.Details[OnchainAccounting.ResolvedByKey]);
        Assert.Equal(OnchainAccounting.OfferedHtlc, lost.Details[OnchainAccounting.HtlcDirectionKey]);
        Assert.Equal("peer", lost.Details[OnchainAccounting.ClaimedByKey]);
        Assert.Equal("payment", lost.Details[OnchainAccounting.ValueBookedByKey]);
        Assert.Equal(AccountingDetailKeys.ClaimPathPreimage, lost.Details[AccountingDetailKeys.ClaimPath]);
        Assert.Equal(OnchainAccounting.PendingBucket, lost.Details[OnchainAccounting.BucketKey]);

        // Assert: rule (a): their HTLC we claimed moves into the wallet, its value booked by our invoice
        var gained = _store.Events.Single(e => e.OutputIndex == theirHtlc.OutputIndex);
        Assert.Equal(OnchainAccounting.IncomingHtlc, gained.Details[OnchainAccounting.HtlcDirectionKey]);
        Assert.Equal("invoice", gained.Details[OnchainAccounting.ValueBookedByKey]);
        Assert.Equal(((long)TheirHtlcSat - 400) * 1_000, gained.AmountMsat);
        Assert.Equal(400_000, gained.FeeMsat);
        Assert.False(gained.Details.ContainsKey(OnchainAccounting.BucketFromKey));
        Assert.False(gained.Details.ContainsKey(OnchainAccounting.IncludesFeeBumpKey));
        Assert.Equal(OnchainAccounting.WalletBucket, gained.Details[OnchainAccounting.BucketToKey]);
        Assert.Equal(0, PendingBalance());

        // Assert (NL-602 A2, the books): with the off-chain events that own the two HTLCs' value (our payment that the
        // peer's claim completed, our invoice that our claim settled), pending nets to zero and the channels lost B
        var close = _store.Events[0];
        var books = BooksSimulator.Of([.. _store.Events, .. OffChainEventsOfTheHtlcs(close.OccurredAt)]);
        Assert.Equal(0, books[AccountRole.Pending]);
        Assert.Equal(close.AmountMsat, books[AccountRole.Channels]);
        Assert.Equal((long)OurHtlcSat * 1_000, books[AccountRole.Sent]);
        Assert.Equal(-(long)TheirHtlcSat * 1_000, books[AccountRole.Received]);
        Assert.Equal(0, books[AccountRole.OnchainGain]);
        Assert.Equal(Math.Max(0, Msat(close, OnchainAccounting.LostKey)), books[AccountRole.LossOnchain]);
    }

    [Fact]
    public async Task Given_OurRevokedCommitment_When_ThePeerTakesOurHtlcByRevocation_Then_ALossNotTheValueOfAPayment()
    {
        // Arrange (NL-612): our own commitment confirmed and the peer spends our offered HTLC output through the
        // revocation path (we had broadcast a revoked state); our payment never completed (no preimage)
        var local = _pair.Alice.State.LocalCommit;
        var commitment = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        await Watcher.HandleFundingSpentAsync(SpentBy(commitment), TestContext.Current.CancellationToken);
        var ourHtlc = Row(OutputDescriptorKind.LocalOfferedHtlc);
        _store.Origins[(_channel.ChannelId, HtlcDirection.Outgoing, ourHtlc.HtlcId!.Value)] =
            HtlcOrigin.Local(RealSigningCommitmentPair.Hash(RealSigningCommitmentPair.Preimage(1)));
        var executor = CreateExecutor();

        // Act
        var taken = Spend([(commitment.TxId, ourHtlc.OutputIndex)], OurHtlcSat - 300, RevocationWitness());
        await MineSpendAsync(executor, taken, commitment.TxId, ourHtlc.OutputIndex, SpendHeight + 2);

        // Assert: written off the pending bucket as the peer's, with the path, and no payment owns its value
        var lost = _store.Events.Single(e => e.OutputIndex == ourHtlc.OutputIndex && e.TxId == commitment.TxId);
        Assert.Equal(-(long)OurHtlcSat * 1_000, lost.AmountMsat);
        Assert.Equal("peer", lost.Details[OnchainAccounting.ClaimedByKey]);
        Assert.Equal(AccountingDetailKeys.ClaimPathRevocation, lost.Details[AccountingDetailKeys.ClaimPath]);
        Assert.False(lost.Details.ContainsKey(OnchainAccounting.ValueBookedByKey));

        // Assert (the books): the HTLC's value is a loss, not taken out of the channels a second time
        var books = BooksSimulator.Of([lost]);
        Assert.Equal(-(long)OurHtlcSat * 1_000, books[AccountRole.Pending]);
        Assert.Equal((long)OurHtlcSat * 1_000, books[AccountRole.LossOnchain]);
        Assert.Equal(0, books[AccountRole.Channels]);
    }

    [Fact]
    public async Task Given_RevokedCommitment_When_PenalizedAndTheCheaterTakesOurHtlc_Then_PenaltyBreachAndZeroPending()
    {
        // Arrange: Bob's commitment with both HTLCs, revoked by the next rounds (in which we pay Bob 5,000 sat, so our
        // latest balance is not the revoked state's, NL-616), confirmed
        var revoked = _pair.Alice.State.RemoteCommit;
        var paid = _pair.Add(_pair.Alice, 5_000_000, RealSigningCommitmentPair.Preimage(3));
        _pair.Settle(_pair.Alice);
        _pair.Fulfill(_pair.Bob, paid, RealSigningCommitmentPair.Preimage(3));
        _pair.Settle(_pair.Bob);
        _channel.UpdateCommitments(_pair.Alice.State);
        _store.RevocationLog[(_channel.ChannelId, revoked.Number)] =
            RevokedCommitmentModel.From(_channel.ChannelId, revoked);
        var secret = _pair.Bob.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, revoked.Number);
        var shachain = new Mock<ISecretStorageService>();
        shachain.Setup(s => s.DeriveOldSecret(It.IsAny<ulong>())).Returns(secret);
        _shachainFactory.Setup(f => f.CreatePerCommitmentStorage()).Returns(shachain.Object);
        var commitment = BuildCommitment(CommitmentSide.Remote, revoked.Spec, revoked.Number,
                                         revoked.PerCommitmentPoint);
        await Watcher.HandleFundingSpentAsync(SpentBy(commitment), TestContext.Current.CancellationToken);

        // Assert (NL-616): the close takes our latest balance out of the channels (what the books hold), not the
        // revoked state's, which the details keep
        var closed = Assert.Single(_store.Events);
        var latestBalance = OurBalanceMsat(_pair.Alice.State.LocalCommit.Spec);
        Assert.NotEqual(OurBalanceMsat(revoked.Spec), latestBalance);
        Assert.Equal(-latestBalance, closed.AmountMsat);
        Assert.Equal(OnchainAccounting.BalanceFromLatestLocal, closed.Details[OnchainAccounting.BalanceSourceKey]);
        Assert.Equal(OurBalanceMsat(revoked.Spec).ToString(CultureInfo.InvariantCulture),
                     closed.Details[OnchainAccounting.RevokedStateBalanceKey]);
        var toRemote = Row(OutputDescriptorKind.PaymentToRemote);
        var toLocal = Row(OutputDescriptorKind.RevokedToLocal);
        var ourHtlc = _store.Outputs.Values.Single(o => o is
        {
            Descriptor: OutputDescriptorKind.RevokedHtlc, HtlcDirection: HtlcDirection.Outgoing
        });
        var theirHtlc = _store.Outputs.Values.Single(o => o is
        {
            Descriptor: OutputDescriptorKind.RevokedHtlc, HtlcDirection: HtlcDirection.Incoming
        });
        Assert.Equal(string.Join(",", new[] { toRemote.OutputIndex, ourHtlc.OutputIndex }.Order()),
                     closed.Details[OnchainAccounting.CountedVoutsKey]);
        var executor = CreateExecutor();

        // Act: one penalty takes the cheater's to_local and HTLC (1,001 sat fee), the cheater's HTLC-success takes
        // our HTLC, and our to_remote is swept
        var penaltyInputsSat = Sat(toLocal) + Sat(theirHtlc);
        var penalty = Stored(Spend([(commitment.TxId, toLocal.OutputIndex), (commitment.TxId, theirHtlc.OutputIndex)],
                                   penaltyInputsSat - 1_001), BroadcastPurpose.Penalty);
        await MineSpendAsync(executor, penalty, commitment.TxId, toLocal.OutputIndex, SpendHeight + 1);
        await MineSpendAsync(executor, penalty, commitment.TxId, theirHtlc.OutputIndex, SpendHeight + 1);
        var cheaterSuccess = Spend([(commitment.TxId, ourHtlc.OutputIndex)], OurHtlcSat - 700);
        await MineSpendAsync(executor, cheaterSuccess, commitment.TxId, ourHtlc.OutputIndex, SpendHeight + 2);
        var sweep = Stored(Spend([(commitment.TxId, toRemote.OutputIndex)], Sat(toRemote) - 300),
                           BroadcastPurpose.Sweep);
        await MineSpendAsync(executor, sweep, commitment.TxId, toRemote.OutputIndex, SpendHeight + 3);

        // Assert: two penalties sharing the penalty's output and fee, the breach loss of our HTLC
        var penalties = _store.Events.Where(e => e.Kind == AccountingEventKind.PenaltyClaimed).ToList();
        Assert.Equal(2, penalties.Count);
        Assert.Equal(((long)penaltyInputsSat - 1_001) * 1_000, penalties.Sum(e => e.AmountMsat));
        Assert.Equal(1_001_000, penalties.Sum(e => e.FeeMsat));
        Assert.All(penalties, e => Assert.True(e.AmountMsat > 0));
        Assert.Contains(penalties, e => e.EventKey == AccountingEventKeys.PenaltyClaimed(commitment.TxId,
                                                                                            toLocal.OutputIndex));
        var breach = Assert.Single(_store.Events, e => e.Kind == AccountingEventKind.BreachLoss);
        Assert.Equal(AccountingEventKeys.BreachLoss(commitment.TxId, ourHtlc.OutputIndex), breach.EventKey);
        Assert.Equal(-(long)OurHtlcSat * 1_000, breach.AmountMsat);
        Assert.Equal("peer", breach.Details[OnchainAccounting.ClaimedByKey]);
        Assert.Equal(0, PendingBalance());

        // Assert (NL-602 A2, the books): pending nets to zero and the channels lost B; the penalties are gains, not the
        // income of an invoice (a revoked HTLC output is taken through the revocation path, no preimage), and our HTLC
        // the cheater took is a loss (no payment owns it here)
        Assert.DoesNotContain(penalties, e => e.Details.ContainsKey(OnchainAccounting.ValueBookedByKey));
        var books = BooksSimulator.Of(_store.Events);
        Assert.Equal(0, books[AccountRole.Pending]);
        Assert.Equal(closed.AmountMsat, books[AccountRole.Channels]);
        var lost = Msat(closed, OnchainAccounting.LostKey);
        Assert.Equal(-(long)penaltyInputsSat * 1_000 + Math.Min(0, lost), books[AccountRole.OnchainGain]);
        Assert.Equal((long)OurHtlcSat * 1_000 + Math.Max(0, lost), books[AccountRole.LossOnchain]);
    }

    [Fact]
    public async Task Given_AResolvedSpendReorgedOut_When_TheNextRound_Then_ReversedAndRecordedAgainWhenItReconfirms()
    {
        // Arrange: our to_local sweep resolved the output at SpendHeight + 150
        var local = _pair.Alice.State.LocalCommit;
        var commitment = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        await Watcher.HandleFundingSpentAsync(SpentBy(commitment), TestContext.Current.CancellationToken);
        var toLocal = Row(OutputDescriptorKind.DelayedToLocal);
        var executor = CreateExecutor();
        var sweep = Stored(Spend([(commitment.TxId, toLocal.OutputIndex)], Sat(toLocal) - 500), BroadcastPurpose.Sweep);
        await MineSpendAsync(executor, sweep, commitment.TxId, toLocal.OutputIndex, SpendHeight + 150);
        var resolved = _store.Events[^1];

        // Act: the chain monitor rolled the spend back (reorg), then the next block's round
        _store.Watches[(commitment.TxId, toLocal.OutputIndex)].ClearSpend();
        await executor.RunRoundAsync(SpendHeight + 151, TestContext.Current.CancellationToken);

        // Assert: the resolution is negated in the save that unresolved the row
        var (reversal, save) = _store.AccountingEvents[^1];
        Assert.Contains($"output {toLocal.OutputIndex} Pending", _store.Saves[save]);
        Assert.Equal(AccountingEventKind.Reversal, reversal.Kind);
        Assert.Equal(AccountingEventKeys.Reversal(resolved.EventKey, SpendHeight + 150), reversal.EventKey);
        Assert.Equal(-resolved.AmountMsat, reversal.AmountMsat);
        Assert.Equal(-resolved.FeeMsat, reversal.FeeMsat);
        Assert.Equal(resolved.EventKey, reversal.Details[OnchainAccounting.ReversesKey]);
        Assert.Equal(nameof(AccountingEventKind.OutputResolved), reversal.Details[OnchainAccounting.OriginalKindKey]);
        Assert.Equal(Msat(_store.Events[0], OnchainAccounting.PendingKey), PendingBalance());

        // Act: another round changes nothing; then the sweep confirms again one block higher
        await executor.RunRoundAsync(SpendHeight + 152, TestContext.Current.CancellationToken);
        var count = _store.Events.Count;
        await MineSpendAsync(executor, sweep, commitment.TxId, toLocal.OutputIndex, SpendHeight + 153);

        // Assert: recorded again under its next confirmation key (NL-613, the wallet writers' scheme)
        Assert.Equal(count + 1, _store.Events.Count);
        var again = _store.Events[^1];
        Assert.Equal(AccountingEventKeys.Reconfirmed(resolved.EventKey, 2), again.EventKey);
        Assert.Equal(resolved.AmountMsat, again.AmountMsat);
        Assert.Equal(Msat(_store.Events[0], OnchainAccounting.PendingKey) - ValueMsat(toLocal), PendingBalance());

        // Assert (NL-602 A2, the books): the reversal undid the first sweep's postings exactly and the second
        // confirmation booked them once more
        var books = BooksSimulator.Of(_store.Events);
        Assert.Equal(PendingBalance(), books[AccountRole.Pending]);
        Assert.Equal(_store.Events[0].AmountMsat, books[AccountRole.Channels]);
        Assert.Equal(resolved.AmountMsat, books[AccountRole.Clearing]);
        Assert.Equal(resolved.FeeMsat, books[AccountRole.FeeSweep]);
        var reversalEntry = books.Entry(reversal.EventKey)!;
        Assert.Equal(books.Entry(resolved.EventKey)!.Postings.Select(p => (p.Account, -p.AmountMsat)),
                     reversalEntry.Postings.Select(p => (p.Account, p.AmountMsat)));
    }

    [Fact]
    public void Given_OurStoredAnchorSweepOfBothAnchors_When_Booked_Then_ThePeersAnchorIsAGainAndClearingNets()
    {
        // Arrange (NL-611): the anchor sweep of a commitment we fund spends our anchor (330 sat, a counted row) and the
        // peer's (330 sat, no row) into one 500 sat wallet output; it is a stored Sweep row with its 160 sat fee
        var commitmentTxId = new TxId(Enumerable.Repeat((byte)0xc1, 32).ToArray());
        var data = new OutputDescriptorData(330, new byte[34], null, 16, true, null, null);
        var ourAnchor = new OutputResolutionModel
        {
            TransactionId = commitmentTxId,
            OutputIndex = 1,
            ChannelId = _channel.ChannelId,
            Descriptor = OutputDescriptorKind.OurAnchor,
            DescriptorData = data.Encode()
        };
        var rows = new Dictionary<(TxId, uint), OutputResolutionModel> { [(commitmentTxId, 1)] = ourAnchor };
        var sweep = new ChainTx(new TxId(Enumerable.Repeat((byte)0xc2, 32).ToArray()), 2, 0,
                                [
                                    new ChainTxInput(commitmentTxId, 0, 16, []),
                                    new ChainTxInput(commitmentTxId, 1, 16, [])
                                ],
                                [new ChainTxOutput(500, new byte[22])]);
        var close = new ChannelCloseModel(_channel.ChannelId, ChannelCloseKind.RemoteCommitment, commitmentTxId, 7,
                                          SpendHeight, OnchainTestStore.BlockHash(1), s_now);

        // Act
        var flows = OnchainAccounting.Ours(ourAnchor, 330_000, true, sweep, rows, false, 160_000);
        var unstored = OnchainAccounting.Ours(ourAnchor, 330_000, true, sweep, rows, false);
        var resolved = OnchainAccounting.Resolution(_channel, close, ourAnchor, data, AccountingEventKind.OutputResolved,
                                                    AccountingEventKeys.OutputResolved(commitmentTxId, 1), flows, true,
                                                    sweep.TxId, SpendHeight + 16, s_now);
        var received = new AccountingEventModel
        {
            EventKey = AccountingEventKeys.WalletReceived(sweep.TxId, 0),
            Kind = AccountingEventKind.WalletReceived,
            OccurredAt = s_now,
            BlockHeight = SpendHeight + 16,
            TxId = sweep.TxId,
            OutputIndex = 0,
            AmountMsat = 500_000,
            Finality = AccountingFinality.Confirmed,
            Details = new Dictionary<string, string>
            {
                [AccountingDetailKeys.Source] = AccountingDetailKeys.BroadcastSource,
                [AccountingDetailKeys.Purpose] = nameof(BroadcastPurpose.Sweep)
            }
        };

        // Assert: resolved by us, the whole wallet output and fee on our anchor's event, the peer's anchor a gain
        Assert.Equal(AccountingDetailKeys.ResolvedByUs, flows.ResolvedBy);
        Assert.Equal(AccountingDetailKeys.ExternalInputsNote, flows.Note);
        Assert.Equal(330_000, flows.PendingOutMsat);
        Assert.Equal(500_000, flows.WalletMsat);
        Assert.Equal(160_000, flows.FeeMsat);
        Assert.Equal(AccountingDetailKeys.MergedNote, unstored.Note);
        var books = BooksSimulator.Of([resolved, received]);
        Assert.Equal(-330_000, books[AccountRole.Pending]);
        Assert.Equal(0, books[AccountRole.Clearing]);
        Assert.Equal(500_000, books[AccountRole.Wallet]);
        Assert.Equal(160_000, books[AccountRole.FeeSweep]);
        Assert.Equal(-330_000, books[AccountRole.OnchainGain]);
        Assert.Equal(0, books[AccountRole.LossOnchain]);
    }

    [Fact]
    public async Task Given_AResolutionReorgedBackToTheSameHeightTwice_When_ItReconfirms_Then_EachConfirmationIsRecorded()
    {
        // Arrange (NL-613): our to_local sweep resolved the output at SpendHeight + 150
        var local = _pair.Alice.State.LocalCommit;
        var commitment = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        await Watcher.HandleFundingSpentAsync(SpentBy(commitment), TestContext.Current.CancellationToken);
        var toLocal = Row(OutputDescriptorKind.DelayedToLocal);
        var executor = CreateExecutor();
        var sweep = Stored(Spend([(commitment.TxId, toLocal.OutputIndex)], Sat(toLocal) - 500), BroadcastPurpose.Sweep);
        await MineSpendAsync(executor, sweep, commitment.TxId, toLocal.OutputIndex, SpendHeight + 150);
        var resolved = _store.Events[^1];

        // Act: twice, a reorg rolls the spend back and the sweep confirms again at the same height
        for (var round = 0; round < 2; round++)
        {
            _store.Watches[(commitment.TxId, toLocal.OutputIndex)].ClearSpend();
            await executor.RunRoundAsync(SpendHeight + 151, TestContext.Current.CancellationToken);
            await MineSpendAsync(executor, sweep, commitment.TxId, toLocal.OutputIndex, SpendHeight + 150);
        }

        // Assert: three confirmations under their generation keys, the first two reversed, the pending bucket as after
        // one sweep (a height key collided here: the third confirmation was never recorded)
        var confirmations = _store.Events.Where(e => e.Kind == AccountingEventKind.OutputResolved).ToList();
        Assert.Equal([resolved.EventKey, AccountingEventKeys.Reconfirmed(resolved.EventKey, 2),
                      AccountingEventKeys.Reconfirmed(resolved.EventKey, 3)],
                     confirmations.Select(e => e.EventKey));
        Assert.Equal(2, _store.Events.Count(e => e.Kind == AccountingEventKind.Reversal));
        Assert.Equal(Msat(_store.Events[0], OnchainAccounting.PendingKey) - ValueMsat(toLocal), PendingBalance());
        var books = BooksSimulator.Of(_store.Events);
        Assert.Equal(PendingBalance(), books[AccountRole.Pending]);
        Assert.Equal(resolved.AmountMsat, books[AccountRole.Clearing]);
        Assert.Equal(resolved.FeeMsat, books[AccountRole.FeeSweep]);
    }

    [Fact]
    public async Task Given_ACloseReplacedAfterAReorg_When_TheOtherCommitmentConfirms_Then_OldEventsReversedInTheNewCloseSave()
    {
        // Arrange: our commitment recorded and its to_local swept; a reorg took both out and the peer's commitment
        // confirmed instead
        var local = _pair.Alice.State.LocalCommit;
        var ours = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        await Watcher.HandleFundingSpentAsync(SpentBy(ours), TestContext.Current.CancellationToken);
        var toLocal = Row(OutputDescriptorKind.DelayedToLocal);
        var sweep = Stored(Spend([(ours.TxId, toLocal.OutputIndex)], Sat(toLocal) - 500), BroadcastPurpose.Sweep);
        await MineSpendAsync(CreateExecutor(), sweep, ours.TxId, toLocal.OutputIndex, SpendHeight + 150);
        var oldClose = _store.Events[0];
        var oldResolution = _store.Events[1];
        var remote = _pair.Alice.State.RemoteCommit;
        var theirs = BuildCommitment(CommitmentSide.Remote, remote.Spec, remote.Number, remote.PerCommitmentPoint);
        var reorged = new OutpointSpentEventArgs(_channel.ChannelId, theirs, SpendHeight + 2, 1,
                                                 _channel.FundingOutput!.TransactionId!.Value,
                                                 _channel.FundingOutput.Index!.Value, OnchainTestStore.BlockHash(9));

        // Act
        await Watcher.HandleFundingSpentAsync(reorged, TestContext.Current.CancellationToken);

        // Assert: the old resolution and the old close negated, and the new close, all in the new close's save
        var newSave = _store.AccountingEvents.Skip(2).Select(e => e.Save).Distinct().ToList();
        Assert.Single(newSave);
        Assert.Contains("close", _store.Saves[newSave[0]]);
        var added = _store.Events.Skip(2).ToList();
        Assert.Equal([AccountingEventKind.Reversal, AccountingEventKind.Reversal, AccountingEventKind.ChannelForceClosed],
                     added.Select(e => e.Kind));
        Assert.Equal(AccountingEventKeys.Reversal(oldResolution.EventKey, SpendHeight + 150), added[0].EventKey);
        Assert.Equal(-oldResolution.AmountMsat, added[0].AmountMsat);
        Assert.Equal(AccountingEventKeys.Reversal(oldClose.EventKey, SpendHeight), added[1].EventKey);
        Assert.Equal(-oldClose.AmountMsat, added[1].AmountMsat);
        Assert.Equal(oldClose.EventKey, added[1].Details[OnchainAccounting.ReversesKey]);
        Assert.Equal(AccountingEventKeys.ChannelForceClosed(_channel.ChannelId, theirs.TxId), added[2].EventKey);

        // The old close's pending bucket is back to zero; the new close's holds its own outputs
        Assert.Equal(0, PendingBalance(ours.TxId));
        Assert.Equal(Msat(added[2], OnchainAccounting.PendingKey), PendingBalance(theirs.TxId));

        // Assert (NL-602 A2, the books): only the new close stands
        var books = BooksSimulator.Of(_store.Events);
        Assert.Equal(Msat(added[2], OnchainAccounting.PendingKey), books[AccountRole.Pending]);
        Assert.Equal(added[2].AmountMsat, books[AccountRole.Channels]);
        Assert.Equal(0, books[AccountRole.Clearing]);
        Assert.Equal(0, books[AccountRole.FeeSweep]);
    }

    [Fact]
    public async Task Given_ACountedOutputGivenUp_When_TheRoundIgnoresIt_Then_ItsValueIsLostOnce()
    {
        // Arrange: our commitment recorded; the resolver gives to_local up (worth less than its sweep)
        var local = _pair.Alice.State.LocalCommit;
        var commitment = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        await Watcher.HandleFundingSpentAsync(SpentBy(commitment), TestContext.Current.CancellationToken);
        var toLocal = Row(OutputDescriptorKind.DelayedToLocal);
        _resolver.OnResolve = (_, rows) => rows.Where(r => r.OutputIndex == toLocal.OutputIndex
                                                        && r.State == OutputResolutionState.Pending)
                                               .Select(r => (OutputResolverAction)new UpsertOutputAction(r with
                                               {
                                                   State = OutputResolutionState.Ignored
                                               }))
                                               .ToList();
        var executor = CreateExecutor();

        // Act: two rounds
        await executor.RunRoundAsync(SpendHeight + 5, TestContext.Current.CancellationToken);
        await executor.RunRoundAsync(SpendHeight + 6, TestContext.Current.CancellationToken);

        // Assert: one loss, in the save that gave the output up
        var (ignored, save) = Assert.Single(_store.AccountingEvents.Skip(1));
        Assert.Contains($"output {toLocal.OutputIndex} Ignored", _store.Saves[save]);
        Assert.Equal(AccountingEventKeys.OutputIgnored(commitment.TxId, toLocal.OutputIndex), ignored.EventKey);
        Assert.Equal(-ValueMsat(toLocal), ignored.AmountMsat);
        Assert.Equal("ignored", ignored.Details[OnchainAccounting.ResolvedByKey]);

        // Assert (NL-602 A2, the books): the given-up output left the pending bucket as a loss
        var close = _store.Events[0];
        var books = BooksSimulator.Of(_store.Events);
        Assert.Equal(Msat(close, OnchainAccounting.PendingKey) - ValueMsat(toLocal), books[AccountRole.Pending]);
        Assert.Equal(ValueMsat(toLocal) + Math.Max(0, Msat(close, OnchainAccounting.LostKey)),
                     books[AccountRole.LossOnchain]);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    /// <summary>
    /// What the payment core writes for the two HTLCs of the shared arrangement (the real writers): our payment of
    /// <see cref="OurHtlcSat"/>, completed by the preimage the peer revealed, and our invoice of
    /// <see cref="TheirHtlcSat"/>, settled by our on-chain claim.
    /// </summary>
    private IReadOnlyList<AccountingEventModel> OffChainEventsOfTheHtlcs(DateTimeOffset at)
    {
        var ourPreimage = RealSigningCommitmentPair.Preimage(1);
        var payment = new PaymentModel(RealSigningCommitmentPair.Hash(ourPreimage), "lnbcrt-test",
                                       _channel.RemoteNodeId, LightningMoney.Satoshis(OurHtlcSat), LightningMoney.Zero,
                                       at);
        payment.AddOutgoingHtlc(_channel.ChannelId, 0);
        payment.Succeed(ourPreimage, at);

        var theirPreimage = RealSigningCommitmentPair.Preimage(2);
        var invoice = new InvoiceModel(RealSigningCommitmentPair.Hash(theirPreimage), theirPreimage,
                                       RealSigningCommitmentPair.Preimage(9), LightningMoney.Satoshis(TheirHtlcSat),
                                       "on chain", "lnbcrt-test", at, 3_600, 18);
        invoice.Accept(LightningMoney.Satoshis(TheirHtlcSat));
        invoice.Settle(at);

        return
        [
            PaymentAccountingEvents.PaymentSucceeded(payment, 1, false, null),
            PaymentAccountingEvents.InvoiceSettled(invoice, LightningMoney.Satoshis(TheirHtlcSat), _channel.ChannelId,
                                                   _channel, 1, true, SpendHeight + 3)
        ];
    }

    private OnchainResolutionExecutor CreateExecutor() =>
        new(_broadcaster.Object, _provider.GetRequiredService<IChannelLockProvider>(), _memory.Object,
            NullLogger<OnchainResolutionExecutor>.Instance, _outpointWatcher.Object,
            _provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new OnchainOptions()),
            new FixedClock(s_now));

    /// <summary>
    /// What the chain monitor does for a mined spend of a watched output: records it on the watch, then hands it to
    /// the executor.
    /// </summary>
    private async Task MineSpendAsync(OnchainResolutionExecutor executor, SignedTransaction spender, TxId spentTxId,
                                      uint spentVout, uint height)
    {
        var watch = _store.Watches[(spentTxId, spentVout)];
        if (!watch.IsSpent)
            watch.MarkSpent(spender.TxId, height, OnchainTestStore.BlockHash((byte)(height % 251)));
        await executor.HandleOutputSpentAsync(
            new OutpointSpentEventArgs(_channel.ChannelId, spender, height, 1, spentTxId, spentVout,
                                       OnchainTestStore.BlockHash((byte)(height % 251))),
            TestContext.Current.CancellationToken);
    }

    /// <summary>A transaction of ours, stored for broadcast as the resolvers store theirs.</summary>
    private SignedTransaction Stored(SignedTransaction transaction, BroadcastPurpose purpose, TxId? replaces = null)
    {
        _store.Broadcasts.Add(new BroadcastTransactionModel(transaction, purpose, _channel.ChannelId, SpendHeight,
                                                            replacesTransactionId: replaces));
        return transaction;
    }

    private static SignedTransaction Spend(IEnumerable<(TxId TxId, uint Vout)> inputs, ulong outputSat,
                                           byte[][]? firstInputWitness = null)
    {
        var transaction = Network.RegTest.CreateTransaction();
        foreach (var (txId, vout) in inputs)
            transaction.Inputs.Add(new OutPoint(new uint256(txId), vout));
        if (firstInputWitness is not null)
            transaction.Inputs[0].WitScript = new WitScript(firstInputWitness);
        transaction.Outputs.Add(Money.Satoshis(outputSat), new Key().PubKey.WitHash.ScriptPubKey);
        return new SignedTransaction(new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes());
    }

    /// <summary>The witness of the peer's HTLC-success transaction: <c>0 &lt;sig&gt; &lt;sig&gt; &lt;preimage&gt;
    /// &lt;script&gt;</c> (only its shape and preimage matter to the books).</summary>
    private static byte[][] HtlcSuccessWitness(Secret preimage) =>
        [[], FakeSignature(), FakeSignature(), (byte[])preimage, [0x51]];

    /// <summary>The witness of a revocation spend: <c>&lt;sig&gt; &lt;revocationpubkey&gt; &lt;script&gt;</c>.</summary>
    private static byte[][] RevocationWitness() => [FakeSignature(), [0x02, .. new byte[32]], [0x51]];

    private static byte[] FakeSignature() => [0x30, .. new byte[70]];

    /// <summary>The second-level row the local resolver writes for our confirmed HTLC transaction.</summary>
    private OutputResolutionModel SecondLevelRow(ChainTx htlcTransaction, OutputResolutionModel parent)
    {
        var parentData = OutputDescriptorData.Decode(parent.DescriptorData);
        var data = new OutputDescriptorData(htlcTransaction.Outputs[0].AmountSat, htlcTransaction.Outputs[0].ScriptPubKey,
                                            null, 144, false, null, parentData.Htlc);
        return new OutputResolutionModel
        {
            TransactionId = htlcTransaction.TxId,
            OutputIndex = 0,
            ChannelId = _channel.ChannelId,
            Descriptor = OutputDescriptorKind.DelayedToLocal,
            DescriptorData = data.Encode(),
            HtlcDirection = parent.HtlcDirection,
            HtlcId = parent.HtlcId
        };
    }

    private OutputResolutionModel Row(OutputDescriptorKind kind) =>
        _store.Outputs.Values.Single(o => o.Descriptor == kind);

    private static ulong Sat(OutputResolutionModel row) => OutputDescriptorData.Decode(row.DescriptorData).AmountSat;

    private static long ValueMsat(OutputResolutionModel row) => (long)Sat(row) * 1_000;

    private static long Msat(AccountingEventModel accountingEvent, string key) =>
        long.Parse(accountingEvent.Details[key], CultureInfo.InvariantCulture);

    private static long OurBalanceMsat(CommitmentSpec spec) =>
        (long)spec.LocalMsat + spec.Htlcs.Where(h => h.Direction == HtlcDirection.Outgoing)
                                       .Sum(h => (long)h.AmountMsat);

    /// <summary>
    /// The pending on-chain bucket of a close (by default every close) from the events alone: what the close put in,
    /// plus every resolution's pending flows, a reversal counting as its original negated.
    /// </summary>
    private long PendingBalance(TxId? closeTxId = null)
    {
        var events = _store.Events;
        bool OfClose(AccountingEventModel e) =>
            closeTxId is not { } txId
         || (e.Kind == AccountingEventKind.ChannelForceClosed ? e.TxId == txId
                 : e.Details.TryGetValue(OnchainAccounting.CloseTxIdKey, out var close) && close == txId.ToString());

        long Flow(AccountingEventModel e) => e.Kind == AccountingEventKind.ChannelForceClosed
                                                 ? Msat(e, OnchainAccounting.PendingKey)
                                                 : Msat(e, OnchainAccounting.PendingInKey)
                                                 - Msat(e, OnchainAccounting.PendingOutKey);

        long balance = 0;
        foreach (var e in events)
        {
            if (e.Kind == AccountingEventKind.Reversal)
            {
                var original = events.Single(o => o.EventKey == e.Details[OnchainAccounting.ReversesKey]);
                if (OfClose(original))
                    balance -= Flow(original);
            }
            else if (OfClose(e))
            {
                balance += Flow(e);
            }
        }

        return balance;
    }

    private OutpointSpentEventArgs SpentBy(SignedTransaction spend) =>
        new(_channel.ChannelId, spend, SpendHeight, 1, _channel.FundingOutput!.TransactionId!.Value,
            _channel.FundingOutput.Index!.Value, OnchainTestStore.BlockHash(1));

    /// <summary>A commitment of the channel as it would be on chain (unsigned: the txid is the same).</summary>
    private SignedTransaction BuildCommitment(CommitmentSide side, CommitmentSpec spec, ulong number,
                                              CompactPubKey? remotePoint)
    {
        var factory = _provider.GetRequiredService<ICommitmentTransactionModelFactory>();
        var builder = _provider.GetRequiredService<ICommitmentTransactionBuilder>();
        var txSpec = CommitmentTxSpec.FromCommitmentSpec(spec);
        var model = side == CommitmentSide.Local
                        ? factory.CreateCommitmentTransactionModel(_channel, txSpec, CommitmentSide.Local, number)
                        : factory.CreateCommitmentTransactionModel(_channel, txSpec, CommitmentSide.Remote, number,
                                                                   remotePoint);
        return builder.BuildWithOutputMap(model).Transaction;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A resolver that resolves every close kind with what the test scripts.</summary>
    private sealed class ScriptedResolver : IOutputResolver
    {
        public Func<ChannelCloseModel, IReadOnlyList<OutputResolutionModel>, IReadOnlyList<OutputResolverAction>>?
            OnResolve
        { get; set; }

        public Func<ChannelCloseModel, OutputResolutionModel, ChainTx, IReadOnlyList<OutputResolverAction>>? OnSpent
        {
            get;
            set;
        }

        public bool CanResolve(ChannelCloseKind kind) => true;

        public Task<IReadOnlyList<OutputResolverAction>> ResolveAsync(ChannelCloseModel close,
                                                                      IReadOnlyList<OutputResolutionModel> outputs,
                                                                      uint height,
                                                                      CancellationToken cancellationToken) =>
            Task.FromResult(OnResolve?.Invoke(close, outputs) ?? []);

        public Task<IReadOnlyList<OutputResolverAction>> OnOutputSpentAsync(
            ChannelCloseModel close, OutputResolutionModel output, ChainTx spendingTransaction, uint height,
            CancellationToken cancellationToken) =>
            Task.FromResult(OnSpent?.Invoke(close, output, spendingTransaction) ?? []);
    }
}
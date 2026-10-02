using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NLightning.Tests.Utils.Accounting;

namespace NLightning.Application.Tests.Channels.Close;

using Application.Channels.Close;
using Application.Channels.Managers;
using Application.Channels.Reestablish;
using Application.Channels.Services;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Handlers;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The close outside the negotiation (BOLT2 plan N10-T3, NL-036): the closing transaction's confirmation makes the
/// channel Closed and forgets it in memory; a restart in Closing rebroadcasts the stored transaction; ShuttingDown and
/// Negotiating channels are registered and send channel_reestablish when the peer connects.
/// </summary>
public class ClosingLifecycleTests
{
    private static readonly SignedTransaction s_closingTx =
        new(new TxId(Enumerable.Repeat((byte)0x3c, 32).ToArray()), CreateRawTx());

    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChannelDbRepository> _channelDb = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IWatchedTransactionDbRepository> _watchedDb = new();
    private readonly Mock<Domain.Protocol.InteractiveTx.Interfaces.IInteractiveTxSessionDbRepository> _sessionsDb = new();
    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly ClosingNegotiationRegistry _registry = new();
    private readonly List<ChannelState> _persisted = [];
    private readonly Mock<IAccountingEventDbRepository> _accounting = new();
    private readonly List<AccountingEventModel> _accountingEvents = [];
    private readonly List<string> _saveOrder = [];

    public ClosingLifecycleTests()
    {
        _unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(_accounting.Object);
        _accounting.Setup(a => a.Add(It.IsAny<AccountingEventModel>()))
                   .Callback((AccountingEventModel e) =>
                    {
                        _accountingEvents.Add(e);
                        _saveOrder.Add("event");
                    });
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => _saveOrder.Add("save"))
                   .Returns(Task.CompletedTask);
        _unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_channelDb.Object);
        _unitOfWork.SetupGet(u => u.WatchedTransactionDbRepository).Returns(_watchedDb.Object);
        _unitOfWork.SetupGet(u => u.InteractiveTxSessionDbRepository).Returns(_sessionsDb.Object);
        _channelDb.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                  .Callback((ChannelModel c) => _persisted.Add(c.State))
                  .Returns(Task.CompletedTask);
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
    }

    [Fact]
    public async Task Given_Closing_When_ClosingTransactionConfirmed_Then_ClosedAndForgotten()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.Closing);
        channel.SetClosingTransaction(s_closingTx);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        _registry.Get(channelId);
        CreateManager();

        // Act
        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object, Confirmed(channelId, s_closingTx.TxId));

        // Assert
        await WaitUntilAsync(() => channel.State == ChannelState.Closed);
        Assert.Equal([ChannelState.Closed], _persisted);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
        _memory.Verify(m => m.TryRemoveChannel(channelId), Times.Once);
        Assert.False(_registry.TryGet(channelId, out _));

        // NL-470: the closed channel's stored negotiations go in the same save (the table has no FK to Channels)
        _sessionsDb.Verify(r => r.DeleteByChannelIdAsync(channelId), Times.Once);
    }

    [Fact]
    public async Task Given_Closing_When_ClosingTransactionConfirmed_Then_OneMutualCloseEventRidesInTheClosedSave()
    {
        // Arrange: our whole 1,000,000 sat balance, 999,000 sat to our shutdown script, 1,000 sat closing fee (ours)
        var channel = CreateClosingChannel(ChannelState.Closing);
        var closing = ClosingTx(channel, 999_000, null);
        channel.SetClosingTransaction(closing);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        CreateManager();

        // Act
        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object, Confirmed(channelId, closing.TxId));

        // Assert
        await WaitUntilAsync(() => channel.State == ChannelState.Closed);
        var closed = Assert.Single(_accountingEvents);
        Assert.Equal(AccountingEventKind.ChannelClosedMutual, closed.Kind);
        Assert.Equal(AccountingEventKeys.ChannelClosedMutual(channelId, closing.TxId), closed.EventKey);
        Assert.Equal(-1_000_000_000, closed.AmountMsat);
        Assert.Equal(1_000_000, closed.FeeMsat);
        Assert.Equal(AccountingFinality.Confirmed, closed.Finality);
        Assert.Equal(600U, closed.BlockHeight);
        Assert.Equal(closing.TxId, closed.TxId);
        Assert.Equal(0U, closed.OutputIndex);
        Assert.Equal(channelId, closed.ChannelId);
        Assert.Equal(NormalOperationTestContext.PeerNodeId, closed.Counterparty);
        Assert.Equal("999000", closed.Details["ourOutputSat"]);
        Assert.Equal("1000", closed.Details["closingFeeSat"]);
        Assert.Equal("true", closed.Details["feePaidByUs"]);
        Assert.Equal(["event", "save"], _saveOrder);

        // NL-602 A2 (the books): the balance leaves the channels, the closing fee is ours, and the clearing account
        // holds our closing output until the chain monitor's deposit of it (written in its format here) nets it
        var books = BooksSimulator.Of(_accountingEvents);
        Assert.Equal(-1_000_000_000, books[AccountRole.Channels]);
        Assert.Equal(1_000_000, books[AccountRole.FeeClose]);
        Assert.Equal(999_000_000, books[AccountRole.Clearing]);
        books.Apply(new AccountingEventModel
        {
            EventKey = AccountingEventKeys.WalletReceived(closing.TxId, 0),
            Kind = AccountingEventKind.WalletReceived,
            OccurredAt = closed.OccurredAt,
            BlockHeight = closed.BlockHeight,
            TxId = closing.TxId,
            OutputIndex = 0,
            AmountMsat = 999_000_000,
            Details = AccountingDetailsCodec.Create(("source", "channel"), ("change", "false"))
        });
        Assert.Equal(0, books[AccountRole.Clearing]);
        Assert.Equal(999_000_000, books[AccountRole.Wallet]);

        // A confirmation raised again (a replayed block) finds the channel Closed: nothing more
        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object, Confirmed(channelId, closing.TxId));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Single(_accountingEvents);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AClosedChannel_When_ItsClosingTransactionConfirmsAgain_Then_TheMutualCloseIsRecordedAgainOnlyAfterItsReversal(
        bool reversed)
    {
        // Arrange (NL-607): a Closed channel (not in memory) whose mutual close the chain monitor reversed when a reorg
        // rewound the closing watch (or did not: a replayed confirmation)
        var channel = CreateClosingChannel(ChannelState.Closing);
        var closing = ClosingTx(channel, 999_000, null);
        channel.SetClosingTransaction(closing);
        channel.UpdateState(ChannelState.Closed);
        var channelId = channel.ChannelId;
        _channelDb.Setup(r => r.GetByIdAsync(channelId)).ReturnsAsync(channel);
        var first = new AccountingEventModel
        {
            EventKey = AccountingEventKeys.ChannelClosedMutual(channelId, closing.TxId),
            Kind = AccountingEventKind.ChannelClosedMutual,
            OccurredAt = DateTimeOffset.UnixEpoch,
            BlockHeight = 590,
            ChannelId = channelId,
            TxId = closing.TxId,
            AmountMsat = -1_000_000_000,
            FeeMsat = 1_000_000,
            Finality = AccountingFinality.Confirmed
        };
        List<AccountingEventModel> recorded = reversed
                                                  ? [first, AccountingConfirmations.CreateReversal(first, DateTimeOffset.UnixEpoch, 580)]
                                                  : [first];
        var read = false;
        _accounting.Setup(a => a.GetByKeyPrefixAsync(first.EventKey, It.IsAny<CancellationToken>()))
                   .Callback(() => read = true)
                   .ReturnsAsync(recorded);
        CreateManager();

        // Act
        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object, Confirmed(channelId, closing.TxId));
        await WaitUntilAsync(() => reversed ? _saveOrder.Count == 2 : read);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert: recorded again at the new block under its next confirmation key, in its own save; the channel is
        // not brought back into memory
        _memory.Verify(m => m.AddChannel(It.IsAny<ChannelModel>()), Times.Never);
        if (!reversed)
        {
            Assert.Empty(_accountingEvents);
            return;
        }

        var again = Assert.Single(_accountingEvents);
        Assert.Equal(AccountingEventKeys.Reconfirmed(first.EventKey, 2), again.EventKey);
        Assert.Equal(AccountingEventKind.ChannelClosedMutual, again.Kind);
        Assert.Equal(600U, again.BlockHeight);
        Assert.Equal(first.AmountMsat, again.AmountMsat);
        Assert.Equal(first.FeeMsat, again.FeeMsat);
        Assert.Equal(["event", "save"], _saveOrder);
        Assert.Equal(ChannelState.Closed, channel.State);
    }

    [Fact]
    public async Task Given_ThePeerPaidTheClosingFee_When_ClosingTransactionConfirmed_Then_OnlyTheMsatWeCouldNotCarryAreOurFee()
    {
        // Arrange: 600,000.5 sat ours, the peer funded and pays the fee: our output carries the whole satoshis
        var channel = CreateClosingChannel(ChannelState.Closing, LightningMoney.MilliSatoshis(600_000_500));
        var closing = ClosingTx(channel, 600_000, 399_000);
        channel.SetClosingTransaction(closing);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        CreateManager();

        // Act
        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object, Confirmed(channelId, closing.TxId));

        // Assert
        await WaitUntilAsync(() => channel.State == ChannelState.Closed);
        var closed = Assert.Single(_accountingEvents);
        Assert.Equal(-600_000_500, closed.AmountMsat);
        Assert.Equal(500, closed.FeeMsat);

        // NL-602 A2 (the books): our 600,000 sat output reaches the clearing account, the half satoshi it could not
        // carry is our closing fee
        var books = BooksSimulator.Of(_accountingEvents);
        Assert.Equal(-600_000_500, books[AccountRole.Channels]);
        Assert.Equal(500, books[AccountRole.FeeClose]);
        Assert.Equal(600_000_000, books[AccountRole.Clearing]);
        Assert.Equal("1000", closed.Details["closingFeeSat"]);
        Assert.Equal("false", closed.Details["feePaidByUs"]);
    }

    [Fact]
    public async Task Given_ASimpleCloseThePeerClosedWithoutOurDustOutput_When_Confirmed_Then_TheFeeIsNotOurs()
    {
        // Arrange (NL-610): the peer funded and closed (option_simple_close); our 400 sat balance was below dust, so
        // the closing transaction has the peer's output only. Without the recorded terms the lost 400 sat read as a
        // closing fee we paid
        var channel = CreateClosingChannel(ChannelState.Closing, LightningMoney.Satoshis(400));
        var closing = SimpleClose(channel, false, new Script((byte[])channel.RemoteShutdownScript!));
        channel.SetClosingTransaction(closing);
        channel.SetCloseTerms(MutualCloseProtocol.Simple, false);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        CreateManager();

        // Act
        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object, Confirmed(channelId, closing.TxId));

        // Assert
        await WaitUntilAsync(() => channel.State == ChannelState.Closed);
        var closed = Assert.Single(_accountingEvents);
        Assert.Equal(-400_000, closed.AmountMsat);
        Assert.Equal("false", closed.Details["feePaidByUs"]);
        Assert.False(closed.Details.ContainsKey("feePayerInferred"));
        Assert.Equal("simple", closed.Details["closeProtocol"]);
        Assert.Equal("peer", closed.Details["closer"]);
    }

    [Fact]
    public void Given_MutualClosesFoundOnChain_When_TheirTermsAreRead_Then_ProtocolFromTheShapeAndCloserFromOurOutput()
    {
        // Arrange (NL-610): our whole 1,000,000 sat balance; a legacy close, and a simple close paying us 600,000 sat
        var channel = CreateClosingChannel(ChannelState.Closing);

        // Act
        var legacy = ChannelManager.CloseTermsOf(channel, MutualClose(channel));
        var simple = ChannelManager.CloseTermsOf(channel, SimpleClose(channel, true, new Script([0x51])));
        var withoutOurs = ChannelManager.CloseTermsOf(channel, SimpleClose(channel, false, new Script([0x51])));

        // Assert
        Assert.Equal((MutualCloseProtocol.Legacy, (bool?)null), legacy);
        Assert.Equal((MutualCloseProtocol.Simple, (bool?)true), simple);
        Assert.Equal((MutualCloseProtocol.Simple, (bool?)null), withoutOurs);
    }

    [Fact]
    public async Task Given_ClosingAtStartupWithCompletedWatch_When_Registered_Then_OneMutualCloseEventAtTheWatchHeight()
    {
        // Arrange
        var channel = CreateClosingChannel(ChannelState.Closing);
        var closing = ClosingTx(channel, 999_000, null);
        channel.SetClosingTransaction(closing);
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(closing.TxId))
                  .ReturnsAsync(Confirmed(channel.ChannelId, closing.TxId).WatchedTransaction);
        var manager = CreateManager();

        // Act
        await manager.RegisterExistingChannelAsync(channel);

        // Assert
        var closed = Assert.Single(_accountingEvents);
        Assert.Equal(AccountingEventKind.ChannelClosedMutual, closed.Kind);
        Assert.Equal(600U, closed.BlockHeight);
    }

    [Fact]
    public async Task Given_Closing_When_AnotherTransactionConfirmed_Then_StillClosing()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.Closing);
        channel.SetClosingTransaction(s_closingTx);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        CreateManager();

        // Act
        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object,
                       Confirmed(channelId, new TxId(Enumerable.Repeat((byte)0x55, 32).ToArray())));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelState.Closing, channel.State);
        Assert.Empty(_persisted);
        Assert.Empty(_accountingEvents);
    }

    [Fact]
    public async Task Given_ClosingAtStartup_When_Registered_Then_ClosingTransactionRebroadcast()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.Closing);
        channel.SetClosingTransaction(s_closingTx);
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(s_closingTx.TxId))
                  .ReturnsAsync(new WatchedTransactionModel(channel.ChannelId, s_closingTx.TxId, 6));
        var manager = CreateManager();

        // Act
        await manager.RegisterExistingChannelAsync(channel);

        // Assert
        _memory.Verify(m => m.AddChannel(channel), Times.Once);
        _chain.Verify(c => c.SendTransactionAsync(It.Is<Transaction>(t => t.ToBytes().SequenceEqual(s_closingTx.RawTxBytes))),
                      Times.Once);
        _monitor.Verify(m => m.WatchTransactionAsync(It.IsAny<ChannelId>(), It.IsAny<TxId>(), It.IsAny<uint>()),
                        Times.Never);
        _monitor.Verify(m => m.WatchOutpointSpend(channel.ChannelId, channel.FundingOutput!.TransactionId!.Value, 0),
                        Times.Once);
        Assert.Empty(_persisted);
    }

    [Fact]
    public async Task Given_ClosingAtStartupWithoutWatch_When_Registered_Then_WatchedAgainAndRebroadcast()
    {
        // Arrange - regression: an older build saved the watch after Closing; a crash between the saves left Closing
        // without its watch, so the confirmation never arrived and the channel stayed Closing
        var channel = CreateChannel(ChannelState.Closing);
        channel.SetClosingTransaction(s_closingTx);
        var manager = CreateManager();

        // Act
        await manager.RegisterExistingChannelAsync(channel);

        // Assert
        _monitor.Verify(m => m.WatchTransactionAsync(channel.ChannelId, s_closingTx.TxId, 6), Times.Once);
        _chain.Verify(c => c.SendTransactionAsync(It.IsAny<Transaction>()), Times.Once);
        Assert.Equal(ChannelState.Closing, channel.State);
    }

    [Fact]
    public async Task Given_ClosingAtStartupWithCompletedWatch_When_Registered_Then_Closed()
    {
        // Arrange - regression: the watch completed but CompleteCloseAsync (fire-and-forget) never persisted Closed
        var channel = CreateChannel(ChannelState.Closing);
        channel.SetClosingTransaction(s_closingTx);
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(s_closingTx.TxId))
                  .ReturnsAsync(Confirmed(channel.ChannelId, s_closingTx.TxId).WatchedTransaction);
        var manager = CreateManager();

        // Act
        await manager.RegisterExistingChannelAsync(channel);

        // Assert
        Assert.Equal(ChannelState.Closed, channel.State);
        Assert.Equal([ChannelState.Closed], _persisted);
        _memory.Verify(m => m.TryRemoveChannel(channel.ChannelId), Times.Once);
        _chain.Verify(c => c.SendTransactionAsync(It.IsAny<Transaction>()), Times.Never);
    }

    [Fact]
    public async Task Given_ClosingWithCompletedWatch_When_NewBlock_Then_Closed()
    {
        // Arrange - regression: only funding states were retried on a new block, so a close whose completion failed
        // stayed Closing
        var channel = CreateChannel(ChannelState.Closing);
        channel.SetClosingTransaction(s_closingTx);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => new[] { channel }.Where(predicate).ToList());
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(s_closingTx.TxId))
                  .ReturnsAsync(Confirmed(channelId, s_closingTx.TxId).WatchedTransaction);
        CreateManager();

        // Act
        _monitor.Raise(m => m.OnNewBlockDetected += null, _monitor.Object, new NewBlockEventArgs(610, new byte[32]));

        // Assert
        await WaitUntilAsync(() => channel.State == ChannelState.Closed);
        Assert.Equal([ChannelState.Closed], _persisted);
    }

    [Theory]
    [InlineData(ChannelState.ShuttingDown)]
    [InlineData(ChannelState.Negotiating)]
    [InlineData(ChannelState.Closing)]
    public async Task Given_ClosingChannel_When_FundingSpentByUnrecordedMutualClose_Then_ItBecomesTheClosingTx(
        ChannelState state)
    {
        // Arrange - regression: the peer broadcast a proposal we signed (our link dropped before its closing_signed
        // arrived, or it broadcast the other dust variant); nothing watched the funding output, so the channel never
        // moved on
        var channel = CreateClosingChannel(state);
        if (state == ChannelState.Closing)
            channel.SetClosingTransaction(s_closingTx);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        var added = new List<WatchedTransactionModel>();
        _watchedDb.Setup(r => r.Add(It.IsAny<WatchedTransactionModel>()))
                  .Callback((WatchedTransactionModel w) => added.Add(w));
        var registryEntry = _registry.Get(channelId);
        var waiter = registryEntry.WaitForClosingTxAsync();
        CreateManager();
        var spend = MutualClose(channel);

        // Act
        _monitor.Raise(m => m.OnWatchedOutpointSpent += null, _monitor.Object,
                       new OutpointSpentEventArgs(channelId, spend, 700, 3));

        // Assert: Closing and the watch (already seen at 700) in one save, then followed by the monitor
        await WaitUntilAsync(() => channel.ClosingTransaction?.TxId == spend.TxId);
        Assert.Equal(ChannelState.Closing, channel.State);
        Assert.Equal([ChannelState.Closing], _persisted);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
        var watch = Assert.Single(added);
        Assert.Equal(spend.TxId, watch.TransactionId);
        Assert.Equal(700U, watch.FirstSeenAtHeight);
        Assert.Equal(3U, watch.TransactionIndex);
        _monitor.Verify(m => m.TrackWatchedTransaction(watch), Times.Once);
        Assert.Equal(spend.TxId, await waiter.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // And its confirmation closes the channel as usual
        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object, Confirmed(channelId, spend.TxId));
        await WaitUntilAsync(() => channel.State == ChannelState.Closed);
    }

    [Fact]
    public async Task Given_FailedChannelThatSignedTheClose_When_FundingSpentByMutualClose_Then_ItBecomesTheClosingTxAndClosed()
    {
        // Arrange - NL-312: the channel failed after it signed a closing tx (Negotiating → Failed); when the peer
        // broadcasts the close we signed, the channel must not stay Failed for good
        var channel = CreateClosingChannel(ChannelState.Failed);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        var added = new List<WatchedTransactionModel>();
        _watchedDb.Setup(r => r.Add(It.IsAny<WatchedTransactionModel>()))
                  .Callback((WatchedTransactionModel w) => added.Add(w));
        CreateManager();
        var spend = MutualClose(channel);

        // Act
        _monitor.Raise(m => m.OnWatchedOutpointSpent += null, _monitor.Object,
                       new OutpointSpentEventArgs(channelId, spend, 700, 3));

        // Assert: the close becomes the closing tx, the state stays Failed (never lowered), then its confirmation
        // closes the channel (Failed 35 → Closed 40)
        await WaitUntilAsync(() => channel.ClosingTransaction?.TxId == spend.TxId);
        Assert.Equal(ChannelState.Failed, channel.State);
        Assert.Equal([ChannelState.Failed], _persisted);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
        var watch = Assert.Single(added);
        Assert.Equal(spend.TxId, watch.TransactionId);
        Assert.Equal(700U, watch.FirstSeenAtHeight);
        Assert.Equal(3U, watch.TransactionIndex);
        _monitor.Verify(m => m.TrackWatchedTransaction(watch), Times.Once);

        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object, Confirmed(channelId, spend.TxId));
        await WaitUntilAsync(() => channel.State == ChannelState.Closed);
        Assert.Equal([ChannelState.Failed, ChannelState.Closed], _persisted);
        _memory.Verify(m => m.TryRemoveChannel(channelId), Times.Once);
    }

    [Fact]
    public async Task Given_FailedWithCompletedCloseWatch_When_NewBlock_Then_Closed()
    {
        // Arrange - NL-312: the close of a Failed channel reached its depth but the completion raced the failure
        var channel = CreateClosingChannel(ChannelState.Failed);
        channel.SetClosingTransaction(s_closingTx);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => new[] { channel }.Where(predicate).ToList());
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(s_closingTx.TxId))
                  .ReturnsAsync(Confirmed(channelId, s_closingTx.TxId).WatchedTransaction);
        CreateManager();

        // Act
        _monitor.Raise(m => m.OnNewBlockDetected += null, _monitor.Object, new NewBlockEventArgs(610, new byte[32]));

        // Assert
        await WaitUntilAsync(() => channel.State == ChannelState.Closed);
        Assert.Equal([ChannelState.Closed], _persisted);
    }

    [Fact]
    public async Task Given_Negotiating_When_FundingSpentByAnotherTransaction_Then_Unchanged()
    {
        // Arrange: a commitment transaction (BOLT 5, not handled here) is not a mutual close
        var channel = CreateClosingChannel(ChannelState.Negotiating);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        CreateManager();
        var tx = Transaction.Load(MutualClose(channel).RawTxBytes, Network.RegTest);
        tx.Outputs[0].ScriptPubKey = new Key().PubKey.WitHash.ScriptPubKey;
        var spend = new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());

        // Act
        _monitor.Raise(m => m.OnWatchedOutpointSpent += null, _monitor.Object,
                       new OutpointSpentEventArgs(channelId, spend, 700, 3));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelState.Negotiating, channel.State);
        Assert.Null(channel.ClosingTransaction);
        Assert.Empty(_persisted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_PeerReplacedItsScript_When_FundingSpentByEarlierSimpleCloseTx_Then_ClosingThenClosed(
        bool withOurOutput)
    {
        // Arrange - regression: option_simple_close lets the peer change its script (a later closing_complete, or its
        // shutdown after a reconnection); a closing transaction we signed before pays its old script and must still
        // close the channel when it confirms instead of the stored one
        var channel = CreateClosingChannel(ChannelState.Closing);
        channel.SetClosingTransaction(s_closingTx);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        CreateManager();
        var oldPeerScript = new Key().PubKey.WitHash.ScriptPubKey;
        var spend = SimpleClose(channel, withOurOutput, oldPeerScript);

        // Act
        _monitor.Raise(m => m.OnWatchedOutpointSpent += null, _monitor.Object,
                       new OutpointSpentEventArgs(channelId, spend, 700, 3));

        // Assert: recorded as the closing transaction, then closed at depth
        await WaitUntilAsync(() => channel.ClosingTransaction?.TxId == spend.TxId);
        Assert.Equal(ChannelState.Closing, channel.State);
        _monitor.Raise(m => m.OnTransactionConfirmed += null, _monitor.Object, Confirmed(channelId, spend.TxId));
        await WaitUntilAsync(() => channel.State == ChannelState.Closed);
    }

    [Fact]
    public async Task Given_Negotiating_When_FundingSpentBySimpleCloseShapeWithTwoForeignOutputs_Then_NotAMutualClose()
    {
        // Arrange: a 0xFFFFFFFD spend with neither output to our script is not a closing transaction of ours
        var channel = CreateClosingChannel(ChannelState.Negotiating);
        var channelId = channel.ChannelId;
        _memory.Setup(m => m.TryGetChannel(channelId, out channel)).Returns(true);
        CreateManager();
        var tx = Transaction.Load(SimpleClose(channel, false, new Key().PubKey.WitHash.ScriptPubKey).RawTxBytes,
                                  Network.RegTest);
        tx.Outputs.Add(new TxOut(Money.Satoshis(1_000), new Key().PubKey.WitHash.ScriptPubKey));
        var spend = new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());

        // Act
        var isMutual = ChannelManager.IsMutualCloseOf(channel, spend);
        _monitor.Raise(m => m.OnWatchedOutpointSpent += null, _monitor.Object,
                       new OutpointSpentEventArgs(channelId, spend, 700, 3));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(isMutual);
        Assert.Null(channel.ClosingTransaction);
        Assert.Empty(_persisted);
    }

    [Theory]
    [InlineData(ChannelState.ShuttingDown)]
    [InlineData(ChannelState.Negotiating)]
    public async Task Given_ClosingNegotiationAtStartup_When_PeerConnects_Then_ReestablishSent(ChannelState state)
    {
        // Arrange (NL-036): registered as it is, then the reestablish starts the close again
        var channel = CreateChannel(state);
        channel.SetLocalShutdownScript(Convert.FromHexString("0014" + new string('1', 40)));
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => new[] { channel }.Where(predicate).ToList());
        var reestablishRequests = 0;
        var manager = CreateManager(services => services.AddScoped<ReestablishService>(_ =>
        {
            // The real service needs a whole node; being asked for it proves the reestablish path was taken
            reestablishRequests++;
            throw new InvalidOperationException("not built in this test");
        }));
        var raised = new List<Domain.Protocol.Interfaces.IChannelMessage>();
        manager.OnResponseMessageReady += (_, args) => raised.Add(args.ResponseMessage);
        await manager.RegisterExistingChannelAsync(channel);
        var registryEntry = _registry.Get(channel.ChannelId);
        registryEntry.ShutdownReceivedOnConnection = true;

        // Act
        await manager.OnPeerConnectedAsync(NormalOperationTestContext.PeerNodeId);

        // Assert: registered unchanged, and the connection state of the close was reset (B2-RE-29)
        _memory.Verify(m => m.AddChannel(channel), Times.Once);
        Assert.Equal(state, channel.State);
        Assert.False(registryEntry.ShutdownReceivedOnConnection);
        Assert.Empty(raised);
        Assert.Equal(1, reestablishRequests);
    }

    private ChannelManager CreateManager(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ReestablishTracker());
        services.AddSingleton(_registry);
        services.AddScoped<ChannelDomainEventQueue>();
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddScoped(_ => _chain.Object);
        configure?.Invoke(services);

        return new ChannelManager(_monitor.Object, new ChannelLockProvider(), _memory.Object,
                                  NullLogger<ChannelManager>.Instance, new Mock<ILightningSigner>().Object,
                                  services.BuildServiceProvider());
    }

    /// <summary>A closing transaction of <paramref name="channel"/> paying our and (when given) the peer's script.</summary>
    private static SignedTransaction ClosingTx(ChannelModel channel, long ourSat, long? theirSat)
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Version = 2;
        tx.Inputs.Add(new OutPoint(new uint256((byte[])channel.FundingOutput!.TransactionId!.Value), 0),
                      sequence: Sequence.Final);
        tx.Outputs.Add(new TxOut(Money.Satoshis(ourSat), new Script((byte[])channel.LocalShutdownScript!)));
        if (theirSat is { } their)
            tx.Outputs.Add(new TxOut(Money.Satoshis(their), new Script((byte[])channel.RemoteShutdownScript!)));
        return new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());
    }

    private static ChannelModel CreateClosingChannel(ChannelState state, LightningMoney? localBalance = null)
    {
        var channel = CreateChannel(state, localBalance);
        channel.SetLocalShutdownScript(Convert.FromHexString("0014" + new string('1', 40)));
        channel.SetRemoteShutdownScript(Convert.FromHexString("0014" + new string('2', 40)));
        return channel;
    }

    /// <summary>A mutual close of <paramref name="channel"/>: the funding outpoint, both shutdown scripts.</summary>
    private static SignedTransaction MutualClose(ChannelModel channel)
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Version = 2;
        tx.LockTime = LockTime.Zero;
        tx.Inputs.Add(new OutPoint(new uint256((byte[])channel.FundingOutput!.TransactionId!.Value), 0),
                      sequence: Sequence.Final);
        tx.Outputs.Add(new TxOut(Money.Satoshis(600_000), new Script((byte[])channel.LocalShutdownScript!)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(399_000), new Script((byte[])channel.RemoteShutdownScript!)));
        return new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());
    }

    /// <summary>
    /// An <c>option_simple_close</c> transaction of <paramref name="channel"/> (sequence 0xFFFFFFFD, lock time 777) to
    /// <paramref name="peerScript"/>, with our output when <paramref name="withOurOutput"/>.
    /// </summary>
    private static SignedTransaction SimpleClose(ChannelModel channel, bool withOurOutput, Script peerScript)
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Version = 2;
        tx.LockTime = new LockTime(777);
        tx.Inputs.Add(new OutPoint(new uint256((byte[])channel.FundingOutput!.TransactionId!.Value), 0),
                      sequence: new Sequence(0xFFFFFFFD));
        if (withOurOutput)
            tx.Outputs.Add(new TxOut(Money.Satoshis(600_000), new Script((byte[])channel.LocalShutdownScript!)));
        tx.Outputs.Add(new TxOut(Money.Satoshis(399_000), peerScript));
        return new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());
    }

    private static TransactionConfirmedEventArgs Confirmed(ChannelId channelId, TxId txId)
    {
        var watched = new WatchedTransactionModel(channelId, txId, 6);
        watched.SetHeightAndIndex(600, 1);
        watched.MarkAsCompleted();
        return new TransactionConfirmedEventArgs(watched, 605);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.True(condition());
    }

    private static byte[] CreateRawTx()
    {
        var tx = Transaction.Create(Network.RegTest);
        tx.Inputs.Add(new OutPoint(uint256.One, 0));
        tx.Outputs.Add(new TxOut(Money.Satoshis(10_000), new Key().PubKey.WitHash));
        return tx.ToBytes();
    }

    /// <summary>A channel we funded with the whole capacity ours, or, with <paramref name="localBalance"/>, a channel
    /// the peer funded with that balance ours.</summary>
    private static ChannelModel CreateChannel(ChannelState state, LightningMoney? localBalance = null)
    {
        var capacity = LightningMoney.Satoshis(1_000_000);
        var local = localBalance ?? capacity;
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(1_000_000), 144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No);
        var fundingOutput = new FundingOutputInfo(capacity,
                                                  NormalOperationTestContext.Point(0x01),
                                                  NormalOperationTestContext.Point(0x02))
        {
            TransactionId = new TxId(Enumerable.Repeat((byte)0x0f, 32).ToArray()),
            Index = 0
        };
        var keySet = new ChannelKeySetModel(0, NormalOperationTestContext.Point(0x01),
                                            NormalOperationTestContext.Point(0x03),
                                            NormalOperationTestContext.Point(0x04),
                                            NormalOperationTestContext.Point(0x05),
                                            NormalOperationTestContext.Point(0x06),
                                            NormalOperationTestContext.Point(0x07));
        return new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat((byte)0x0e, 32).ToArray()), null,
                                fundingOutput, localBalance is null, null, null, local, keySet, 0, 0,
                                capacity - local, keySet, 0, NormalOperationTestContext.PeerNodeId, 0, state,
                                ChannelVersion.V1);
    }
}
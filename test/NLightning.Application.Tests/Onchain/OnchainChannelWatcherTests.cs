using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain;

using Application.Channels.Safety.Interfaces;
using Application.Channels.Services;
using Application.Onchain;
using Application.Onchain.Interfaces;
using Application.Protocol.Factories;
using Channels.Services;
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
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Serialization;

/// <summary>
/// BOLT 5 plan O2-T5 (NL-272): <see cref="OnchainChannelWatcher"/> over <b>real</b> commitments
/// (<see cref="RealSigningCommitmentPair"/>, Alice's view): every classification of a funding spend persists the close,
/// its outputs of ours (with their watches) and <see cref="ChannelState.OnchainResolving"/> in one save, tells the peer
/// and starts the resolution; a replayed spend changes nothing.
/// </summary>
public sealed class OnchainChannelWatcherTests : IDisposable
{
    private const uint SpendHeight = 600;

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: false);
    private readonly OnchainTestStore _store = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChannelErrorSender> _errorSender = new();
    private readonly Mock<IOnchainResolutionExecutor> _executor = new();
    private readonly Mock<IOutpointWatcher> _outpointWatcher = new();
    private readonly Mock<ISecretStorageServiceFactory> _shachainFactory = new();
    private readonly RecordingLogger<OnchainChannelWatcher> _logger = new();
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    public OnchainChannelWatcherTests()
    {
        // Arrange (shared): an HTLC each way, committed on both sides
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Add(_pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(2));
        _pair.Settle(_pair.Alice);
        _channel = _pair.Alice.Channel;
        _channel.UpdateCommitments(_pair.Alice.State);

        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = _channel;
                    return id == _channel.ChannelId;
                }));
        _errorSender.Setup(s => s.TrySendAsync(It.IsAny<CompactPubKey>(), It.IsAny<ErrorMessage>()))
                    .ReturnsAsync(true);

        var unitOfWork = _store.CreateUnitOfWork();
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(new Mock<IRemoteShachainDbRepository>().Object);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILogger<OnchainChannelWatcher>>(_logger);
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
        services.AddSingleton(_executor.Object);
        services.AddSingleton(_outpointWatcher.Object);
        services.AddSingleton(_shachainFactory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton<OnchainChannelWatcher>();
        _provider = services.BuildServiceProvider();
    }

    private OnchainChannelWatcher Watcher => _provider.GetRequiredService<OnchainChannelWatcher>();

    [Fact]
    public async Task Given_OurCommitment_When_FundingSpent_Then_LocalCloseOutputsAndStatePersistedInOneSave()
    {
        // Arrange
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert: to_local and both HTLC outputs are ours; to_remote (the peer's) is not recorded
        Assert.NotNull(outcome);
        Assert.Equal(ChannelCloseKind.LocalCommitment, outcome.Kind);
        Assert.False(outcome.Replayed);
        var close = Assert.Single(_store.Closes.Values);
        Assert.Equal(spend.TxId, close.CommitmentTransactionId);
        Assert.Equal(local.Number, close.CommitmentNumber);
        Assert.Equal(SpendHeight, close.SpentAtHeight);
        AssertKinds([
            OutputDescriptorKind.DelayedToLocal, OutputDescriptorKind.LocalOfferedHtlc,
            OutputDescriptorKind.LocalReceivedHtlc
        ]);
        AssertRecordedInOneSave(expectedOutputs: 3);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);

        // The descriptor data holds the output's facts (never keys): the offered HTLC's hash and expiry
        var offered = _store.Outputs.Values.Single(o => o.Descriptor == OutputDescriptorKind.LocalOfferedHtlc);
        var data = OutputDescriptorData.Decode(offered.DescriptorData);
        Assert.Equal(HtlcDirection.Outgoing, offered.HtlcDirection);
        Assert.Equal(RealSigningCommitmentPair.Hash(RealSigningCommitmentPair.Preimage(1)), data.Htlc!.Value.PaymentHash);
        Assert.Equal(20_000UL, data.AmountSat);

        // After the save: watches followed, the peer told (B5-GEN-04), the resolution started at the spend height
        _outpointWatcher.Verify(w => w.TrackWatchedOutpoint(It.IsAny<WatchedOutpointModel>()), Times.Exactly(3));
        _errorSender.Verify(s => s.TrySendAsync(_channel.RemoteNodeId, It.IsAny<ErrorMessage>()), Times.Once);
        _executor.Verify(e => e.ResolveChannelAsync(_channel.ChannelId, SpendHeight, It.IsAny<CancellationToken>()),
                         Times.Once);
    }

    [Fact]
    public async Task Given_FundingSpent_When_Recorded_Then_NewWatchesCaughtUpFromTheSpendHeightBeforeTheFirstRound()
    {
        // Arrange (BOLT 5 "monitor every output not irrevocably resolved"): the monitor may already have processed a
        // block spending an output of the commitment (the same block, or one after it) before the watches existed
        var calls = new List<string>();
        IReadOnlyList<WatchedOutpointModel>? caughtUp = null;
        _outpointWatcher.Setup(w => w.TrackWatchedOutpoint(It.IsAny<WatchedOutpointModel>()))
                        .Callback(() => calls.Add("track"));
        _executor.Setup(e => e.CatchUpSpendsAsync(It.IsAny<ChannelId>(), It.IsAny<IReadOnlyList<WatchedOutpointModel>>(),
                                                  It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                 .Callback((ChannelId _, IReadOnlyList<WatchedOutpointModel> watches, uint _, CancellationToken _) =>
                  {
                      caughtUp = watches;
                      calls.Add("catch up");
                  })
                 .Returns(Task.CompletedTask);
        _executor.Setup(e => e.ResolveChannelAsync(It.IsAny<ChannelId>(), It.IsAny<uint>(),
                                                   It.IsAny<CancellationToken>()))
                 .Callback(() => calls.Add("resolve"))
                 .Returns(Task.CompletedTask);
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);

        // Act
        await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert: every new watch tracked, then caught up from the commitment's height, then the first round
        Assert.Equal(["track", "track", "track", "catch up", "resolve"], calls);
        Assert.Equal(_store.Watches.Values.Select(w => w.OutputIndex).Order(),
                     caughtUp!.Select(w => w.OutputIndex).Order());
        Assert.All(caughtUp!, w => Assert.Equal(_store.Closes[_channel.ChannelId].CommitmentTransactionId,
                                                w.TransactionId));
        _executor.Verify(e => e.CatchUpSpendsAsync(_channel.ChannelId, It.IsAny<IReadOnlyList<WatchedOutpointModel>>(),
                                                   SpendHeight, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_SameSpendConfirmedAgainElsewhere_When_Raised_Then_CloseHeightAndBlockUpdatedOnly()
    {
        // Arrange (a reorg re-confirms the commitment one block higher): the irrevocable depth counts from there
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);
        var saves = _store.Saves.Count;
        var moved = new OutpointSpentEventArgs(_channel.ChannelId, spend, SpendHeight + 1, 1,
                                               _channel.FundingOutput!.TransactionId!.Value,
                                               _channel.FundingOutput.Index!.Value, OnchainTestStore.BlockHash(7));

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(moved, TestContext.Current.CancellationToken);

        // Assert: a replay (outputs untouched) whose close now carries the new block
        Assert.True(outcome!.Replayed);
        var close = _store.Closes[_channel.ChannelId];
        Assert.Equal(SpendHeight + 1, close.SpentAtHeight);
        Assert.Equal(OnchainTestStore.BlockHash(7), close.BlockHash);
        Assert.Equal(["close"], _store.Saves[saves]);
        Assert.Equal(saves + 1, _store.Saves.Count);

        // Act: raised again in that block
        await Watcher.HandleFundingSpentAsync(moved, TestContext.Current.CancellationToken);

        // Assert: nothing more
        Assert.Equal(saves + 1, _store.Saves.Count);
    }

    [Fact]
    public async Task Given_AnotherSpendAfterAReorg_When_Raised_Then_OldRowsIgnoredAndOldPendingTransactionsAbandoned()
    {
        // Arrange (O6-T3, NL-292): our commitment was recorded and a sweep of it is pending; a reorg took it out and
        // the peer's commitment confirmed instead
        var local = _pair.Alice.State.LocalCommit;
        var ours = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        await Watcher.HandleFundingSpentAsync(SpentBy(ours), TestContext.Current.CancellationToken);
        var oldRows = _store.Outputs.Values.Where(o => o.TransactionId == ours.TxId).ToList();
        Assert.NotEmpty(oldRows);
        var sweep = new BroadcastTransactionModel(new SignedTransaction(new TxId(Enumerable.Repeat((byte)0x77, 32)
                                                                                    .ToArray()), [1, 2, 3]),
                                                  BroadcastPurpose.Sweep, _channel.ChannelId, SpendHeight + 1);
        _store.Broadcasts.Add(sweep);
        var remote = _pair.Alice.State.RemoteCommit;
        var theirs = BuildCommitment(CommitmentSide.Remote, remote.Spec, remote.Number, remote.PerCommitmentPoint);
        var reorged = new OutpointSpentEventArgs(_channel.ChannelId, theirs, SpendHeight + 2, 1,
                                                 _channel.FundingOutput!.TransactionId!.Value,
                                                 _channel.FundingOutput.Index!.Value, OnchainTestStore.BlockHash(9));

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(reorged, TestContext.Current.CancellationToken);

        // Assert: the new close with its own rows; every row of the old close ignored, the old sweep abandoned; the
        // channel still resolving on chain (never back to Open)
        Assert.Equal(ChannelCloseKind.RemoteCommitment, outcome!.Kind);
        Assert.Equal(theirs.TxId, _store.Closes[_channel.ChannelId].CommitmentTransactionId);
        Assert.All(_store.Outputs.Values.Where(o => o.TransactionId == ours.TxId),
                   o => Assert.Equal(OutputResolutionState.Ignored, o.State));
        Assert.Contains(_store.Outputs.Values, o => o.TransactionId == theirs.TxId
                                                 && o.State == OutputResolutionState.Pending);
        Assert.Equal(BroadcastState.Abandoned, sweep.State);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
    }

    [Fact]
    public async Task Given_PeersCurrentCommitment_When_FundingSpent_Then_RemoteCloseAndOurPendingCommitmentAbandoned()
    {
        // Arrange: we had failed the channel and our commitment was never confirmed (its row is pending)
        var ourCommitment = BuildCommitment(CommitmentSide.Local, _pair.Alice.State.LocalCommit.Spec,
                                            _pair.Alice.State.LocalCommit.Number, null);
        _store.Broadcasts.Add(new BroadcastTransactionModel(ourCommitment, BroadcastPurpose.LocalCommitment,
                                                            _channel.ChannelId, 590,
                                                            commitmentNumber: _pair.Alice.State.LocalCommit.Number));
        var remote = _pair.Alice.State.RemoteCommit;
        var spend = BuildCommitment(CommitmentSide.Remote, remote.Spec, remote.Number, remote.PerCommitmentPoint);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert: our to_remote and both HTLC outputs (claimed directly), and our commitment given up in that save
        Assert.Equal(ChannelCloseKind.RemoteCommitment, outcome!.Kind);
        AssertKinds([
            OutputDescriptorKind.PaymentToRemote, OutputDescriptorKind.RemoteOfferedHtlc,
            OutputDescriptorKind.RemoteReceivedHtlc
        ]);
        Assert.Equal(BroadcastState.Abandoned, _store.Broadcasts[0].State);
        Assert.Contains("broadcast abandoned", Assert.Single(_store.Saves));
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
    }

    [Fact]
    public async Task Given_PeersNextCommitment_When_FundingSpent_Then_RemoteNextClose()
    {
        // Arrange: Alice signed a new commitment for Bob, whose revoke_and_ack never came
        _pair.Add(_pair.Alice, 5_000_000, RealSigningCommitmentPair.Preimage(3));
        _pair.Alice.Apply("commit", _pair.Alice.State.SendCommit(_pair.Alice.CommitmentSigner));
        _channel.UpdateCommitments(_pair.Alice.State);
        var next = _pair.Alice.State.RemoteNextCommit!.Commit;
        var spend = BuildCommitment(CommitmentSide.Remote, next.Spec, next.Number, next.PerCommitmentPoint);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert: three HTLC outputs now (the new one too)
        Assert.Equal(ChannelCloseKind.RemoteNextCommitment, outcome!.Kind);
        Assert.Equal(next.Number, _store.Closes[_channel.ChannelId].CommitmentNumber);
        Assert.Equal(4, outcome.OutputCount);
        Assert.Contains(_store.Outputs.Values, o => o.Descriptor == OutputDescriptorKind.PaymentToRemote);
    }

    [Fact]
    public async Task Given_RevokedPeerCommitment_When_FundingSpent_Then_RevokedCloseWithEveryOutputToPenalize()
    {
        // Arrange: Bob's commitment with both HTLCs, revoked by the next round (its log entry and secret are known)
        var revoked = _pair.Alice.State.RemoteCommit;
        _pair.Add(_pair.Alice, 5_000_000, RealSigningCommitmentPair.Preimage(3));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        Assert.True(_pair.Alice.State.RemoteCommit.Number > revoked.Number);
        _store.RevocationLog[(_channel.ChannelId, revoked.Number)] =
            RevokedCommitmentModel.From(_channel.ChannelId, revoked);
        var secret = _pair.Bob.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, revoked.Number);
        var shachain = new Mock<ISecretStorageService>();
        shachain.Setup(s => s.DeriveOldSecret(It.IsAny<ulong>())).Returns(secret);
        _shachainFactory.Setup(f => f.CreatePerCommitmentStorage()).Returns(shachain.Object);
        var spend = BuildCommitment(CommitmentSide.Remote, revoked.Spec, revoked.Number, revoked.PerCommitmentPoint);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert: the cheater's to_local and both HTLC outputs to penalize, and our to_remote
        Assert.Equal(ChannelCloseKind.RevokedCommitment, outcome!.Kind);
        AssertKinds([
            OutputDescriptorKind.PaymentToRemote, OutputDescriptorKind.RevokedToLocal,
            OutputDescriptorKind.RevokedHtlc, OutputDescriptorKind.RevokedHtlc
        ]);
        AssertRecordedInOneSave(expectedOutputs: 4);
    }

    [Fact]
    public async Task Given_RevokedCommitmentOlderThanTheLog_When_FundingSpent_Then_UnmappedHtlcOutputsAlerted()
    {
        // Arrange: Bob's revoked commitment had both HTLCs, but it predates the revocation log (no entry)
        var revoked = _pair.Alice.State.RemoteCommit;
        _pair.Add(_pair.Alice, 5_000_000, RealSigningCommitmentPair.Preimage(3));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _store.RevocationLogStart = revoked.Number + 1;
        var secret = _pair.Bob.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, revoked.Number);
        var shachain = new Mock<ISecretStorageService>();
        shachain.Setup(s => s.DeriveOldSecret(It.IsAny<ulong>())).Returns(secret);
        _shachainFactory.Setup(f => f.CreatePerCommitmentStorage()).Returns(shachain.Object);
        var spend = BuildCommitment(CommitmentSide.Remote, revoked.Spec, revoked.Number, revoked.PerCommitmentPoint);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert: to_local and to_remote are found by script; the two HTLC outputs are named in a critical alert
        Assert.Equal(ChannelCloseKind.RevokedCommitment, outcome!.Kind);
        AssertKinds([OutputDescriptorKind.PaymentToRemote, OutputDescriptorKind.RevokedToLocal]);
        var alert = Assert.Single(_logger.Messages, m => m.Contains("[B5-GEN-06]", StringComparison.Ordinal));
        Assert.Contains("2 output(s)", alert);
        Assert.Contains("predates the revocation log", alert);
        Assert.Contains("20000 sat", alert);
        Assert.Contains("30000 sat", alert);
    }

    [Fact]
    public async Task Given_FuturePeerCommitment_When_FundingSpent_Then_OnlyToRemoteRecorded()
    {
        // Arrange (B5-RMT-03): a peer commitment numbered beyond anything we know (we lost data)
        var number = _pair.Alice.State.RemoteCommit.Number + 5;
        var spend = BuildCommitment(CommitmentSide.Remote, _pair.Alice.State.RemoteCommit.Spec, number,
                                    _pair.Bob.Point(number));

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelCloseKind.FutureCommitment, outcome!.Kind);
        AssertKinds([OutputDescriptorKind.PaymentToRemote]);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
    }

    [Fact]
    public async Task Given_UnknownSpend_When_FundingSpent_Then_UnknownCloseWithoutOutputs()
    {
        // Arrange (B5-GEN-06): not a commitment and no shutdown script
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(_channel.FundingOutput!.TransactionId!.Value), 0));
        transaction.Outputs.Add(Money.Satoshis(900_000), new Key().PubKey.WitHash.ScriptPubKey);
        var spend = new SignedTransaction(new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes());

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelCloseKind.Unknown, outcome!.Kind);
        Assert.Empty(_store.Outputs);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
        _executor.Verify(e => e.ResolveChannelAsync(_channel.ChannelId, SpendHeight, It.IsAny<CancellationToken>()),
                         Times.Once);
    }

    [Fact]
    public async Task Given_SpendAlreadyRecorded_When_ReplayedBlockRaisesItAgain_Then_NothingChanges()
    {
        // Arrange
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);
        var saves = _store.Saves.Count;

        // Act
        var replay = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert: reported as a replay; no save, no second error, no second watch
        Assert.True(replay!.Replayed);
        Assert.Equal(ChannelCloseKind.LocalCommitment, replay.Kind);
        Assert.Equal(3, replay.OutputCount);
        Assert.Equal(saves, _store.Saves.Count);
        _errorSender.Verify(s => s.TrySendAsync(It.IsAny<CompactPubKey>(), It.IsAny<ErrorMessage>()), Times.Once);
        _outpointWatcher.Verify(w => w.TrackWatchedOutpoint(It.IsAny<WatchedOutpointModel>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Given_FailedChannelWithStoredError_When_FundingSpent_Then_ErrorNotSentAgain()
    {
        // Arrange: we failed the channel (the peer already has our error)
        _channel.UpdateState(ChannelState.Failed);
        _channel.MarkErrorSent(new byte[] { 1, 2, 3 });
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);

        // Act
        await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
        _errorSender.Verify(s => s.TrySendAsync(It.IsAny<CompactPubKey>(), It.IsAny<ErrorMessage>()), Times.Never);
    }

    [Fact]
    public async Task Given_SpendOfAnotherOutpoint_When_Handled_Then_Ignored()
    {
        // Arrange: a resolution output of the channel, not its funding output
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        var args = new OutpointSpentEventArgs(_channel.ChannelId, spend, SpendHeight, 1,
                                              new TxId(Enumerable.Repeat((byte)9, 32).ToArray()), 0,
                                              OnchainTestStore.BlockHash(1));

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(args, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(outcome);
        Assert.Empty(_store.Saves);
        Assert.Equal(ChannelState.Open, _channel.State);
    }

    [Fact]
    public async Task Given_SaveFails_When_FundingSpent_Then_NothingFollowsAndTheReplayRecordsIt()
    {
        // Arrange
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);
        _store.FailNextSave = new InvalidOperationException("database down");

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken));

        // Assert: no watch followed, no error, no resolution; the channel's in-memory state was not published
        _outpointWatcher.Verify(w => w.TrackWatchedOutpoint(It.IsAny<WatchedOutpointModel>()), Times.Never);
        _errorSender.Verify(s => s.TrySendAsync(It.IsAny<CompactPubKey>(), It.IsAny<ErrorMessage>()), Times.Never);
        _executor.Verify(e => e.ResolveChannelAsync(It.IsAny<ChannelId>(), It.IsAny<uint>(),
                                                    It.IsAny<CancellationToken>()), Times.Never);
        _memory.Verify(m => m.UpdateChannel(It.IsAny<ChannelModel>()), Times.Never);
        Assert.Empty(_store.Saves);

        // Act: the block is processed again (the monitor raises the spend again)
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend), TestContext.Current.CancellationToken);

        // Assert: recorded now, in one save
        Assert.False(outcome!.Replayed);
        AssertRecordedInOneSave(expectedOutputs: 3);
        _executor.Verify(e => e.ResolveChannelAsync(_channel.ChannelId, SpendHeight, It.IsAny<CancellationToken>()),
                         Times.Once);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    /// <summary>Keeps every formatted log message, with its level's alert prefix intact.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                    return _messages.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            lock (_messages)
                _messages.Add(formatter(state, exception));
        }
    }

    private void AssertKinds(IEnumerable<OutputDescriptorKind> expected)
    {
        Assert.Equal(expected.OrderBy(k => k), _store.Outputs.Values.Select(o => o.Descriptor).OrderBy(k => k));
        Assert.All(_store.Outputs.Values, o =>
        {
            Assert.Equal(OutputResolutionState.Pending, o.State);
            Assert.Equal(_channel.ChannelId, o.ChannelId);
            Assert.Contains((o.TransactionId, o.OutputIndex), _store.Watches.Keys);
        });
        Assert.All(_store.Watches.Values, w => Assert.Equal(WatchedOutpointPurpose.ResolutionOutput, w.Purpose));
    }

    /// <summary>The close, every output, every watch, the stored error and the state change went in one save.</summary>
    private void AssertRecordedInOneSave(int expectedOutputs)
    {
        var save = Assert.Single(_store.Saves);
        Assert.Contains("close", save);
        Assert.Equal(expectedOutputs, save.Count(w => w.StartsWith("output ", StringComparison.Ordinal)));
        Assert.Equal(expectedOutputs, save.Count(w => w.StartsWith("watch ", StringComparison.Ordinal)));
        Assert.Contains("channel OnchainResolving", save);
        Assert.NotNull(_channel.ErrorSent);
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
}
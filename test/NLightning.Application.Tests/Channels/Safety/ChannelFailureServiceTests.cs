using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Safety;

using Application.Channels.Safety;
using Application.Channels.Safety.Interfaces;
using Application.Channels.Services;
using Application.Protocol.Factories;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Serialization;
using Services;

/// <summary>
/// BOLT2 plan N9-T4 (partial NL-094): <see cref="ChannelFailureService"/> over <b>real</b> commitments (two engines
/// with real <c>LocalLightningSigner</c>s, <see cref="RealSigningCommitmentPair"/>): the broadcast transaction is our
/// latest local commitment with a valid 2-of-2 witness (both signatures), persisted Failed before it is published,
/// never a revoked one, never after data loss; the confirmation closes the channel.
/// </summary>
public sealed class ChannelFailureServiceTests : IDisposable
{
    private RealSigningCommitmentPair _pair = null!;
    private readonly Mock<IBlockchainMonitor> _blockchainMonitor = new();
    private readonly Mock<IBitcoinChainService> _chainService = new();
    private readonly Mock<IChannelErrorSender> _errorSender = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChannelDbRepository> _channelDb = new();
    private readonly Mock<IWatchedTransactionDbRepository> _watchedDb = new();
    private readonly List<string> _calls = [];
    private readonly List<SignedTransaction> _published = [];
    private readonly List<WatchedTransactionModel> _storedWatches = [];
    private ServiceProvider _provider = null!;
    private ChannelModel _channel = null!;

    public ChannelFailureServiceTests()
    {
        Init(hasAnchors: false);
    }

    private void Init(bool hasAnchors)
    {
        _pair = new RealSigningCommitmentPair(hasAnchors);
        _channel = _pair.Alice.Channel;

        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = _channel;
                    return id == _channel.ChannelId;
                }));
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => predicate(_channel) ? [_channel] : []);
        _watchedDb.Setup(r => r.GetAllPendingAsync()).ReturnsAsync([]);
        _watchedDb.Setup(r => r.Add(It.IsAny<WatchedTransactionModel>()))
                  .Callback<WatchedTransactionModel>(w =>
                   {
                       _calls.Add("watch staged");
                       _storedWatches.Add(w);
                   });
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId id) => _storedWatches.FirstOrDefault(w => w.TransactionId == id));
        _channelDb.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                  .Callback<ChannelModel>(c => _calls.Add($"persist {c.State}"))
                  .Returns(Task.CompletedTask);
        _blockchainMonitor
           .Setup(m => m.PublishAndWatchTransactionAsync(It.IsAny<ChannelId>(), It.IsAny<SignedTransaction>(),
                                                         It.IsAny<uint>()))
           .Callback<ChannelId, SignedTransaction, uint>((_, tx, _) =>
            {
                _calls.Add("publish and watch");
                _published.Add(tx);
            })
           .Returns(Task.CompletedTask);
        _blockchainMonitor.Setup(m => m.TrackWatchedTransaction(It.IsAny<WatchedTransactionModel>()))
                          .Callback(() => _calls.Add("track"));
        _blockchainMonitor.Setup(m => m.PublishTransactionAsync(It.IsAny<SignedTransaction>()))
                          .Callback<SignedTransaction>(tx =>
                           {
                               _calls.Add("publish");
                               _published.Add(tx);
                           })
                          .Returns(Task.CompletedTask);
        _errorSender.Setup(s => s.TrySendAsync(It.IsAny<CompactPubKey>(), It.IsAny<ErrorMessage>()))
                    .Callback(() => _calls.Add("error sent"))
                    .ReturnsAsync(true);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_channelDb.Object);
        unitOfWork.SetupGet(u => u.WatchedTransactionDbRepository).Returns(_watchedDb.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => _calls.Add("save")).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSerializationInfrastructureServices();
        services.AddSingleton(_pair.Alice.Signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddSingleton(_blockchainMonitor.Object);
        services.AddSingleton(_chainService.Object);
        services.AddSingleton(_errorSender.Object);
        services.AddSingleton(_memory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddChannelSafetyServices();
        _provider = services.BuildServiceProvider();
    }

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    private ChannelFailureService Service => _provider.GetRequiredService<ChannelFailureService>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ChannelWithHtlcsBothWays_When_Failed_Then_LatestCommitmentBroadcastWithValidWitness(
        bool hasAnchors)
    {
        // Arrange
        if (hasAnchors)
        {
            Dispose();
            Init(hasAnchors: true);
        }

        var current = _pair;
        current.Add(current.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        current.Add(current.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(2));
        current.Settle(current.Alice);
        _channel.UpdateCommitments(current.Alice.State);
        var expectedTxId = current.Commitments.Last(c => c.Signer == "Bob").Verified;

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId,
                                                     new ChannelFailureRequest("test", "htlc timed out"),
                                                     TestContext.Current.CancellationToken);

        // Assert: the latest local commitment (the txid Alice verified last), published once
        Assert.Equal(ChannelFailureStatus.Broadcast, outcome.Status);
        var published = Assert.Single(_published);
        Assert.Equal(expectedTxId, published.TxId);
        Assert.Equal(expectedTxId, outcome.CommitmentTxId);

        var tx = Transaction.Load(published.RawTxBytes, Network.Main);
        AssertSpendsFundingOutput(tx, current);
        var htlcOutputs = tx.Outputs.Count(o => o.Value.Satoshi is 20_000 or 30_000);
        Assert.Equal(2, htlcOutputs);
        Assert.Equal(hasAnchors ? 6 : 4, tx.Outputs.Count);

        // Failed + error + the commitment's watch persisted in one save before the publish (NL-271), the error sent
        // after it
        Assert.Equal(["watch staged", "persist Failed", "save", "track", "publish", "error sent"], _calls);
        var watch = Assert.Single(_storedWatches);
        Assert.Equal(expectedTxId, watch.TransactionId);
        Assert.Equal(ChannelState.Failed, _channel.State);
        Assert.NotNull(_channel.ErrorSent);
    }

    [Fact]
    public async Task Given_OlderSnapshotThanSigner_When_Failed_Then_RevokedCommitmentNeverSigned()
    {
        // Arrange: keep Alice's state after the first commitment dance, then move on (the signer revokes it)
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        var revoked = _pair.Alice.State;
        _pair.Add(_pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(2));
        _pair.Settle(_pair.Bob);
        Assert.True(_pair.Alice.State.LocalCommit.Number > revoked.LocalCommit.Number);
        _channel.UpdateCommitments(revoked);

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId,
                                                     new ChannelFailureRequest("test", "htlc timed out"),
                                                     TestContext.Current.CancellationToken);

        // Assert (I4, B2-RAA-N01): the signer refuses; nothing is published, the channel is still failed
        Assert.Equal(ChannelFailureStatus.NoBroadcastableCommitment, outcome.Status);
        Assert.Empty(_published);
        Assert.Equal(ChannelState.Failed, _channel.State);
    }

    [Fact]
    public async Task Given_DataLoss_When_Failed_Then_NoBroadcastAndSignerRefusesAfterwards()
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _channel.MarkDataLossDetected();

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId,
                                                     new ChannelFailureRequest("test", "data loss"),
                                                     TestContext.Current.CancellationToken);

        // Assert (I12, B2-RE-23): failed and the error sent, nothing broadcast, and the signer is locked
        Assert.Equal(ChannelFailureStatus.RefusedDataLoss, outcome.Status);
        Assert.Empty(_published);
        Assert.Contains("error sent", _calls);
        Assert.Equal(ChannelState.Failed, _channel.State);
        var builder = _provider.GetRequiredService<LocalCommitmentBroadcastBuilder>();
        Assert.Throws<SignerException>(() => builder.Build(_channel));
        Assert.Throws<SignerException>(() => _pair.Alice.CommitmentSigner.SignRemoteCommitment(
                                                 _channel.ChannelId, 5,
                                                 _pair.Alice.State.BuildSpec(
                                                     Domain.Bitcoin.Transactions.Enums.CommitmentSide.Remote),
                                                 _pair.Bob.Point(5)));
    }

    [Fact]
    public async Task Given_ChannelWithoutSnapshot_When_Failed_Then_CommitmentZeroBroadcastWithFundingSignature()
    {
        // Arrange: before channel_ready there is no snapshot; the peer's funding_signed signature is stored
        var bob = _pair.Bob;
        var aliceCommitment0 = bob.SigningService.SignRemoteCommitment(
            bob.Channel, CommitmentTxSpec.FromChannel(bob.Channel), 0, _pair.Alice.Point(0));
        _channel.UpdateLastReceivedSignature(aliceCommitment0.Signature);

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId,
                                                     new ChannelFailureRequest("test", "reestablish zero"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.Broadcast, outcome.Status);
        var published = Assert.Single(_published);
        Assert.Equal(aliceCommitment0.CommitmentTxId, published.TxId);
        AssertSpendsFundingOutput(Transaction.Load(published.RawTxBytes, Network.Main), _pair);
    }

    [Fact]
    public async Task Given_FailedTwice_When_SecondCall_Then_NotPublishedAgainAndErrorKept()
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        var first = await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "first"),
                                                   TestContext.Current.CancellationToken);
        var storedError = _channel.ErrorSent!.Value.ToArray();

        // Act
        var second = await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("b", "second"),
                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.Broadcast, first.Status);
        Assert.Equal(ChannelFailureStatus.Rebroadcast, second.Status);
        Assert.Equal(first.CommitmentTxId, second.CommitmentTxId);
        Assert.Single(_published);
        Assert.Equal(storedError, _channel.ErrorSent!.Value.ToArray());
    }

    [Fact]
    public async Task Given_WatchAlreadyStored_When_Failed_Then_RebroadcastThroughChainService()
    {
        // Arrange: a previous run published it (the watch is in the database)
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId id) => new WatchedTransactionModel(_channel.ChannelId, id, 1));
        _chainService.Setup(c => c.SendTransactionAsync(It.IsAny<Transaction>()))
                     .ThrowsAsync(new InvalidOperationException("txn-already-in-mempool"));

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "again"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.Rebroadcast, outcome.Status);
        Assert.Empty(_published);
        _chainService.Verify(c => c.SendTransactionAsync(It.IsAny<Transaction>()), Times.Once);
    }

    [Fact]
    public async Task Given_WatchStoredAndSendRefusedForAnotherReason_When_Failed_Then_PublishFailedAndPending()
    {
        // Arrange: the watch exists (an earlier publish saved it before its send failed); bitcoind refuses again,
        // and does not know the transaction
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId id) => new WatchedTransactionModel(_channel.ChannelId, id, 1));
        _chainService.Setup(c => c.SendTransactionAsync(It.IsAny<Transaction>()))
                     .ThrowsAsync(new InvalidOperationException("min relay fee not met"));
        var service = Service;

        // Act
        var outcome = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "again"),
                                                     TestContext.Current.CancellationToken);

        // Assert: not reported as a rebroadcast, not remembered as published, retried later
        Assert.Equal(ChannelFailureStatus.PublishFailed, outcome.Status);
        Assert.False(service.TryGetPublishedCommitment(_channel.ChannelId, out _));
        Assert.True(service.IsPublishPending(_channel.ChannelId));
    }

    [Fact]
    public async Task Given_SendRefusedButNodeKnowsTransaction_When_Rebroadcast_Then_Rebroadcast()
    {
        // Arrange: the refusal text is unknown, but getrawtransaction finds it (mempool or chain)
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId id) => new WatchedTransactionModel(_channel.ChannelId, id, 1));
        _chainService.Setup(c => c.SendTransactionAsync(It.IsAny<Transaction>()))
                     .ThrowsAsync(new InvalidOperationException("some other wording"));
        _chainService.Setup(c => c.GetTransactionAsync(It.IsAny<uint256>())).ReturnsAsync(Transaction.Create(Network.Main));
        var service = Service;

        // Act
        var outcome = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "again"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.Rebroadcast, outcome.Status);
        Assert.False(service.IsPublishPending(_channel.ChannelId));
    }

    [Fact]
    public async Task Given_PublishFailsAfterWatchSavedAndResendFailsToo_When_Blocks_Then_RetriedUntilPublished()
    {
        // Arrange: the watch is saved with Failed, then the send throws (bitcoind down)
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        WatchedTransactionModel? saved = null;
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId id) => saved is not null && saved.TransactionId == id ? saved : null);
        _watchedDb.Setup(r => r.Add(It.IsAny<WatchedTransactionModel>()))
                  .Callback<WatchedTransactionModel>(w => saved = w);
        _blockchainMonitor.Setup(m => m.PublishTransactionAsync(It.IsAny<SignedTransaction>()))
                          .ThrowsAsync(new InvalidOperationException("bitcoind down"));
        _chainService.SetupSequence(c => c.SendTransactionAsync(It.IsAny<Transaction>()))
                     .ThrowsAsync(new InvalidOperationException("bitcoind down"))
                     .ReturnsAsync(uint256.One);
        var service = Service;
        var first = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b"),
                                                   TestContext.Current.CancellationToken);
        _calls.Clear();

        // Act: two blocks' retries
        await service.RetryPendingPublishesAsync(TestContext.Current.CancellationToken);
        var pendingAfterSecondFailure = service.IsPublishPending(_channel.ChannelId);
        await service.RetryPendingPublishesAsync(TestContext.Current.CancellationToken);

        // Assert: failed, failed again (still pending), then sent; the error is not re-sent by the retries
        Assert.Equal(ChannelFailureStatus.PublishFailed, first.Status);
        Assert.True(pendingAfterSecondFailure);
        Assert.False(service.IsPublishPending(_channel.ChannelId));
        Assert.True(service.TryGetPublishedCommitment(_channel.ChannelId, out var txId));
        Assert.Equal(first.CommitmentTxId, txId);
        _chainService.Verify(c => c.SendTransactionAsync(It.IsAny<Transaction>()), Times.Exactly(2));
        Assert.DoesNotContain("error sent", _calls);
    }

    [Fact]
    public async Task Given_StartedWithPendingPublish_When_NewBlock_Then_Retried()
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _blockchainMonitor.Setup(m => m.PublishTransactionAsync(It.IsAny<SignedTransaction>()))
                          .ThrowsAsync(new InvalidOperationException("bitcoind down"));
        var service = Service;
        service.Start();
        await service.WhenResumedAsync();
        await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b"),
                                       TestContext.Current.CancellationToken);
        Assert.True(service.IsPublishPending(_channel.ChannelId));

        // Act
        _blockchainMonitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(501, new byte[32]));

        // Assert
        await WaitUntilAsync(() => !service.IsPublishPending(_channel.ChannelId));
        Assert.True(service.TryGetPublishedCommitment(_channel.ChannelId, out _));
        service.Stop();
    }

    [Fact]
    public async Task Given_FailedChannelWithUnconfirmedCommitmentWatchAfterRestart_When_Started_Then_BroadcastResumed()
    {
        // Arrange: a previous run persisted Failed and the watch of its commitment, then crashed before the send;
        // the channel has no HTLC past a deadline, so the monitor would never ask again
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        var previousRun = await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b"),
                                                         TestContext.Current.CancellationToken);
        Assert.Equal(ChannelFailureStatus.Broadcast, previousRun.Status);
        var watch = new WatchedTransactionModel(_channel.ChannelId, previousRun.CommitmentTxId!.Value, 1);
        _watchedDb.Setup(r => r.GetAllPendingAsync()).ReturnsAsync([watch]);
        _watchedDb.Setup(r => r.GetByTransactionIdAsync(watch.TransactionId)).ReturnsAsync(watch);
        _calls.Clear();

        // A new process: a fresh service over the same channel memory and database
        using var service = ActivatorUtilities.CreateInstance<ChannelFailureService>(_provider);

        // Act
        service.Start();
        await service.WhenResumedAsync();

        // Assert: sent again through the chain service; no new watch, no error to a peer that is not connected yet
        _chainService.Verify(c => c.SendTransactionAsync(It.Is<Transaction>(t => t.GetHash() == new uint256(
                                                                                 watch.TransactionId))), Times.Once);
        Assert.True(service.TryGetPublishedCommitment(_channel.ChannelId, out _));
        Assert.DoesNotContain("error sent", _calls);
        Assert.Single(_published); // only the previous run's publish
    }

    [Theory]
    [InlineData(false, false)] // failed without broadcast and nothing watched: no intent to broadcast recorded
    [InlineData(true, true)] // data loss: never broadcast (I12)
    public async Task Given_FailedChannelWithoutBroadcastIntentOrWithDataLoss_When_Started_Then_NothingSent(
        bool hasWatch, bool dataLoss)
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b", Broadcast: false),
                                       TestContext.Current.CancellationToken);
        if (dataLoss)
            _channel.MarkDataLossDetected();
        if (hasWatch)
            _watchedDb.Setup(r => r.GetAllPendingAsync())
                      .ReturnsAsync([new WatchedTransactionModel(_channel.ChannelId, new TxId(new byte[32]), 1)]);
        var service = Service;

        // Act
        service.Start();
        await service.WhenResumedAsync();

        // Assert
        _chainService.Verify(c => c.SendTransactionAsync(It.IsAny<Transaction>()), Times.Never);
        Assert.Empty(_published);
        service.Stop();
    }

    [Fact]
    public async Task Given_PublishThrows_When_Failed_Then_PublishFailedAndChannelStillFailed()
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _blockchainMonitor.Setup(m => m.PublishTransactionAsync(It.IsAny<SignedTransaction>()))
                          .ThrowsAsync(new InvalidOperationException("bitcoind down"));

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.PublishFailed, outcome.Status);
        Assert.Equal(ChannelState.Failed, _channel.State);
    }

    [Fact]
    public async Task Given_CrashAfterFailedSave_When_Restarted_Then_BroadcastResumedFromTheSameSave()
    {
        // Arrange (NL-271): the process dies right after the Failed save, before anything is published
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _blockchainMonitor.Setup(m => m.PublishTransactionAsync(It.IsAny<SignedTransaction>()))
                          .ThrowsAsync(new OperationCanceledException("process stopped"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "htlc timed out"),
                                           TestContext.Current.CancellationToken));
        Assert.Equal(["watch staged", "persist Failed", "save", "track"], _calls);
        Assert.Empty(_published);
        var watch = Assert.Single(_storedWatches);
        _watchedDb.Setup(r => r.GetAllPendingAsync()).ReturnsAsync([watch]);
        _calls.Clear();

        // A new process over the same channel memory and database; no HTLC deadline asks again
        using var service = ActivatorUtilities.CreateInstance<ChannelFailureService>(_provider);

        // Act
        service.Start();
        await service.WhenResumedAsync();

        // Assert: the commitment recorded by the Failed save is sent; nothing is saved again
        _chainService.Verify(c => c.SendTransactionAsync(It.Is<Transaction>(t => t.GetHash() == new uint256(
                                                                                 watch.TransactionId))), Times.Once);
        Assert.True(service.TryGetPublishedCommitment(_channel.ChannelId, out var txId));
        Assert.Equal(watch.TransactionId, txId);
        Assert.DoesNotContain("save", _calls);
        service.Stop();
    }

    [Fact]
    public async Task Given_FailedWithoutWatch_When_FailedAgainWithBroadcast_Then_WatchSavedAloneAndPublished()
    {
        // Arrange: failed earlier without a broadcast (the error is stored)
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b", Broadcast: false),
                                       TestContext.Current.CancellationToken);
        var storedError = _channel.ErrorSent!.Value.ToArray();
        _calls.Clear();

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("c", "d"),
                                                     TestContext.Current.CancellationToken);

        // Assert: the watch is saved (the channel row is unchanged) before the publish, the stored error is kept
        Assert.Equal(ChannelFailureStatus.Broadcast, outcome.Status);
        Assert.Equal(["watch staged", "save", "track", "publish", "error sent"], _calls);
        Assert.Equal(storedError, _channel.ErrorSent!.Value.ToArray());
    }

    [Fact]
    public async Task Given_ConditionNoLongerHolds_When_Failed_Then_NotApplicableAndNothingPersisted()
    {
        // Arrange: the caller's reason went away while it waited for the lock
        var request = new ChannelFailureRequest("closing_signed reply timeout", "no reply")
        {
            StillApplies = _ => false
        };

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId, request,
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.NotApplicable, outcome.Status);
        Assert.Empty(_calls);
        Assert.NotEqual(ChannelState.Failed, _channel.State);
    }

    [Fact]
    public async Task Given_ChannelFailedExceptionWithoutBroadcast_When_Failed_Then_OnlyPersisted()
    {
        // Arrange
        var failure = new ChannelFailedException(_channel.ChannelId, "bad revocation", "bad secret")
        {
            MustBroadcast = false
        };

        // Act
        var outcome = await Service.FailChannelAsync(failure, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.FailedWithoutBroadcast, outcome.Status);
        Assert.Empty(_published);
        Assert.Equal(ChannelState.Failed, _channel.State);
    }

    [Fact]
    public async Task Given_UnknownChannel_When_Failed_Then_Throws()
    {
        // Act / Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.FailChannelAsync(
                                                           new ChannelId(new byte[32]),
                                                           new ChannelFailureRequest("a", "b"),
                                                           TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_BroadcastCommitment_When_Confirmed_Then_ChannelClosed()
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        var service = Service;
        service.Start();
        var outcome = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b"),
                                                     TestContext.Current.CancellationToken);
        var watched = new WatchedTransactionModel(_channel.ChannelId, outcome.CommitmentTxId!.Value, 1);
        watched.SetHeightAndIndex(500, 1);

        // Act
        _blockchainMonitor.Raise(m => m.OnTransactionConfirmed += null,
                                 new TransactionConfirmedEventArgs(watched, 500));

        // Assert
        await WaitUntilAsync(() => _channel.State == ChannelState.Closed);
        Assert.Contains("persist Closed", _calls);
        service.Stop();
    }

    [Fact]
    public async Task Given_FundingConfirmation_When_ChannelFailed_Then_NotClosed()
    {
        // Arrange
        var service = Service;
        service.Start();
        await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b", Broadcast: false),
                                       TestContext.Current.CancellationToken);
        var watched = new WatchedTransactionModel(_channel.ChannelId, _channel.FundingOutput!.TransactionId!.Value, 3);

        // Act
        _blockchainMonitor.Raise(m => m.OnTransactionConfirmed += null,
                                 new TransactionConfirmedEventArgs(watched, 500));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelState.Failed, _channel.State);
        service.Stop();
    }

    [Fact]
    public async Task Given_ClosedChannel_When_Failed_Then_NotApplicable()
    {
        // Arrange
        _channel.UpdateState(ChannelState.Closed);

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.NotApplicable, outcome.Status);
        Assert.Empty(_calls);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    private static void AssertSpendsFundingOutput(Transaction tx, RealSigningCommitmentPair pair)
    {
        var keys = new[] { new PubKey(pair.Alice.Basepoints.FundingPubKey), new PubKey(pair.Bob.Basepoints.FundingPubKey) }
                  .OrderBy(k => k.ToHex(), StringComparer.Ordinal)
                  .ToArray();
        var fundingScript = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, keys);
        var spent = new TxOut(Money.Satoshis((long)RealSigningCommitmentPair.FundingSatoshis),
                              fundingScript.WitHash.ScriptPubKey);

        Assert.Single(tx.Inputs);
        Assert.Equal(4, tx.Inputs[0].WitScript.PushCount);
        Assert.True(tx.Inputs.AsIndexedInputs().First().VerifyScript(spent, out var error),
                    $"the commitment's witness does not spend the funding output: {error}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);

        Assert.True(condition());
    }
}
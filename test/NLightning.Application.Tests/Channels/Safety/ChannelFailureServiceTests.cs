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
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Serialization;
using Services;

/// <summary>
/// BOLT2 plan N9-T4, BOLT 5 plan O2-T2 (NL-271): <see cref="ChannelFailureService"/> over <b>real</b> commitments (two
/// engines with real <c>LocalLightningSigner</c>s, <see cref="RealSigningCommitmentPair"/>): the broadcast transaction
/// is our latest local commitment with a valid 2-of-2 witness (both signatures), persisted with Failed and the error as
/// a <see cref="BroadcastPurpose.LocalCommitment"/> row carrying its commitment number, in one save, before it is
/// published; never a revoked one, never after data loss.
/// </summary>
public sealed class ChannelFailureServiceTests : IDisposable
{
    private RealSigningCommitmentPair _pair = null!;
    private readonly Mock<IBlockchainMonitor> _blockchainMonitor = new();
    private readonly Mock<IChannelErrorSender> _errorSender = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChannelDbRepository> _channelDb = new();
    private readonly Mock<IWatchedTransactionDbRepository> _watchedDb = new();
    private readonly Mock<IBroadcastTransactionDbRepository> _broadcastDb = new();
    private readonly List<string> _calls = [];
    private readonly List<SignedTransaction> _published = [];
    private readonly List<BroadcastTransactionModel> _storedBroadcasts = [];
    private readonly Queue<Func<bool>> _publishResults = new();
    private readonly RecordingLogger<ChannelFailureService> _log = new();
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
        _broadcastDb.Setup(r => r.Add(It.IsAny<BroadcastTransactionModel>()))
                    .Callback<BroadcastTransactionModel>(b =>
                     {
                         _calls.Add("broadcast staged");
                         _storedBroadcasts.Add(b);
                     });
        _broadcastDb.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                    .ReturnsAsync((TxId id) => _storedBroadcasts.FirstOrDefault(b => b.TransactionId == id));
        _broadcastDb.Setup(r => r.GetByChannelIdAsync(It.IsAny<ChannelId>()))
                    .ReturnsAsync((ChannelId id) => _storedBroadcasts.Where(b => b.ChannelId == id).ToList());
        _channelDb.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                  .Callback<ChannelModel>(c => _calls.Add($"persist {c.State}"))
                  .Returns(Task.CompletedTask);
        _blockchainMonitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(500);
        _blockchainMonitor.Setup(m => m.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                          .Returns((BroadcastTransactionModel b) =>
                           {
                               _calls.Add("publish");
                               var accepted = _publishResults.Count == 0 || _publishResults.Dequeue()();
                               if (accepted)
                                   _published.Add(b.ToSignedTransaction());
                               return Task.FromResult(accepted);
                           });
        _errorSender.Setup(s => s.TrySendAsync(It.IsAny<CompactPubKey>(), It.IsAny<ErrorMessage>()))
                    .Callback(() => _calls.Add("error sent"))
                    .ReturnsAsync(true);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_channelDb.Object);
        unitOfWork.SetupGet(u => u.WatchedTransactionDbRepository).Returns(_watchedDb.Object);
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(_broadcastDb.Object);
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
        services.AddSingleton(_errorSender.Object);
        services.AddSingleton(_memory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddChannelSafetyServices();
        services.AddSingleton<ILogger<ChannelFailureService>>(_log);
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

        // NL-271: Failed + error + the commitment's broadcast row persisted in one save before the publish, the error
        // sent after it; the row carries the commitment number (S1 restore, NL-297)
        Assert.Equal(["broadcast staged", "persist Failed", "save", "publish", "error sent"], _calls);
        var row = Assert.Single(_storedBroadcasts);
        Assert.Equal(expectedTxId, row.TransactionId);
        Assert.Equal(BroadcastPurpose.LocalCommitment, row.Purpose);
        Assert.Equal(_channel.ChannelId, row.ChannelId);
        Assert.Equal(current.Alice.State.LocalCommit.Number, row.CommitmentNumber);
        Assert.Equal(500u, row.FirstBroadcastHeight);
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

        // Assert (I4, B2-RAA-N01): the signer refuses; nothing is stored or published, the channel is still failed
        Assert.Equal(ChannelFailureStatus.NoBroadcastableCommitment, outcome.Status);
        Assert.Empty(_published);
        Assert.Empty(_storedBroadcasts);
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
        Assert.Empty(_storedBroadcasts);
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
        Assert.Equal(0UL, Assert.Single(_storedBroadcasts).CommitmentNumber);
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
        Assert.Single(_storedBroadcasts);
        Assert.Equal(storedError, _channel.ErrorSent!.Value.ToArray());
    }

    [Fact]
    public async Task Given_RowStoredByAnEarlierRun_When_Failed_Then_PublishedAgainWithoutANewRow()
    {
        // Arrange: a previous run failed the channel (its row is stored); this process never published it
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "first"),
                                       TestContext.Current.CancellationToken);
        _calls.Clear();
        using var service = ActivatorUtilities.CreateInstance<ChannelFailureService>(_provider);

        // Act
        var outcome = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "again"),
                                                     TestContext.Current.CancellationToken);

        // Assert: sent through the broadcaster, nothing staged again
        Assert.Equal(ChannelFailureStatus.Rebroadcast, outcome.Status);
        Assert.Single(_storedBroadcasts);
        Assert.Equal(["publish", "error sent"], _calls);
    }

    [Fact]
    public async Task Given_RowStoredAndRefusedAgain_When_Failed_Then_PublishFailedAndPending()
    {
        // Arrange: the row exists (an earlier run), and bitcoind refuses the send
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "first"),
                                       TestContext.Current.CancellationToken);
        using var service = ActivatorUtilities.CreateInstance<ChannelFailureService>(_provider);
        _publishResults.Enqueue(() => false);

        // Act
        var outcome = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "again"),
                                                     TestContext.Current.CancellationToken);

        // Assert: not reported as a rebroadcast, not remembered as published, retried later
        Assert.Equal(ChannelFailureStatus.PublishFailed, outcome.Status);
        Assert.False(service.TryGetPublishedCommitment(_channel.ChannelId, out _));
        Assert.True(service.IsPublishPending(_channel.ChannelId));
    }

    [Fact]
    public async Task Given_RowAlreadyConfirmed_When_Failed_Then_RebroadcastWithoutSending()
    {
        // Arrange: our commitment is already in a block
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "first"),
                                       TestContext.Current.CancellationToken);
        _storedBroadcasts[0].MarkConfirmed(501, new Hash(new byte[32]));
        using var service = ActivatorUtilities.CreateInstance<ChannelFailureService>(_provider);
        _calls.Clear();

        // Act
        var outcome = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "again"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.Rebroadcast, outcome.Status);
        Assert.DoesNotContain("publish", _calls);
    }

    [Fact]
    public async Task Given_RowAbandonedByThePeersCommitment_When_Failed_Then_SupersededAndNothingSent()
    {
        // Arrange: the on-chain watcher gave our commitment up (the peer's commitment spent the funding output)
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "first"),
                                       TestContext.Current.CancellationToken);
        _storedBroadcasts[0].MarkAbandoned();
        using var service = ActivatorUtilities.CreateInstance<ChannelFailureService>(_provider);
        _calls.Clear();

        // Act
        var outcome = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "again"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.Superseded, outcome.Status);
        Assert.DoesNotContain("publish", _calls);
        Assert.False(service.IsPublishPending(_channel.ChannelId));
    }

    [Fact]
    public async Task Given_PublishRefusedTwice_When_Blocks_Then_RetriedUntilPublished()
    {
        // Arrange: the row is saved with Failed, then bitcoind refuses the send twice
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _publishResults.Enqueue(() => false);
        _publishResults.Enqueue(() => false);
        var service = Service;
        var first = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b"),
                                                   TestContext.Current.CancellationToken);
        _calls.Clear();

        // Act: two blocks' retries
        await service.RetryPendingPublishesAsync(TestContext.Current.CancellationToken);
        var pendingAfterSecondFailure = service.IsPublishPending(_channel.ChannelId);
        await service.RetryPendingPublishesAsync(TestContext.Current.CancellationToken);

        // Assert: failed, failed again (still pending), then sent; the error is not re-sent by the retries and no
        // row is added again
        Assert.Equal(ChannelFailureStatus.PublishFailed, first.Status);
        Assert.True(pendingAfterSecondFailure);
        Assert.False(service.IsPublishPending(_channel.ChannelId));
        Assert.True(service.TryGetPublishedCommitment(_channel.ChannelId, out var txId));
        Assert.Equal(first.CommitmentTxId, txId);
        Assert.Equal(["publish", "publish"], _calls);
        Assert.Single(_storedBroadcasts);
    }

    [Fact]
    public async Task Given_StartedWithPendingPublish_When_NewBlock_Then_Retried()
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _publishResults.Enqueue(() => false);
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
    public async Task Given_FailedByAnOlderBuildWithOnlyAWatch_When_Started_Then_RowWrittenAndPublished()
    {
        // Arrange: an older build persisted Failed and the watch of its commitment, but no broadcast row; the
        // channel has no HTLC past a deadline, so the monitor would never ask again
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        var expected = _provider.GetRequiredService<LocalCommitmentBroadcastBuilder>().Build(_channel);
        await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b", Broadcast: false),
                                       TestContext.Current.CancellationToken);
        var watch = new WatchedTransactionModel(_channel.ChannelId, expected.Transaction.TxId, 1);
        _watchedDb.Setup(r => r.GetAllPendingAsync()).ReturnsAsync([watch]);
        _calls.Clear();

        // A new process: a fresh service over the same channel memory and database
        using var service = ActivatorUtilities.CreateInstance<ChannelFailureService>(_provider);

        // Act
        service.Start();
        await service.WhenResumedAsync();

        // Assert: the row is written alone and published; no error to a peer that is not connected yet
        Assert.Equal(["broadcast staged", "save", "publish"], _calls);
        Assert.Equal(expected.Transaction.TxId, Assert.Single(_published).TxId);
        Assert.Equal(expected.CommitmentNumber, Assert.Single(_storedBroadcasts).CommitmentNumber);
        service.Stop();
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
        Assert.Empty(_published);
        Assert.Empty(_storedBroadcasts);
        service.Stop();
    }

    [Fact]
    public async Task Given_PublishThrows_When_Failed_Then_PublishFailedAndChannelStillFailed()
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _publishResults.Enqueue(() => throw new InvalidOperationException("bitcoind down"));

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.PublishFailed, outcome.Status);
        Assert.Equal(ChannelState.Failed, _channel.State);
    }

    [Fact]
    public async Task Given_CrashAfterFailedSave_When_Restarted_Then_TheRowOfThatSaveIsPendingAndNotSavedAgain()
    {
        // Arrange (NL-271): the process dies right after the Failed save, before anything is published
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _publishResults.Enqueue(() => throw new OperationCanceledException("process stopped"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "htlc timed out"),
                                           TestContext.Current.CancellationToken));

        // Assert: the one save holds Failed and the pending row with its commitment number
        Assert.Equal(["broadcast staged", "persist Failed", "save", "publish"], _calls);
        Assert.Empty(_published);
        var row = Assert.Single(_storedBroadcasts);
        Assert.Equal(BroadcastState.Pending, row.State);
        Assert.Equal(_pair.Alice.State.LocalCommit.Number, row.CommitmentNumber);
        _calls.Clear();

        // Act: a new process; the chain monitor rebroadcasts pending rows itself, so the resume adds nothing
        using var service = ActivatorUtilities.CreateInstance<ChannelFailureService>(_provider);
        service.Start();
        await service.WhenResumedAsync();

        // Assert
        Assert.Empty(_calls);
        service.Stop();
    }

    [Fact]
    public async Task Given_FailedWithoutBroadcast_When_FailedAgainWithBroadcast_Then_RowSavedAloneAndPublished()
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

        // Assert: the row is saved (the channel row is unchanged) before the publish, the stored error is kept
        Assert.Equal(ChannelFailureStatus.Broadcast, outcome.Status);
        Assert.Equal(["broadcast staged", "save", "publish", "error sent"], _calls);
        Assert.Equal(storedError, _channel.ErrorSent!.Value.ToArray());
    }

    [Fact]
    public async Task Given_PreparedUnderTheLock_When_Completed_Then_SavedBeforeThePublishWhichWaitsForCompletion()
    {
        // Arrange (NL-271 remainder): the channel manager's path for a handler's MustBroadcast failure
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        var lockProvider = _provider.GetRequiredService<IChannelLockProvider>();
        var failure = new ChannelFailedException(_channel.ChannelId, "[B2-RE-14] peer lost its state", "state lost")
        {
            MustBroadcast = true
        };

        // Act
        PreparedChannelFailure prepared;
        using (await lockProvider.AcquireAsync(_channel.ChannelId, TestContext.Current.CancellationToken))
            prepared = await Service.PrepareFailureUnderLockAsync(_channel.ChannelId,
                                                                  ChannelFailureService.ToRequest(failure),
                                                                  TestContext.Current.CancellationToken);
        var callsBeforeCompletion = _calls.ToList();
        var outcome = await Service.CompleteFailureAsync(prepared, sendError: false,
                                                         TestContext.Current.CancellationToken);

        // Assert: Failed and the row in one save under the lock; published only by the completion, no error sent
        Assert.Equal(["broadcast staged", "persist Failed", "save"], callsBeforeCompletion);
        Assert.Equal(ChannelFailureStatus.Broadcast, outcome.Status);
        Assert.Equal(["broadcast staged", "persist Failed", "save", "publish"], _calls);
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
    public async Task Given_PublishPending_When_RequestNoLongerApplies_Then_RetryKeptAndNextBlockPublishes()
    {
        // Arrange (W4-E review F2): an earlier failure's publish was refused (bitcoind down), so it waits for the
        // next block; then a closing deadline's failure arrives whose precondition no longer holds (the channel is
        // Failed, not Negotiating)
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _publishResults.Enqueue(() => false);
        var service = Service;
        var first = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("reestablish", "b"),
                                                   TestContext.Current.CancellationToken);
        var stale = new ChannelFailureRequest("no satisfying fee_range in time", "no fee_range", true, "B2-CLS-R04")
        {
            StillApplies = c => c.State == ChannelState.Negotiating
        };

        // Act
        var outcome = await service.FailChannelAsync(_channel.ChannelId, stale, TestContext.Current.CancellationToken);
        var pendingAfterStaleRequest = service.IsPublishPending(_channel.ChannelId);
        await service.RetryPendingPublishesAsync(TestContext.Current.CancellationToken);

        // Assert: the stale request changed nothing, and the next block still published the commitment
        Assert.Equal(ChannelFailureStatus.PublishFailed, first.Status);
        Assert.Equal(ChannelFailureStatus.NotApplicable, outcome.Status);
        Assert.True(pendingAfterStaleRequest);
        Assert.False(service.IsPublishPending(_channel.ChannelId));
        Assert.True(service.TryGetPublishedCommitment(_channel.ChannelId, out var txId));
        Assert.Equal(first.CommitmentTxId, txId);
    }

    [Fact]
    public async Task Given_PublishPending_When_FailedAgainWithoutBroadcast_Then_RetryKept()
    {
        // Arrange: a refused publish waits for the next block, then the Failed channel is failed again without a
        // broadcast (e.g. a handler's ChannelFailedException without MustBroadcast)
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _publishResults.Enqueue(() => false);
        var service = Service;
        await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b"),
                                       TestContext.Current.CancellationToken);

        // Act
        var outcome = await service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("c", "d", false),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.FailedWithoutBroadcast, outcome.Status);
        Assert.True(service.IsPublishPending(_channel.ChannelId));
    }

    [Fact]
    public async Task Given_CommitmentBroadcast_When_Logged_Then_TxIdInDisplayOrder()
    {
        // Arrange (NL-275): the txid in the logs must be the one bitcoind, LND and explorers show (the reversed hash)
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        var published = Assert.Single(_published);
        var displayed = Transaction.Load(published.RawTxBytes, Network.Main).GetHash().ToString();
        var internalOrder = Convert.ToHexString((byte[])outcome.CommitmentTxId!.Value).ToLowerInvariant();
        Assert.NotEqual(internalOrder, displayed);
        Assert.Contains(_log.Messages, m => m.Contains(displayed, StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Messages, m => m.Contains(internalOrder, StringComparison.Ordinal));
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
    public async Task Given_BroadcastCommitmentConfirmed_When_Started_Then_ChannelLeftToTheOnchainWatcher()
    {
        // Arrange (BOLT 5 plan O2-T5): the failure service no longer closes a channel on its commitment's confirmation;
        // the funding spend moves it to OnchainResolving and the executor closes it after the resolution
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
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelState.Failed, _channel.State);
        Assert.DoesNotContain("persist Closed", _calls);
        service.Stop();
    }

    [Theory]
    [InlineData(ChannelState.Closing)]
    [InlineData(ChannelState.Closed)]
    [InlineData(ChannelState.OnchainResolving)]
    public async Task Given_ClosingClosedOrResolvingChannel_When_Failed_Then_NotApplicable(ChannelState state)
    {
        // Arrange
        _channel.UpdateState(state);

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

    /// <summary>Keeps every formatted log message.</summary>
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
}
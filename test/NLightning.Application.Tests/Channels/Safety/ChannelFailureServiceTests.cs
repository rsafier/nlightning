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
        _channelDb.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                  .Callback<ChannelModel>(c => _calls.Add($"persist {c.State}"))
                  .Returns(Task.CompletedTask);
        _blockchainMonitor
           .Setup(m => m.PublishAndWatchTransactionAsync(It.IsAny<ChannelId>(), It.IsAny<SignedTransaction>(),
                                                         It.IsAny<uint>()))
           .Callback<ChannelId, SignedTransaction, uint>((_, tx, _) =>
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

        // Failed + error persisted before the publish, the error sent after it
        Assert.Equal(["persist Failed", "save", "publish", "error sent"], _calls);
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
    public async Task Given_PublishThrows_When_Failed_Then_PublishFailedAndChannelStillFailed()
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _blockchainMonitor
           .Setup(m => m.PublishAndWatchTransactionAsync(It.IsAny<ChannelId>(), It.IsAny<SignedTransaction>(),
                                                         It.IsAny<uint>()))
           .ThrowsAsync(new InvalidOperationException("bitcoind down"));

        // Act
        var outcome = await Service.FailChannelAsync(_channel.ChannelId, new ChannelFailureRequest("a", "b"),
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelFailureStatus.PublishFailed, outcome.Status);
        Assert.Equal(ChannelState.Failed, _channel.State);
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
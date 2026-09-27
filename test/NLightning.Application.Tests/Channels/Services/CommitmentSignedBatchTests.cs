using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Services;

using Application.Channels.Interfaces;
using Application.Channels.Managers;
using Application.Channels.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Handlers;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using static Handlers.NormalOperationTestContext;

/// <summary>
/// The Application side of batched commitments (splicing plan SP1-B-T2, SP-OP-03/05/06/07): our batch is persisted
/// with its diff before it goes out, a received batch is persisted before the one revocation secret is released, and
/// <c>ChannelManager.HandleCommitmentSignedBatchAsync</c> raises the replies under the channel lock.
/// </summary>
public class CommitmentSignedBatchTests
{
    private static readonly TxId s_currentTxId = new(Enumerable.Repeat((byte)0x77, 32).ToArray());
    private static readonly TxId s_spliceTxId = new(Enumerable.Repeat((byte)0x88, 32).ToArray());

    private readonly NormalOperationTestContext _context = new();
    private readonly Mock<ICommitScheduler> _commitScheduler = new();

    /// <summary>We (the funder) splice in 500,000 sat: a pending funding of 1,500,000 sat.</summary>
    private static ChannelFunding SpliceIn() =>
        new(s_spliceTxId, 1, 1_500_000, Point(0x31), Point(0x32), 1, 500_000_000, 0, ChannelFundingKind.Splice,
            ChannelFundingStatus.Pending);

    private void AddPendingSplice() =>
        _context.SetState(_context.State.ReceiveSpliceCommitment(SpliceIn(), new CommitmentSignatures(Signature(9), []),
                                                                 _context.Ports).Next);

    private ChannelStateTransitionService CreateTransitions()
    {
        var secretStorageFactory = new Mock<ISecretStorageServiceFactory>();
        secretStorageFactory.Setup(f => f.CreatePerCommitmentStorage()).Returns(_context.Shachain.Object);
        return new ChannelStateTransitionService(_context.ChannelMemoryRepository.Object, _context.Events,
                                                 _context.Ports, _context.LightningSigner.Object,
                                                 NullLogger<ChannelStateTransitionService>.Instance,
                                                 _context.MessageFactory, _context.MessageSerializer.Object,
                                                 Options.Create(_context.NodeOptions), secretStorageFactory.Object,
                                                 _context.UnitOfWork.Object, commitmentVerifier: _context.Ports,
                                                 commitScheduler: _commitScheduler.Object);
    }

    /// <summary>The peer's batch for our next commitment: one member per given funding.</summary>
    private CommitmentSignedBatch PeerBatch(int htlcSignatures, params TxId[] fundings) =>
        new(TestChannelId,
            fundings.Select(f => _context.MessageFactory.CreateCommitmentSignedMessage(
                                TestChannelId, Signature(0x61), Enumerable.Repeat(Signature(0x62), htlcSignatures),
                                f)).ToList());

    [Fact]
    public async Task Given_APendingSplice_When_SigningPending_Then_StartBatchThenOneCommitmentSignedPerFundingAfterTheSave()
    {
        // Arrange
        AddPendingSplice();
        _context.SetState(_context.State.SendAdd(50_000_000, HashOf(SecretOf(1)), 600, Onion).Next);
        var transitions = CreateTransitions();

        // Act
        var messages = await transitions.SignPendingAsync(_context.Channel);

        // Assert: SP-OP-03 order, funding_txid on every member, persisted (with the whole batch in the diff) first
        Assert.Equal(3, messages.Count);
        var startBatch = Assert.IsType<StartBatchMessage>(messages[0]);
        Assert.Equal(2, startBatch.Payload.BatchSize);
        Assert.Equal(s_currentTxId, Assert.IsType<CommitmentSignedMessage>(messages[1]).FundingTxIdTlv!.FundingTxId);
        Assert.Equal(s_spliceTxId, Assert.IsType<CommitmentSignedMessage>(messages[2]).FundingTxIdTlv!.FundingTxId);
        Assert.Equal(["apply", "save"], _context.Calls);
        var diff = SentCommitDiffCodec.Split(Assert.Single(_context.Applied).Extras!.SentCommitDiff!.Value);
        Assert.Equal([MessageTypes.UpdateAddHtlc, MessageTypes.StartBatch, MessageTypes.CommitmentSigned,
                      MessageTypes.CommitmentSigned],
                     diff.Select(m => (MessageTypes)((m.Span[0] << 8) | m.Span[1])));
        Assert.Equal(s_spliceTxId,
                     Assert.Single(_context.State.RemoteNextCommit!.PendingFundingSignatures).FundingTxId);
    }

    [Fact]
    public async Task Given_NoPendingSplice_When_SigningPending_Then_OneCommitmentSigned()
    {
        // Arrange
        _context.SetState(_context.State.SendAdd(50_000_000, HashOf(SecretOf(1)), 600, Onion).Next);
        var transitions = CreateTransitions();

        // Act
        var messages = await transitions.SignPendingAsync(_context.Channel);
        var single = await CreateTransitions().SignIfPendingAsync(_context.Channel);

        // Assert
        var cs = Assert.IsType<CommitmentSignedMessage>(Assert.Single(messages));
        Assert.Equal(s_currentTxId, cs.FundingTxIdTlv!.FundingTxId);
        Assert.Null(single);
    }

    [Fact]
    public async Task Given_APendingSplice_When_TheSingleMessageFormIsAsked_Then_NothingIsSignedAndTheSchedulerSigns()
    {
        // Arrange: the receive handlers' follow-up can only return one commitment_signed
        AddPendingSplice();
        _context.SetState(_context.State.SendAdd(50_000_000, HashOf(SecretOf(1)), 600, Onion).Next);
        var before = _context.State;

        // Act
        var result = await CreateTransitions().SignIfPendingAsync(_context.Channel);

        // Assert
        Assert.Null(result);
        Assert.Same(before, _context.State);
        Assert.Empty(_context.Calls);
        _commitScheduler.Verify(s => s.Schedule(TestChannelId), Times.Once);
    }

    [Fact]
    public async Task Given_AValidBatch_When_Received_Then_PersistedBeforeTheSecretAndOneRevokeAndAckThenOurBatch()
    {
        // Arrange: the peer adds an HTLC and signs both fundings
        AddPendingSplice();
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(2)), 600, Onion).Next);
        var batch = PeerBatch(1, s_spliceTxId, s_currentTxId);

        // Act
        var replies = await CreateTransitions().ReceiveCommitmentSignedBatchAsync(_context.Channel, batch);

        // Assert: SP-OP-07 one revoke_and_ack, the secret only after the save (I3), then our own batch for the add
        Assert.IsType<RevokeAndAckMessage>(replies[0]);
        Assert.Single(replies.OfType<RevokeAndAckMessage>());
        Assert.IsType<StartBatchMessage>(replies[1]);
        Assert.Equal(2, replies.OfType<CommitmentSignedMessage>().Count());
        Assert.Equal(["apply", "save", "advance 1", "reveal 0", "apply", "save"], _context.Calls);
        var local = _context.Applied[0].Next.LocalCommit;
        Assert.Equal(1UL, local.Number);
        Assert.Equal(s_spliceTxId, Assert.Single(local.PendingFundingSignatures).FundingTxId);
        Assert.Equal(LastSentCommitmentMessage.RevokeAndAck, _context.Applied[0].Extras!.LastSent);
    }

    [Fact]
    public async Task Given_ABatchWithoutTheSplicesMember_When_Received_Then_ChannelFailedAndNothingPersistedOrRevealed()
    {
        // Arrange: SP-OP-05, a funding without its commitment_signed
        AddPendingSplice();
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(2)), 600, Onion).Next);
        var before = _context.State;

        // Act
        var e = await Assert.ThrowsAsync<ChannelFailedException>(
            () => CreateTransitions().ReceiveCommitmentSignedBatchAsync(_context.Channel, PeerBatch(1, s_currentTxId)));

        // Assert
        Assert.Equal("SP-OP-05", e.RequirementId);
        Assert.Empty(_context.Calls);
        Assert.Same(before, _context.State);
    }

    [Fact]
    public async Task Given_ABatchWithAnInvalidSignature_When_Received_Then_NothingPersistedOrRevealed()
    {
        // Arrange
        AddPendingSplice();
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(2)), 600, Onion).Next);
        _context.Ports.CommitmentSignaturesValid = false;

        // Act
        var e = await Assert.ThrowsAsync<ChannelWarningException>(
            () => CreateTransitions().ReceiveCommitmentSignedBatchAsync(_context.Channel,
                                                                        PeerBatch(1, s_currentTxId, s_spliceTxId)));

        // Assert: warning and close (B2-CS-R01), no secret for a batch that was not verified
        Assert.True(e.CloseConnection);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_NoPendingSplice_When_ABatchWithAnObsoleteMemberArrives_Then_ItIsIgnored()
    {
        // Arrange: SP-OP-06, a member for a funding we already left
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(2)), 600, Onion).Next);

        // Act
        var replies = await CreateTransitions().ReceiveCommitmentSignedBatchAsync(
                          _context.Channel, PeerBatch(1, s_spliceTxId, s_currentTxId));

        // Assert: our follow-up is a single commitment_signed again
        Assert.IsType<RevokeAndAckMessage>(replies[0]);
        Assert.IsType<CommitmentSignedMessage>(Assert.Single(replies.Skip(1)));
    }

    [Fact]
    public async Task Given_AValidBatch_When_TheManagerHandlesIt_Then_RepliesAreRaisedUnderTheChannelLock()
    {
        // Arrange
        AddPendingSplice();
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(2)), 600, Onion).Next);
        var (manager, lockProvider) = CreateChannelManager();
        var raised = new List<(IChannelMessage Message, bool LockHeld)>();
        manager.OnResponseMessageReady += (_, args) =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var held = false;
            try
            {
                lockProvider.AcquireAsync(TestChannelId, cts.Token).GetAwaiter().GetResult().Dispose();
            }
            catch (OperationCanceledException)
            {
                held = true;
            }

            raised.Add((args.ResponseMessage, held));
        };

        // Act
        await manager.HandleCommitmentSignedBatchAsync(PeerBatch(1, s_currentTxId, s_spliceTxId),
                                                       new FeatureOptions(), PeerNodeId);

        // Assert
        Assert.IsType<RevokeAndAckMessage>(raised[0].Message);
        Assert.All(raised, r => Assert.True(r.LockHeld));
        Assert.Equal(4, raised.Count);
    }

    [Fact]
    public async Task Given_ABatchBreakingTheRules_When_TheManagerHandlesIt_Then_TheChannelIsPersistedFailed()
    {
        // Arrange
        AddPendingSplice();
        _context.SetState(_context.State.ReceiveAdd(0, 50_000_000, HashOf(SecretOf(2)), 600, Onion).Next);
        var (manager, _) = CreateChannelManager();

        // Act
        await Assert.ThrowsAsync<ChannelFailedException>(
            () => manager.HandleCommitmentSignedBatchAsync(PeerBatch(1, s_currentTxId), new FeatureOptions(),
                                                           PeerNodeId));

        // Assert: Failed and the error are saved before the error goes out (N6-T3)
        Assert.Equal(ChannelState.Failed, _context.Channel.State);
        _context.ChannelDbRepository.Verify(r => r.UpdateAsync(_context.Channel), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Given_AMemberForAnotherChannel_When_TheManagerHandlesIt_Then_WarningAndClose()
    {
        // Arrange
        var (manager, _) = CreateChannelManager();
        var other = new ChannelId(Enumerable.Repeat((byte)0x3D, 32).ToArray());
        var batch = new CommitmentSignedBatch(TestChannelId,
                                              [
                                                  _context.MessageFactory.CreateCommitmentSignedMessage(
                                                      TestChannelId, Signature(1), [], s_currentTxId),
                                                  _context.MessageFactory.CreateCommitmentSignedMessage(
                                                      other, Signature(1), [], s_spliceTxId)
                                              ]);

        // Act
        var e = await Assert.ThrowsAsync<ChannelWarningException>(
            () => manager.HandleCommitmentSignedBatchAsync(batch, new FeatureOptions(), PeerNodeId));

        // Assert
        Assert.True(e.CloseConnection);
        Assert.Empty(_context.Calls);
    }

    private (ChannelManager Manager, ChannelLockProvider LockProvider) CreateChannelManager()
    {
        _context.ChannelMemoryRepository
                .Setup(r => r.TryGetChannelState(TestChannelId, out It.Ref<ChannelState>.IsAny))
                .Returns(new TryGetStateDelegate((ChannelId _, out ChannelState state) =>
                 {
                     state = _context.Channel.State;
                     return true;
                 }));
        var services = new ServiceCollection();
        services.AddScoped(_ => _context.UnitOfWork.Object);
        services.AddScoped<ChannelDomainEventQueue>();
        services.AddSingleton<IMessageFactory>(_context.MessageFactory);
        services.AddSingleton(_context.MessageSerializer.Object);
        services.AddScoped(_ => CreateTransitions());
        var lockProvider = new ChannelLockProvider();
        var manager = new ChannelManager(new Mock<IBlockchainMonitor>().Object, lockProvider,
                                         _context.ChannelMemoryRepository.Object, NullLogger<ChannelManager>.Instance,
                                         _context.LightningSigner.Object, services.BuildServiceProvider());
        return (manager, lockProvider);
    }

    private delegate bool TryGetStateDelegate(ChannelId channelId, out ChannelState state);
}
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
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
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
/// Splicing plan §3.6, wave sp2 lane SP2-C (SP2-C-T1..T3, NL-479): <see cref="OnchainChannelWatcher"/> over <b>real</b>
/// commitments (<see cref="RealSigningCommitmentPair"/>, Alice's view) of a channel with a pending splice: the splice
/// transaction is never a close, a commitment on the pending splice funding is classified and mapped against that
/// funding (its outpoint, capacity and deltas), a commitment on the current funding discards the pending splice, a
/// revoked commitment of a retired funding is penalized with that funding's revocation log entry, and a splice that
/// confirms after the channel failed has our commitment broadcast on it.
/// </summary>
public sealed class OnchainSpliceWatcherTests : IDisposable
{
    private const uint SpendHeight = 600;
    private const long SpliceInMsat = 250_000_000;

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: false);
    private readonly OnchainTestStore _store = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChannelErrorSender> _errorSender = new();
    private readonly Mock<IOnchainResolutionExecutor> _executor = new();
    private readonly Mock<IOutpointWatcher> _outpointWatcher = new();
    private readonly Mock<ISecretStorageServiceFactory> _shachainFactory = new();
    private readonly Mock<IChannelFundingDbRepository> _fundings = new();
    private readonly Mock<IRevokedCommitmentDbRepository> _revocationLog = new();
    private readonly Mock<ISpliceCommitmentBroadcaster> _spliceBroadcaster = new();
    private readonly List<ChannelFunding> _storedFundings = [];
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    public OnchainSpliceWatcherTests()
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
        _fundings.Setup(f => f.GetByChannelIdAsync(It.IsAny<ChannelId>()))
                 .ReturnsAsync(() => _storedFundings.ToList());

        var unitOfWork = _store.CreateUnitOfWork();
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(new Mock<IRemoteShachainDbRepository>().Object);
        unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(_fundings.Object);
        unitOfWork.SetupGet(u => u.RevokedCommitmentDbRepository).Returns(_revocationLog.Object);
        // The database's copy of the channel: another instance than the shared in-memory model (NL-307)
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
        services.AddSingleton(_executor.Object);
        services.AddSingleton(_outpointWatcher.Object);
        services.AddSingleton(_shachainFactory.Object);
        services.AddSingleton(_spliceBroadcaster.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton<OnchainChannelWatcher>();
        _provider = services.BuildServiceProvider();
    }

    private OnchainChannelWatcher Watcher => _provider.GetRequiredService<OnchainChannelWatcher>();

    private ChannelFunding Current => ChannelFunding.FromFundingOutput(_channel.FundingOutput!)!;

    [Fact]
    public async Task Given_OurPendingSpliceTx_When_ItSpendsTheFundingOutput_Then_NoCloseAndTheChannelStaysOpen()
    {
        // Arrange (SP2-C-T1): the splice transaction of a pending splice confirms
        var spliceTx = BuildSpliceTransaction();
        var splice = AddPendingSplice(spliceTx.TxId, 0);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spliceTx, Current),
                                                            TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(outcome);
        Assert.Empty(_store.Closes);
        Assert.Empty(_store.Saves);
        Assert.Equal(ChannelState.Open, _channel.State);
        Assert.Null(_channel.ErrorSent);
        _executor.Verify(e => e.ResolveChannelAsync(It.IsAny<ChannelId>(), It.IsAny<uint>(),
                                                    It.IsAny<CancellationToken>()), Times.Never);
        _spliceBroadcaster.Verify(b => b.BroadcastOnSpliceAsync(It.IsAny<ChannelId>(), It.IsAny<TxId>(),
                                                                It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(ChannelFundingStatus.Pending, splice.Status);
    }

    [Fact]
    public async Task Given_OurPendingSpliceTx_When_ReplayedAfterTheLock_Then_StillNoClose()
    {
        // Arrange: after the lock the old funding is Replaced and the splice is current; a replayed block raises the
        // splice's spend of the old funding again
        var spliceTx = BuildSpliceTransaction();
        _storedFundings.Add(Current with { Status = ChannelFundingStatus.Replaced });
        _storedFundings.Add(Splice(spliceTx.TxId, 0) with { Status = ChannelFundingStatus.Current });

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spliceTx, Current),
                                                            TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(outcome);
        Assert.Empty(_store.Closes);
    }

    [Fact]
    public async Task Given_TheChannelFailed_When_ItsPendingSpliceConfirms_Then_OurCommitmentOnItIsAskedFor()
    {
        // Arrange (SP2-C-T2): we failed the channel (our commitment on the current funding in flight), the splice won
        var spliceTx = BuildSpliceTransaction();
        AddPendingSplice(spliceTx.TxId, 0);
        _channel.UpdateState(ChannelState.Failed);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spliceTx, Current),
                                                            TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(outcome);
        Assert.Empty(_store.Closes);
        _spliceBroadcaster.Verify(b => b.BroadcastOnSpliceAsync(_channel.ChannelId, spliceTx.TxId,
                                                                It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ARecordedCloseReorgedOut_When_TheDiscardedSpliceConfirms_Then_TheCloseIsRetiredAndOurCommitmentGoesOutOnTheSplice()
    {
        // Arrange (SP2-C-T2, reorg): our commitment on the current funding confirmed and discarded the pending splice;
        // its block was disconnected and the splice was mined in its place
        var spliceTx = BuildSpliceTransaction();
        var splice = Splice(spliceTx.TxId, 0) with { Status = ChannelFundingStatus.Discarded };
        _storedFundings.Add(Current);
        _storedFundings.Add(splice);
        _channel.UpdateState(ChannelState.OnchainResolving);
        var oldClose = TxIdOf(0xD1);
        _store.Closes[_channel.ChannelId] = new ChannelCloseModel(_channel.ChannelId, ChannelCloseKind.LocalCommitment,
                                                                  oldClose, _pair.Alice.State.LocalCommit.Number,
                                                                  SpendHeight - 3, OnchainTestStore.BlockHash(9),
                                                                  DateTimeOffset.UtcNow);
        _store.Outputs[(oldClose, 0)] = new OutputResolutionModel
        {
            TransactionId = oldClose,
            OutputIndex = 0,
            ChannelId = _channel.ChannelId,
            Descriptor = OutputDescriptorKind.DelayedToLocal
        };
        var staleCommitment = new BroadcastTransactionModel(new SignedTransaction(oldClose, [0x02, 0x00]),
                                                            BroadcastPurpose.LocalCommitment, _channel.ChannelId,
                                                            SpendHeight - 4,
                                                            commitmentNumber: _pair.Alice.State.LocalCommit.Number);
        _store.Broadcasts.Add(staleCommitment);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spliceTx, Current),
                                                            TestContext.Current.CancellationToken);

        // Assert: in one save the old close is gone, its outputs ignored, its commitment abandoned and the splice pending
        // again; then our commitment on the splice funding is asked for
        Assert.Null(outcome);
        Assert.Empty(_store.Closes);
        Assert.Equal(OutputResolutionState.Ignored, _store.Outputs[(oldClose, 0)].State);
        Assert.Equal(BroadcastState.Abandoned, staleCommitment.State);
        _fundings.Verify(f => f.UpsertAsync(_channel.ChannelId, It.Is<ChannelFunding>(
                                                d => d.FundingTxId == splice.FundingTxId
                                                  && d.Status == ChannelFundingStatus.Pending)), Times.Once);
        Assert.Single(_store.Saves);
        _spliceBroadcaster.Verify(b => b.BroadcastOnSpliceAsync(_channel.ChannelId, spliceTx.TxId,
                                                                It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ACloseRecordedOnThePendingSplice_When_TheSpliceItselfIsSeen_Then_TheCloseStands()
    {
        // Arrange: the peer's commitment on the (still pending) splice funding was recorded; a replayed block raises
        // the splice's spend of the current funding
        var spliceTx = BuildSpliceTransaction();
        AddPendingSplice(spliceTx.TxId, 0);
        _channel.UpdateState(ChannelState.OnchainResolving);
        var close = new ChannelCloseModel(_channel.ChannelId, ChannelCloseKind.RemoteCommitment, TxIdOf(0xD2),
                                          _pair.Alice.State.RemoteCommit.Number, SpendHeight + 1,
                                          OnchainTestStore.BlockHash(2), DateTimeOffset.UtcNow);
        _store.Closes[_channel.ChannelId] = close;

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spliceTx, Current),
                                                            TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(outcome);
        Assert.Same(close, _store.Closes[_channel.ChannelId]);
        Assert.Empty(_store.Saves);
        _spliceBroadcaster.Verify(b => b.BroadcastOnSpliceAsync(It.IsAny<ChannelId>(), It.IsAny<TxId>(),
                                                                It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_ARevokedCommitmentOnThePendingSplice_When_OnlyTheCurrentFundingLoggedIt_Then_ItsHtlcOutputsAreMapped()
    {
        // Arrange (NL-479 after a restart: the engine lost the pending fundings, so the revocation was logged on the
        // current funding only). The peer broadcasts that revoked commitment on the pending splice funding.
        var revoked = _pair.Alice.State.RemoteCommit;
        _pair.Add(_pair.Alice, 5_000_000, RealSigningCommitmentPair.Preimage(3));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        var splice = AddPendingSplice(TxIdOf(0xD3), 1);
        _revocationLog.Setup(l => l.GetAsync(_channel.ChannelId, revoked.Number))
                      .ReturnsAsync(RevokedCommitmentModel.From(_channel.ChannelId, revoked) with
                      {
                          FundingTxId = Current.FundingTxId
                      });
        var secret = _pair.Bob.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, revoked.Number);
        var shachain = new Mock<ISecretStorageService>();
        shachain.Setup(s => s.DeriveOldSecret(It.IsAny<ulong>())).Returns(secret);
        _shachainFactory.Setup(f => f.CreatePerCommitmentStorage()).Returns(shachain.Object);
        var spend = BuildCommitment(CommitmentSide.Remote, ChannelCommitments.SpecFor(revoked.Spec, splice),
                                    revoked.Number, revoked.PerCommitmentPoint, splice);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend, splice),
                                                            TestContext.Current.CancellationToken);

        // Assert: both HTLC outputs to penalize (the stand-in spec without HTLCs would leave them unmapped)
        Assert.Equal(ChannelCloseKind.RevokedCommitment, outcome!.Kind);
        Assert.Equal([
            OutputDescriptorKind.PaymentToRemote, OutputDescriptorKind.RevokedToLocal,
            OutputDescriptorKind.RevokedHtlc, OutputDescriptorKind.RevokedHtlc
        ], _store.Outputs.Values.Select(o => o.Descriptor).Order());
        _revocationLog.Verify(l => l.GetAsync(_channel.ChannelId, splice.FundingTxId, revoked.Number), Times.Once);
    }

    [Fact]
    public async Task Given_ARevokedCommitmentOfADiscardedSplice_When_ItConfirms_Then_PenalizedWithThatFundingsLog()
    {
        // Arrange (SP2-C-T3, SP-I5): a splice was discarded (a reorg left its funding output spendable); the peer
        // broadcasts a commitment it revoked on that funding. Its own log row carries the HTLCs.
        var revoked = _pair.Alice.State.RemoteCommit;
        _pair.Add(_pair.Alice, 5_000_000, RealSigningCommitmentPair.Preimage(3));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _storedFundings.Add(Current);
        var discarded = Splice(TxIdOf(0xD4), 1) with { Status = ChannelFundingStatus.Discarded };
        _storedFundings.Add(discarded);
        var onDiscarded = ChannelCommitments.SpecFor(revoked.Spec, discarded);
        _revocationLog.Setup(l => l.GetAsync(_channel.ChannelId, discarded.FundingTxId, revoked.Number))
                      .ReturnsAsync(new RevokedCommitmentModel(_channel.ChannelId, revoked.Number, onDiscarded)
                      {
                          FundingTxId = discarded.FundingTxId
                      });
        var secret = _pair.Bob.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, revoked.Number);
        var shachain = new Mock<ISecretStorageService>();
        shachain.Setup(s => s.DeriveOldSecret(It.IsAny<ulong>())).Returns(secret);
        _shachainFactory.Setup(f => f.CreatePerCommitmentStorage()).Returns(shachain.Object);
        var spend = BuildCommitment(CommitmentSide.Remote, onDiscarded, revoked.Number, revoked.PerCommitmentPoint,
                                    discarded);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend, discarded),
                                                            TestContext.Current.CancellationToken);

        // Assert: every output of the discarded funding's commitment to penalize, found by txid
        Assert.Equal(ChannelCloseKind.RevokedCommitment, outcome!.Kind);
        Assert.Equal([
            OutputDescriptorKind.PaymentToRemote, OutputDescriptorKind.RevokedToLocal,
            OutputDescriptorKind.RevokedHtlc, OutputDescriptorKind.RevokedHtlc
        ], _store.Outputs.Values.Select(o => o.Descriptor).Order());
        _revocationLog.Verify(l => l.GetAsync(_channel.ChannelId, revoked.Number), Times.Never);
    }

    [Fact]
    public async Task Given_OurCommitmentOnThePendingSplice_When_ItConfirms_Then_LocalCloseMappedOnThatFunding()
    {
        // Arrange (SP2-C-T3): the splice confirmed and our commitment on it (same number, SP-I4) spends its output
        var splice = AddPendingSplice(TxIdOf(0xC7), 1);
        var local = _pair.Alice.State.LocalCommit;
        var spec = ChannelCommitments.SpecFor(local.Spec, splice);
        StoreLocalOnSplice(splice, new LocalCommit(local.Number, spec, local.RemoteSignatures));
        var spend = BuildCommitment(CommitmentSide.Local, spec, local.Number, null, splice);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend, splice),
                                                            TestContext.Current.CancellationToken);

        // Assert: to_local (with the splice's extra 250,000 sat) and both HTLC outputs, all matched by txid
        Assert.NotNull(outcome);
        Assert.Equal(ChannelCloseKind.LocalCommitment, outcome.Kind);
        var close = Assert.Single(_store.Closes.Values);
        Assert.Equal(spend.TxId, close.CommitmentTransactionId);
        Assert.Equal(local.Number, close.CommitmentNumber);
        Assert.Equal([
            OutputDescriptorKind.DelayedToLocal, OutputDescriptorKind.LocalOfferedHtlc,
            OutputDescriptorKind.LocalReceivedHtlc
        ], _store.Outputs.Values.Select(o => o.Descriptor).Order());
        Assert.All(_store.Outputs.Values, o => Assert.Equal(spend.TxId, o.TransactionId));
        var toLocal = _store.Outputs.Values.Single(o => o.Descriptor == OutputDescriptorKind.DelayedToLocal);
        var onCurrent = (ulong)_pair.Alice.State.LocalCommit.Spec.LocalMsat / 1_000;
        Assert.True(OutputDescriptorData.Decode(toLocal.DescriptorData).AmountSat > onCurrent);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
    }

    [Fact]
    public async Task Given_ThePeersCommitmentOnThePendingSplice_When_ItConfirms_Then_RemoteCloseOnThatFunding()
    {
        // Arrange
        var splice = AddPendingSplice(TxIdOf(0xC8), 1);
        var remote = _pair.Alice.State.RemoteCommit;
        var spec = ChannelCommitments.SpecFor(remote.Spec, splice);
        StoreRemoteOnSplice(splice, remote with { Spec = spec });
        var spend = BuildCommitment(CommitmentSide.Remote, spec, remote.Number, remote.PerCommitmentPoint, splice);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend, splice),
                                                            TestContext.Current.CancellationToken);

        // Assert: our to_remote and both HTLC outputs of the peer's commitment on the splice funding
        Assert.NotNull(outcome);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, outcome.Kind);
        Assert.Equal(new[]
        {
            OutputDescriptorKind.PaymentToRemote, OutputDescriptorKind.RemoteOfferedHtlc,
            OutputDescriptorKind.RemoteReceivedHtlc
        }.Order(), _store.Outputs.Values.Select(o => o.Descriptor).Order());
        var toRemote = _store.Outputs.Values.Single(o => o.Descriptor == OutputDescriptorKind.PaymentToRemote);
        Assert.True(OutputDescriptorData.Decode(toRemote.DescriptorData).AmountSat > remote.Spec.LocalMsat / 1_000,
                    "our to_remote must carry the splice-in");
    }

    [Theory]
    [InlineData(BroadcastPurpose.Splice)]
    [InlineData(BroadcastPurpose.Funding)] // a splice row saved before NL-626 gave splices their own purpose
    public async Task Given_OurCommitmentOnTheCurrentFunding_When_ASpliceIsPending_Then_TheSpliceIsDiscarded(
        BroadcastPurpose splicePurpose)
    {
        // Arrange (§3.6: "a commitment of the current funding confirms while a splice is pending")
        var spliceTx = BuildSpliceTransaction();
        var splice = AddPendingSplice(spliceTx.TxId, 0);
        var spliceBroadcast = new BroadcastTransactionModel(spliceTx, splicePurpose, _channel.ChannelId,
                                                            SpendHeight - 5);
        _store.Broadcasts.Add(spliceBroadcast);
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null, null);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend, Current),
                                                            TestContext.Current.CancellationToken);

        // Assert: the close as usual, and in the same save the splice discarded and its broadcast abandoned
        Assert.Equal(ChannelCloseKind.LocalCommitment, outcome!.Kind);
        Assert.Equal(BroadcastState.Abandoned, spliceBroadcast.State);
        _fundings.Verify(f => f.UpsertAsync(_channel.ChannelId, It.Is<ChannelFunding>(
                                                d => d.FundingTxId == splice.FundingTxId
                                                  && d.Status == ChannelFundingStatus.Discarded)), Times.Once);
        var save = Assert.Single(_store.Saves);
        Assert.Contains("close", save);
        Assert.Contains("broadcast abandoned", save);
    }

    [Fact]
    public async Task Given_ARevokedCommitmentOfTheReplacedFunding_When_ItConfirms_Then_PenalizedWithThatFundingsLog()
    {
        // Arrange (SP-I5, NL-479): the splice locked; the peer broadcasts a commitment it revoked on the old funding
        // (after a reorg unspent it). The old funding's log entry has other balances than the new one's.
        var old = Current;
        var revoked = _pair.Alice.State.RemoteCommit;
        _pair.Add(_pair.Alice, 5_000_000, RealSigningCommitmentPair.Preimage(3));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        var locked = Splice(TxIdOf(0xC9), 0) with
        {
            Status = ChannelFundingStatus.Current,
            LocalBalanceDeltaMsat = 0
        };
        _channel.ReplaceFundingOutput(new Domain.Bitcoin.Transactions.Outputs.FundingOutputInfo(
                                          Domain.Money.LightningMoney.Satoshis(locked.CapacitySatoshis),
                                          locked.LocalFundingPubKey, locked.RemoteFundingPubKey, locked.FundingTxId,
                                          locked.OutputIndex));
        _storedFundings.Add(old with { Status = ChannelFundingStatus.Replaced });
        _storedFundings.Add(locked);
        _revocationLog.Setup(l => l.GetAsync(_channel.ChannelId, old.FundingTxId, revoked.Number))
                      .ReturnsAsync(RevokedCommitmentModel.From(_channel.ChannelId, revoked) with
                      {
                          FundingTxId = old.FundingTxId
                      });
        var secret = _pair.Bob.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, revoked.Number);
        var shachain = new Mock<ISecretStorageService>();
        shachain.Setup(s => s.DeriveOldSecret(It.IsAny<ulong>())).Returns(secret);
        _shachainFactory.Setup(f => f.CreatePerCommitmentStorage()).Returns(shachain.Object);
        var spend = BuildCommitment(CommitmentSide.Remote, revoked.Spec, revoked.Number, revoked.PerCommitmentPoint,
                                    old);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(spend, old), TestContext.Current.CancellationToken);

        // Assert: every output of the old funding's commitment to penalize, found by txid (nothing unmapped)
        Assert.Equal(ChannelCloseKind.RevokedCommitment, outcome!.Kind);
        Assert.Equal(revoked.Number, _store.Closes[_channel.ChannelId].CommitmentNumber);
        Assert.Equal([
            OutputDescriptorKind.PaymentToRemote, OutputDescriptorKind.RevokedToLocal,
            OutputDescriptorKind.RevokedHtlc, OutputDescriptorKind.RevokedHtlc
        ], _store.Outputs.Values.Select(o => o.Descriptor).Order());
        _revocationLog.Verify(l => l.GetAsync(_channel.ChannelId, old.FundingTxId, revoked.Number), Times.Once);
        _revocationLog.Verify(l => l.GetAsync(_channel.ChannelId, revoked.Number), Times.Never);
    }

    [Fact]
    public async Task Given_ASpendOfAnOutpointThatIsNoFunding_When_Raised_Then_Ignored()
    {
        // Arrange
        AddPendingSplice(TxIdOf(0xCA), 1);
        var local = _pair.Alice.State.LocalCommit;
        var spend = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null, null);
        var args = new OutpointSpentEventArgs(_channel.ChannelId, spend, SpendHeight, 1,
                                              TxIdOf(0xCB), 0,
                                              OnchainTestStore.BlockHash(1));

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(args, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(outcome);
        Assert.Empty(_store.Closes);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    private static TxId TxIdOf(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static ChannelFunding Splice(TxId txId, ushort vout) =>
        new(txId, vout, RealSigningCommitmentPair.FundingSatoshis + 250_000, TestKeys.Local, TestKeys.Remote, 1,
            SpliceInMsat, 0, ChannelFundingKind.Splice, ChannelFundingStatus.Pending, 2_500, 0);

    private ChannelFunding AddPendingSplice(TxId txId, ushort vout)
    {
        var splice = Splice(txId, vout);
        if (_storedFundings.Count == 0)
            _storedFundings.Add(Current);
        _storedFundings.Add(splice);
        return splice;
    }

    private void StoreLocalOnSplice(ChannelFunding splice, LocalCommit local) =>
        _fundings.Setup(f => f.GetLocalCommitmentAsync(_channel.ChannelId, splice.FundingTxId)).ReturnsAsync(local);

    private void StoreRemoteOnSplice(ChannelFunding splice, RemoteCommit remote) =>
        _fundings.Setup(f => f.GetRemoteCommitmentAsync(_channel.ChannelId, splice.FundingTxId))
                 .ReturnsAsync((remote, (CommitmentSignatures?)null));

    private OutpointSpentEventArgs SpentBy(SignedTransaction spend, ChannelFunding funding) =>
        new(_channel.ChannelId, spend, SpendHeight, 1, funding.FundingTxId, funding.OutputIndex,
            OnchainTestStore.BlockHash(1));

    /// <summary>A splice transaction: the current funding output and a wallet input to a new 2-of-2 output.</summary>
    private SignedTransaction BuildSpliceTransaction()
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(_channel.FundingOutput!.TransactionId!.Value),
                                            _channel.FundingOutput.Index!.Value));
        transaction.Inputs.Add(new OutPoint(uint256.One, 3));
        transaction.Outputs.Add(Money.Satoshis(1_250_000), new Key().PubKey.WitHash.ScriptPubKey);
        return new SignedTransaction(new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes());
    }

    /// <summary>A commitment of the channel on <paramref name="funding"/> (null: the current one), unsigned.</summary>
    private SignedTransaction BuildCommitment(CommitmentSide side, CommitmentSpec spec, ulong number,
                                              CompactPubKey? remotePoint, ChannelFunding? funding)
    {
        var factory = _provider.GetRequiredService<ICommitmentTransactionModelFactory>();
        var builder = _provider.GetRequiredService<ICommitmentTransactionBuilder>();
        var txSpec = CommitmentTxSpec.FromCommitmentSpec(spec);
        var model = side == CommitmentSide.Local
                        ? factory.CreateCommitmentTransactionModel(_channel, txSpec, CommitmentSide.Local, number)
                        : factory.CreateCommitmentTransactionModel(_channel, txSpec, CommitmentSide.Remote, number,
                                                                   remotePoint);
        if (funding is not null)
            model = CommitmentSigningService.WithFunding(model, funding, side);
        return builder.BuildWithOutputMap(model).Transaction;
    }

    /// <summary>Funding keys of the splice (valid points; the 2-of-2 script is not part of a commitment's txid).</summary>
    private static class TestKeys
    {
        public static readonly CompactPubKey Local =
            Convert.FromHexString("0394854aa6eab5b2a8122cc726e9dded053a2184d88256816826d6231c068d4a5b");

        public static readonly CompactPubKey Remote =
            Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");
    }
}
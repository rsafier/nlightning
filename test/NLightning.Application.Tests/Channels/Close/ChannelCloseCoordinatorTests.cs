using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Close;

using Application.Channels.Close;
using Application.Channels.Safety.Interfaces;
using Application.Channels.Services;
using Application.Protocol.Factories;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Closing;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Domain.Serialization.Interfaces;
using Handlers;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// <see cref="ChannelCloseCoordinator"/> rules that the two-node harness can't reach (BOLT2 plan §6.10/§6.11): the
/// upfront shutdown script (B2-SHUT-R05), shutdown before channel_ready (B2-SHUT-R03) and after the close is out,
/// outputs below their script's dust threshold (B2-CLS-R10), a fee above the funder's balance, persist-before-broadcast
/// (I1) and the shutdown re-send (B2-RE-28). Channels here have no commitment snapshot, so the balances are the
/// channel's; the signer is mocked.
/// </summary>
public class ChannelCloseCoordinatorTests
{
    private static readonly BitcoinScript s_localScript = Convert.FromHexString("0014" + new string('1', 40));
    private static readonly BitcoinScript s_remoteScript = Convert.FromHexString("0020" + new string('2', 64));
    private static readonly CompactSignature s_signature =
        new(new Key(Enumerable.Repeat((byte)0x42, 32).ToArray()).Sign(uint256.One).MakeCanonical().ToCompact());

    private readonly Mock<IChannelDbRepository> _channelDb = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<ILightningSigner> _signer = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IWatchedTransactionDbRepository> _watchedDb = new();
    private readonly List<string> _calls = [];
    private readonly ClosingNegotiationRegistry _registry = new();

    public ChannelCloseCoordinatorTests()
    {
        _unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_channelDb.Object);
        _channelDb.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                  .Callback((ChannelModel c) => _calls.Add($"update:{c.State}"))
                  .Returns(Task.CompletedTask);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => _calls.Add("save")).Returns(Task.CompletedTask);
        _signer.Setup(s => s.SignChannelTransaction(It.IsAny<ChannelId>(), It.IsAny<SignedTransaction>()))
               .Returns(s_signature);
        _unitOfWork.SetupGet(u => u.WatchedTransactionDbRepository).Returns(_watchedDb.Object);
        _watchedDb.Setup(r => r.Add(It.IsAny<WatchedTransactionModel>()))
                  .Callback((WatchedTransactionModel w) => _calls.Add($"watch:{w.RequiredDepth}"));
        _monitor.Setup(m => m.TrackWatchedTransaction(It.IsAny<WatchedTransactionModel>()))
                .Callback(() => _calls.Add("track"));
        _monitor.Setup(m => m.PublishTransactionAsync(It.IsAny<SignedTransaction>()))
                .Callback(() => _calls.Add("publish"))
                .Returns(Task.CompletedTask);
    }

    [Theory]
    [InlineData(ChannelState.V1FundingSigned)]
    [InlineData(ChannelState.ReadyForThem)]
    [InlineData(ChannelState.ReadyForUs)]
    public async Task Given_ChannelNotOpenYet_When_Shutdown_Then_WarningAndNothingPersisted(ChannelState state)
    {
        // Arrange (B2-SHUT-R03 is a MAY: not supported before channel_ready)
        var channel = CreateChannel(state);

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => CreateCoordinator().ReceiveShutdownAsync(channel, Shutdown(s_remoteScript),
                                                                         new FeatureOptions()));

        // Assert
        Assert.False(warning.CloseConnection);
        Assert.Null(channel.RemoteShutdownScript);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Given_ClosingChannel_When_Shutdown_Then_Ignored()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.Closing);

        // Act
        var replies = await CreateCoordinator().ReceiveShutdownAsync(channel, Shutdown(s_remoteScript),
                                                                     new FeatureOptions());

        // Assert
        Assert.Empty(replies);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Given_UpfrontScript_When_ShutdownWithAnotherScript_Then_WarningAndDisconnect(
        bool negotiated, bool accepted)
    {
        // Arrange (B2-SHUT-R05: only binding when option_upfront_shutdown_script was negotiated)
        var upfront = (BitcoinScript)Convert.FromHexString("0014" + new string('9', 40));
        var channel = CreateChannel(ChannelState.Open, remoteUpfront: upfront);
        var features = new FeatureOptions
        {
            UpfrontShutdownScript = negotiated ? FeatureSupport.Optional : FeatureSupport.No
        };

        // Act
        var exception = await Record.ExceptionAsync(
                            () => CreateCoordinator().ReceiveShutdownAsync(channel, Shutdown(s_remoteScript),
                                                                           features));

        // Assert
        if (accepted)
        {
            Assert.Null(exception);
            Assert.Equal(s_remoteScript, channel.RemoteShutdownScript);
        }
        else
        {
            var warning = Assert.IsType<ChannelWarningException>(exception);
            Assert.True(warning.CloseConnection);
            Assert.Contains("B2-SHUT-R05", warning.Message);
            Assert.Null(channel.RemoteShutdownScript);
        }
    }

    [Fact]
    public async Task Given_UpfrontScript_When_ShutdownWithIt_Then_AcceptedAndReplied()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.Open, remoteUpfront: s_remoteScript);
        var features = new FeatureOptions { UpfrontShutdownScript = FeatureSupport.Optional };

        // Act
        var replies = await CreateCoordinator().ReceiveShutdownAsync(channel, Shutdown(s_remoteScript), features);

        // Assert: our shutdown was persisted (with ShuttingDown) before it is returned
        var reply = Assert.IsType<ShutdownMessage>(Assert.Single(replies));
        Assert.Equal(s_localScript, reply.Payload.ScriptPubkey);
        Assert.Equal(ChannelState.ShuttingDown, channel.State);
        Assert.Equal(["update:ShuttingDown", "save", "update:ShuttingDown", "save"], _calls);
        // From the first shutdown on, the peer can broadcast any proposal we sign: the funding output is watched
        _monitor.Verify(m => m.WatchOutpointSpend(channel.ChannelId, channel.FundingOutput!.TransactionId!.Value, 0),
                        Times.AtLeastOnce);
    }

    [Fact]
    public async Task Given_OurUpfrontScript_When_Initiate_Then_ItIsTheShutdownScript()
    {
        // Arrange (B2-SHUT-S09: reuse the upfront script we sent)
        var ourUpfront = (BitcoinScript)Convert.FromHexString("0020" + new string('7', 64));
        var channel = CreateChannel(ChannelState.Open, localUpfront: ourUpfront);
        var provider = new ShutdownScriptProvider(Options.Create(new NodeOptions()),
                                                  new Mock<IBitcoinWalletService>(MockBehavior.Strict).Object);

        // Act
        var messages = await CreateCoordinator(provider).InitiateAsync(channel, new ChannelCloseRequest());

        // Assert
        var shutdown = Assert.IsType<ShutdownMessage>(Assert.Single(messages));
        Assert.Equal(ourUpfront, shutdown.Payload.ScriptPubkey);
        Assert.Equal(ourUpfront, channel.LocalShutdownScript);
    }

    [Fact]
    public async Task Given_NoUpfrontScript_When_GetLocalScript_Then_WalletAddressIsWatchedAgain()
    {
        // Arrange: the wallet reuses an address whose deposit was spent; the monitor stopped watching it after that
        // deposit, so the closing output would never reach the wallet (found by the Docker close proof)
        var key = new Key();
        var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
        var walletAddress = new WalletAddressModel(AddressType.P2Wpkh, 0, false, address);
        var wallet = new Mock<IBitcoinWalletService>();
        wallet.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, false)).ReturnsAsync(walletAddress);
        var monitor = new Mock<IBlockchainMonitor>();
        var provider = new ShutdownScriptProvider(Options.Create(new NodeOptions()), wallet.Object, monitor.Object);

        // Act
        var script = await provider.GetLocalScriptAsync(CreateChannel(ChannelState.Open));

        // Assert
        Assert.Equal(key.PubKey.WitHash.ScriptPubKey.ToBytes(), (byte[])script);
        monitor.Verify(m => m.WatchBitcoinAddress(walletAddress), Times.Once);
    }

    [Theory]
    [InlineData(ChannelState.ShuttingDown, true)]
    [InlineData(ChannelState.Negotiating, true)]
    [InlineData(ChannelState.Closing, true)]
    [InlineData(ChannelState.Closed, false)]
    [InlineData(ChannelState.Failed, false)]
    public async Task Given_AnotherCloseUsesTheUnusedAddress_When_GetLocalScript_Then_ChangeAddressUsed(
        ChannelState otherState, bool expectChange)
    {
        // Arrange (NL-280, W3 review F3): the wallet hands out its first address without a UTXO to every caller, so a
        // second concurrent close would pay to the same address and link the two channels on chain
        var receive = WalletAddress(0, false, out var receiveScript);
        var change = WalletAddress(0, true, out var changeScript);
        var wallet = new Mock<IBitcoinWalletService>();
        wallet.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, false)).ReturnsAsync(receive);
        wallet.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, true)).ReturnsAsync(change);
        var other = CreateChannel(otherState, isInitiator: false, channelIdTag: 0x21);
        other.SetLocalShutdownScript(receiveScript);
        var channel = CreateChannel(ChannelState.Open);
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
              .Returns((Func<ChannelModel, bool> predicate) => new[] { other, channel }.Where(predicate).ToList());
        var monitor = new Mock<IBlockchainMonitor>();
        var provider = new ShutdownScriptProvider(Options.Create(new NodeOptions()), wallet.Object, monitor.Object,
                                                  memory.Object);

        // Act
        var script = await provider.GetLocalScriptAsync(channel);

        // Assert
        Assert.Equal(expectChange ? changeScript : receiveScript, script);
        monitor.Verify(m => m.WatchBitcoinAddress(expectChange ? change : receive), Times.Once);
    }

    [Fact]
    public async Task Given_BothUnusedAddressesTaken_When_GetLocalScript_Then_ReceiveAddressKept()
    {
        // Arrange: two other closes already use both first unused addresses (the wallet can't give a third yet)
        var receive = WalletAddress(0, false, out var receiveScript);
        var change = WalletAddress(0, true, out var changeScript);
        var wallet = new Mock<IBitcoinWalletService>();
        wallet.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, false)).ReturnsAsync(receive);
        wallet.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, true)).ReturnsAsync(change);
        var first = CreateChannel(ChannelState.Negotiating, isInitiator: false, channelIdTag: 0x21);
        first.SetLocalShutdownScript(receiveScript);
        var second = CreateChannel(ChannelState.ShuttingDown, isInitiator: false, channelIdTag: 0x22);
        second.SetLocalShutdownScript(changeScript);
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
              .Returns((Func<ChannelModel, bool> predicate) => new[] { first, second }.Where(predicate).ToList());
        var provider = new ShutdownScriptProvider(Options.Create(new NodeOptions()), wallet.Object, null,
                                                  memory.Object);

        // Act
        var script = await provider.GetLocalScriptAsync(CreateChannel(ChannelState.Open));

        // Assert
        Assert.Equal(receiveScript, script);
    }

    [Fact]
    public async Task Given_TwoConcurrentClosesBeforeEitherScriptIsStored_When_GetLocalScript_Then_DifferentAddresses()
    {
        // Arrange (NL-280 partial, W4-E review F4): two closes on different channels (different locks) ask the wallet
        // before either stores its shutdown script, so the memory check alone sees nothing; the registry's
        // process-wide reservation tells them apart
        var receive = WalletAddress(0, false, out var receiveScript);
        var change = WalletAddress(0, true, out var changeScript);
        var wallet = new Mock<IBitcoinWalletService>();
        wallet.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, false)).ReturnsAsync(receive);
        wallet.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, true)).ReturnsAsync(change);
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        ShutdownScriptProvider Provider() =>
            new(Options.Create(new NodeOptions()), wallet.Object, null, memory.Object, null, _registry);
        var first = CreateChannel(ChannelState.Open, channelIdTag: 0x21);
        var second = CreateChannel(ChannelState.Open, channelIdTag: 0x22);

        // Act
        var scripts = await Task.WhenAll(Provider().GetLocalScriptAsync(first),
                                         Provider().GetLocalScriptAsync(second));
        var firstAgain = await Provider().GetLocalScriptAsync(first);

        // Assert: one gets the receive address, the other the change address; a channel keeps its own claim
        Assert.Equal(new[] { receiveScript, changeScript }.OrderBy(s => s.ToString()),
                     scripts.OrderBy(s => s.ToString()));
        Assert.Equal(scripts[0], firstAgain);
    }

    [Fact]
    public async Task Given_ClosedChannelsReservation_When_Removed_Then_AddressFreeAgain()
    {
        // Arrange
        var receive = WalletAddress(0, false, out var receiveScript);
        var wallet = new Mock<IBitcoinWalletService>();
        wallet.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, It.IsAny<bool>())).ReturnsAsync(receive);
        var provider = new ShutdownScriptProvider(Options.Create(new NodeOptions()), wallet.Object, null, null, null,
                                                  _registry);
        var closed = CreateChannel(ChannelState.Open, channelIdTag: 0x21);
        await provider.GetLocalScriptAsync(closed);

        // Act
        _registry.Remove(closed.ChannelId);

        // Assert
        Assert.True(_registry.TryReserveShutdownScript(CreateChannel(ChannelState.Open, channelIdTag: 0x22).ChannelId,
                                                       receiveScript));
    }

    [Fact]
    public async Task Given_EstimatorHangs_When_NegotiationUnderLock_Then_WaitsOnceBrieflyAndUsesCachedFee()
    {
        // Arrange (W4-E review F3): the estimate fetch runs under the channel lock, in the peer's inbound loop; an
        // unreachable estimator must neither stall it for the HttpClient timeout nor be retried on every message
        var hanging = new TaskCompletionSource<LightningMoney>();
        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetCachedFeeRatePerKw()).Returns(LightningMoney.Satoshis(1_000));
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>())).Returns(hanging.Task);
        var options = Options.Create(new ChannelCloseOptions { FeeEstimateWaitUnderLock = TimeSpan.FromMilliseconds(50) });
        var estimator = new ClosingFeeEstimator(feeService.Object, options, NullLogger<ClosingFeeEstimator>.Instance);
        var channel = CreateFunderReadyToPropose();
        var coordinator = CreateCoordinator(feeService: feeService.Object, feeEstimator: estimator);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act: the opening proposal, then two answers of the peer at a fee we accept as a counter-proposal
        var proposal = Assert.IsType<ClosingSignedMessage>(Assert.Single(await coordinator.AdvanceAsync(channel)));
        for (var i = 0; i < 2; i++)
        {
            try
            {
                await coordinator.ReceiveClosingSignedAsync(channel, ClosingSigned(2_000 + (ulong)i));
            }
            catch (ChannelWarningException)
            {
                // The fake signature is valid for no transaction; only the wait matters here
            }
        }

        stopwatch.Stop();

        // Assert: one fetch, started once; the proposal used the cached 1,000 sat/kw
        feeService.Verify(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
        var weight = ClosingFeeCalculator.EstimateWeight(s_localScript.Length, s_remoteScript.Length);
        Assert.Equal(LightningMoney.Satoshis(ClosingFeeCalculator.FeeSat(1_000, weight)), proposal.Payload.FeeAmount);
        hanging.SetResult(LightningMoney.Satoshis(2_500));
    }

    private static WalletAddressModel WalletAddress(uint index, bool isChange, out BitcoinScript script)
    {
        var key = new Key();
        script = key.PubKey.WitHash.ScriptPubKey.ToBytes();
        return new WalletAddressModel(AddressType.P2Wpkh, index, isChange,
                                      key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString());
    }

    [Fact]
    public async Task Given_ShutdownSent_When_InitiateAgain_Then_NothingSent()
    {
        // Arrange (B2-SHUT-S04: only once)
        var channel = CreateChannel(ChannelState.Open);
        var coordinator = CreateCoordinator();
        await coordinator.InitiateAsync(channel, new ChannelCloseRequest());
        _calls.Clear();

        // Act
        var messages = await coordinator.InitiateAsync(channel, new ChannelCloseRequest());

        // Assert
        Assert.Empty(messages);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Given_OutputBelowItsScriptDust_When_ClosingSigned_Then_ChannelFailedAndNothingBroadcast()
    {
        // Arrange (B2-CLS-R10): dust limits of 300 sat keep a 320 sat P2WSH output, below its 330 sat threshold
        var channel = CreateNegotiatingChannel(localSat: 99_680, remoteSat: 320, dustLimitSat: 300);
        _registry.Get(channel.ChannelId).FeeRangeDueAt = DateTimeOffset.UtcNow.AddMinutes(5);

        // Act
        var failure = await Assert.ThrowsAsync<ChannelFailedException>(
                          () => CreateCoordinator().ReceiveClosingSignedAsync(channel, ClosingSigned(500)));

        // Assert: the channel leaves the negotiation, so no closing deadline is left to fire (W4-E review F2)
        Assert.Equal("B2-CLS-R10", failure.RequirementId);
        Assert.Null(_registry.Get(channel.ChannelId).FeeRangeDueAt);
        Assert.Null(_registry.Get(channel.ChannelId).ReplyDueAt);
        Assert.Equal(ChannelState.Negotiating, channel.State);
        _monitor.Verify(m => m.PublishTransactionAsync(It.IsAny<SignedTransaction>()), Times.Never);
        _watchedDb.Verify(r => r.Add(It.IsAny<WatchedTransactionModel>()), Times.Never);
    }

    [Fact]
    public async Task Given_FeeAboveFunderBalance_When_ClosingSigned_Then_WarningAndDisconnect()
    {
        // Arrange
        var channel = CreateNegotiatingChannel(localSat: 1_000, remoteSat: 99_000, dustLimitSat: 546);

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => CreateCoordinator().ReceiveClosingSignedAsync(channel, ClosingSigned(1_001)));

        // Assert
        Assert.True(warning.CloseConnection);
    }

    [Fact]
    public async Task Given_InvertedFeeRange_When_ClosingSigned_Then_WarningAndDisconnect()
    {
        // Arrange
        var channel = CreateNegotiatingChannel(localSat: 60_000, remoteSat: 40_000, dustLimitSat: 546);
        var message = new ClosingSignedMessage(
            new ClosingSignedPayload(channel.ChannelId, LightningMoney.Satoshis(500), s_signature),
            new FeeRangeTlv(LightningMoney.Satoshis(900), LightningMoney.Satoshis(100)));

        // Act
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => CreateCoordinator().ReceiveClosingSignedAsync(channel, message));

        // Assert
        Assert.True(warning.CloseConnection);
    }

    [Fact]
    public async Task Given_AgreedFee_When_ClosingSigned_Then_PersistedClosingBeforeBroadcastAndEcho()
    {
        // Arrange (I1): we are the non-funder, the funder offers 500 sat without a range; we agree (B2-CLS-R08)
        var channel = CreateNegotiatingChannel(localSat: 40_000, remoteSat: 60_000, dustLimitSat: 546,
                                               isInitiator: false);

        // Act
        var replies = await CreateCoordinator().ReceiveClosingSignedAsync(channel, ClosingSigned(500));

        // Assert
        var echo = Assert.IsType<ClosingSignedMessage>(Assert.Single(replies));
        Assert.Equal(LightningMoney.Satoshis(500), echo.Payload.FeeAmount);
        Assert.Null(echo.FeeRangeTlv);
        Assert.Equal(ChannelState.Closing, channel.State);
        // The watch is staged in the same (single) save as Closing, so no crash leaves Closing without it
        Assert.Equal(["watch:6", "update:Closing", "save", "track", "publish"], _calls);
        _watchedDb.Verify(r => r.Add(It.Is<WatchedTransactionModel>(w => w.TransactionId == channel.ClosingTransaction!.TxId
                                                                     && w.ChannelId == channel.ChannelId)),
                          Times.Once);
        var tx = Transaction.Load(channel.ClosingTransaction!.RawTxBytes, Network.RegTest);
        Assert.Equal(40_000, tx.Outputs.Single(o => o.ScriptPubKey.ToBytes().SequenceEqual((byte[])s_localScript))
                                .Value.Satoshi);
        Assert.Equal(59_500, tx.Outputs.Single(o => o.ScriptPubKey.ToBytes().SequenceEqual((byte[])s_remoteScript))
                                .Value.Satoshi);
        Assert.Equal(4, tx.Inputs[0].WitScript.PushCount);
    }

    [Fact]
    public async Task Given_BroadcastFails_When_Agreed_Then_StillClosing()
    {
        // Arrange: the peer broadcast first (already in the mempool)
        var channel = CreateNegotiatingChannel(localSat: 40_000, remoteSat: 60_000, dustLimitSat: 546,
                                               isInitiator: false);
        _monitor.Setup(m => m.PublishTransactionAsync(It.IsAny<SignedTransaction>()))
                .ThrowsAsync(new InvalidOperationException("txn-already-in-mempool"));

        // Act
        var replies = await CreateCoordinator().ReceiveClosingSignedAsync(channel, ClosingSigned(500));

        // Assert
        Assert.Single(replies);
        Assert.Equal(ChannelState.Closing, channel.State);
    }

    [Fact]
    public async Task Given_Closing_When_PeerRestartsNegotiation_Then_AnsweredWithTheAgreedFee()
    {
        // Arrange: we agreed at 500 sat; our echo was lost and the peer starts over after a reconnection
        var channel = CreateNegotiatingChannel(localSat: 40_000, remoteSat: 60_000, dustLimitSat: 546,
                                               isInitiator: false);
        var coordinator = CreateCoordinator();
        await coordinator.ReceiveClosingSignedAsync(channel, ClosingSigned(500));
        var closingTx = channel.ClosingTransaction!;
        _calls.Clear();

        // Act
        var replies = await coordinator.ReceiveClosingSignedAsync(channel, ClosingSigned(800));

        // Assert: our signature of the stored transaction at its fee; nothing persisted or broadcast again
        var reply = Assert.IsType<ClosingSignedMessage>(Assert.Single(replies));
        Assert.Equal(LightningMoney.Satoshis(500), reply.Payload.FeeAmount);
        Assert.Null(reply.FeeRangeTlv);
        _signer.Verify(s => s.SignChannelTransaction(channel.ChannelId,
                                                     It.Is<SignedTransaction>(t => t.TxId == closingTx.TxId)),
                       Times.Exactly(2));
        Assert.Equal(ChannelState.Closing, channel.State);
        Assert.Same(closingTx, channel.ClosingTransaction);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Given_Closing_When_PeerSendsTheAgreedFeeTwice_Then_AnsweredOncePerConnection()
    {
        // Arrange: our echo was lost and the peer (same estimate) restarts with the agreed fee
        var channel = CreateNegotiatingChannel(localSat: 40_000, remoteSat: 60_000, dustLimitSat: 546,
                                               isInitiator: false);
        var coordinator = CreateCoordinator();
        await coordinator.ReceiveClosingSignedAsync(channel, ClosingSigned(500));
        _calls.Clear();

        // Act
        var first = await coordinator.ReceiveClosingSignedAsync(channel, ClosingSigned(500));
        var second = await coordinator.ReceiveClosingSignedAsync(channel, ClosingSigned(500));
        _registry.ResetConnection(channel.ChannelId);
        var afterReconnect = await coordinator.ReceiveClosingSignedAsync(channel, ClosingSigned(500));

        // Assert: the peer can finish with our answer; a second one would let two Closing nodes echo forever
        Assert.Equal(LightningMoney.Satoshis(500),
                     Assert.IsType<ClosingSignedMessage>(Assert.Single(first)).Payload.FeeAmount);
        Assert.Empty(second);
        Assert.Single(afterReconnect);
        Assert.Empty(_calls);
    }

    [Fact]
    public void Given_ShutdownSent_When_CreateShutdownResend_Then_SameScriptAndMarkedSent()
    {
        // Arrange (B2-RE-28)
        var channel = CreateChannel(ChannelState.ShuttingDown);
        channel.SetLocalShutdownScript(s_localScript);
        var coordinator = CreateCoordinator();

        // Act
        var resend = coordinator.CreateShutdownResend(channel);

        // Assert
        Assert.NotNull(resend);
        Assert.Equal(s_localScript, resend.Payload.ScriptPubkey);
        Assert.True(_registry.Get(channel.ChannelId).ShutdownSentOnConnection);
    }

    [Fact]
    public async Task Given_FeeServiceWithEmptyCache_When_FunderProposes_Then_FeeFromTheFetchedEstimate()
    {
        // Arrange: the host's fee service is a transient typed HttpClient, so the coordinator's instance has an empty
        // cache; reading only the cache made every close propose the 253 sat/kw floor (W4-E, seen against CLN and LND)
        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetCachedFeeRatePerKw()).Returns(LightningMoney.Zero);
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(LightningMoney.Satoshis(2_500));
        var channel = CreateFunderReadyToPropose();
        var coordinator = CreateCoordinator(feeService: feeService.Object);

        // Act
        var proposal = Assert.IsType<ClosingSignedMessage>(Assert.Single(await coordinator.AdvanceAsync(channel)));

        // Assert: 2,500 sat/kw for a P2WPKH and a P2WSH output, and a range up to 3x that (the funder's limit)
        var weight = ClosingFeeCalculator.EstimateWeight(s_localScript.Length, s_remoteScript.Length);
        var expected = ClosingFeeCalculator.FeeSat(2_500, weight);
        Assert.Equal(LightningMoney.Satoshis(expected), proposal.Payload.FeeAmount);
        Assert.NotNull(proposal.FeeRangeTlv);
        Assert.Equal(LightningMoney.Satoshis(expected * 3), proposal.FeeRangeTlv.MaxFeeAmount);
        feeService.Verify(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_OurClosingSignedUnanswered_When_ReplyTimeoutPasses_Then_ChannelFailed()
    {
        // Arrange (B2-CLS-03, NL-284): as the funder we open the negotiation
        var (monitor, clock, failures) = CreateTimeoutMonitor();
        using var _ = monitor;
        var channel = CreateFunderReadyToPropose();
        var sent = await CreateCoordinator(timeouts: monitor).AdvanceAsync(channel);
        Assert.IsType<ClosingSignedMessage>(Assert.Single(sent));

        // Act: one second short of the timeout, then past it
        clock.Advance(new ChannelCloseOptions().ClosingSignedReplyTimeout - TimeSpan.FromSeconds(1));
        await monitor.WhenIdleAsync();
        var failedEarly = failures.Count;
        clock.Advance(TimeSpan.FromSeconds(1));
        await monitor.WhenIdleAsync();

        // Assert: failed (with a broadcast) once, only after the timeout, while still negotiating
        Assert.Equal(0, failedEarly);
        var request = Assert.Single(failures);
        Assert.Equal("B2-CLS-03", request.RequirementId);
        Assert.True(request.Broadcast);
        Assert.Equal(ClosingTimeoutMonitor.NoReplyPeerMessage, request.PeerMessage);
        Assert.NotNull(request.StillApplies);
        Assert.True(request.StillApplies(channel));
    }

    [Fact]
    public async Task Given_OurClosingSignedAnswered_When_ReplyTimeoutPasses_Then_NotFailed()
    {
        // Arrange: the peer echoes our fee (B2-CLS-R02), so the channel is Closing
        var (monitor, clock, failures) = CreateTimeoutMonitor();
        using var _ = monitor;
        var channel = CreateFunderReadyToPropose();
        var coordinator = CreateCoordinator(timeouts: monitor);
        var ours = Assert.IsType<ClosingSignedMessage>(Assert.Single(await coordinator.AdvanceAsync(channel)));
        await coordinator.ReceiveClosingSignedAsync(channel, ClosingSigned((ulong)ours.Payload.FeeAmount.Satoshi));
        Assert.Equal(ChannelState.Closing, channel.State);

        // Act
        clock.Advance(TimeSpan.FromHours(1));
        await monitor.WhenIdleAsync();

        // Assert
        Assert.Empty(failures);
        Assert.Null(_registry.Get(channel.ChannelId).NextDeadline);
    }

    [Fact]
    public async Task Given_OurClosingSignedUnanswered_When_Reconnected_Then_ReplyDeadlineDropped()
    {
        // Arrange: BOLT 2 restarts the negotiation on reconnection (B2-RE-29); the peer being away is no reason to
        // fail the channel
        var (monitor, clock, failures) = CreateTimeoutMonitor();
        using var _ = monitor;
        var channel = CreateFunderReadyToPropose();
        await CreateCoordinator(timeouts: monitor).AdvanceAsync(channel);

        // Act
        _registry.ResetConnection(channel.ChannelId);
        clock.Advance(TimeSpan.FromHours(1));
        await monitor.WhenIdleAsync();

        // Assert
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Given_FeeRangeWithoutOverlap_When_NoBetterRangeInTime_Then_WarnedThenChannelFailed()
    {
        // Arrange (B2-CLS-R04, NL-284): as the funder our range tops out at 3x our ~724 sat estimate; the peer asks
        // for 5000..6000 sat
        var (monitor, clock, failures) = CreateTimeoutMonitor();
        using var _ = monitor;
        var channel = CreateNegotiatingChannel(localSat: 60_000, remoteSat: 40_000, dustLimitSat: 546);
        var coordinator = CreateCoordinator(timeouts: monitor);
        var noOverlap = new ClosingSignedMessage(
            new ClosingSignedPayload(channel.ChannelId, LightningMoney.Satoshis(5_000), s_signature),
            new FeeRangeTlv(LightningMoney.Satoshis(5_000), LightningMoney.Satoshis(6_000)));

        // Act 1: the warning (SHOULD), the connection stays up
        var warning = await Assert.ThrowsAsync<ChannelWarningException>(
                          () => coordinator.ReceiveClosingSignedAsync(channel, noOverlap));
        var dueAt = _registry.Get(channel.ChannelId).FeeRangeDueAt;

        // Act 2: the same range again, and a reconnection, do not move the deadline
        _registry.ResetConnection(channel.ChannelId);
        clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<ChannelWarningException>(() => coordinator.ReceiveClosingSignedAsync(channel,
                                                              noOverlap));
        clock.Advance(new ChannelCloseOptions().FeeRangeTimeout - TimeSpan.FromMinutes(5));
        await monitor.WhenIdleAsync();

        // Assert
        Assert.False(warning.CloseConnection);
        Assert.Equal(_registry.Get(channel.ChannelId).FeeRangeDueAt, dueAt);
        var request = Assert.Single(failures);
        Assert.Equal("B2-CLS-R04", request.RequirementId);
        Assert.Equal(ClosingTimeoutMonitor.NoFeeRangePeerMessage, request.PeerMessage);
        Assert.True(request.StillApplies!(channel));
    }

    [Fact]
    public async Task Given_FeeRangeWithoutOverlap_When_OverlappingRangeFollows_Then_AgreedAndNotFailed()
    {
        // Arrange
        var (monitor, clock, failures) = CreateTimeoutMonitor();
        using var _ = monitor;
        var channel = CreateNegotiatingChannel(localSat: 60_000, remoteSat: 40_000, dustLimitSat: 546);
        var coordinator = CreateCoordinator(timeouts: monitor);
        await Assert.ThrowsAsync<ChannelWarningException>(() => coordinator.ReceiveClosingSignedAsync(
                                                              channel,
                                                              new ClosingSignedMessage(
                                                                  new ClosingSignedPayload(
                                                                      channel.ChannelId,
                                                                      LightningMoney.Satoshis(5_000), s_signature),
                                                                  new FeeRangeTlv(LightningMoney.Satoshis(5_000),
                                                                                  LightningMoney.Satoshis(6_000)))));

        // Act: a satisfying range with a fee inside the overlap (B2-CLS-R05: the funder echoes it)
        var replies = await coordinator.ReceiveClosingSignedAsync(
                          channel,
                          new ClosingSignedMessage(
                              new ClosingSignedPayload(channel.ChannelId, LightningMoney.Satoshis(1_000), s_signature),
                              new FeeRangeTlv(LightningMoney.Satoshis(500), LightningMoney.Satoshis(6_000))));
        clock.Advance(TimeSpan.FromHours(1));
        await monitor.WhenIdleAsync();

        // Assert
        Assert.Equal(ChannelState.Closing, channel.State);
        Assert.Single(replies);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Given_Deadline_When_ChannelNoLongerNegotiating_Then_PreconditionFails()
    {
        // Arrange: the failure waits for the lock while the channel moves on
        var (monitor, clock, failures) = CreateTimeoutMonitor();
        using var _ = monitor;
        var channel = CreateFunderReadyToPropose();
        await CreateCoordinator(timeouts: monitor).AdvanceAsync(channel);
        clock.Advance(TimeSpan.FromHours(1));
        await monitor.WhenIdleAsync();
        var request = Assert.Single(failures);

        // Act
        channel.UpdateState(ChannelState.Closing);

        // Assert
        Assert.False(request.StillApplies!(channel));
    }

    [Fact]
    public void Given_NoShutdownSent_When_CreateShutdownResend_Then_Null()
    {
        // Arrange
        var channel = CreateChannel(ChannelState.ShuttingDown);
        channel.SetRemoteShutdownScript(s_remoteScript);

        // Act / Assert
        Assert.Null(CreateCoordinator().CreateShutdownResend(channel));
    }

    private ChannelCloseCoordinator CreateCoordinator(ShutdownScriptProvider? provider = null,
                                                      ClosingTimeoutMonitor? timeouts = null,
                                                      IFeeService? feeService = null,
                                                      ClosingFeeEstimator? feeEstimator = null)
    {
        var nodeOptions = Options.Create(new NodeOptions());
        var memory = new Mock<IChannelMemoryRepository>();
        var messageFactory = new MessageFactory(nodeOptions);
        var transitions = new ChannelStateTransitionService(memory.Object, new ChannelDomainEventQueue(),
                                                            new Mock<ICommitmentSigner>().Object, _signer.Object,
                                                            NullLogger<ChannelStateTransitionService>.Instance,
                                                            messageFactory, new Mock<IMessageSerializer>().Object,
                                                            nodeOptions,
                                                            new Mock<ISecretStorageServiceFactory>().Object,
                                                            _unitOfWork.Object);
        if (feeService is null)
        {
            var fixedFee = new Mock<IFeeService>();
            fixedFee.Setup(f => f.GetCachedFeeRatePerKw()).Returns(LightningMoney.Satoshis(1_000));
            fixedFee.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(LightningMoney.Satoshis(1_000));
            feeService = fixedFee.Object;
        }

        return new ChannelCloseCoordinator(new ClosingTransactionBuilder(nodeOptions), memory.Object,
                                           feeService, _signer.Object,
                                           NullLogger<ChannelCloseCoordinator>.Instance, messageFactory,
                                           Options.Create(new ChannelCloseOptions()), _registry,
                                           provider ?? new FixedProvider(s_localScript), transitions,
                                           _unitOfWork.Object, _monitor.Object, timeouts, feeEstimator);
    }

    /// <summary>A timeout monitor over this test's registry, a manual clock and a mocked fail-the-channel service
    /// that records each request.</summary>
    private (ClosingTimeoutMonitor Monitor, ManualTimeProvider Clock, List<ChannelFailureRequest> Failures)
        CreateTimeoutMonitor()
    {
        var clock = new ManualTimeProvider();
        var failures = new List<ChannelFailureRequest>();
        var failureService = new Mock<IChannelFailureService>();
        failureService.Setup(f => f.FailChannelAsync(It.IsAny<ChannelId>(), It.IsAny<ChannelFailureRequest>(),
                                                     It.IsAny<CancellationToken>()))
                      .Callback((ChannelId _, ChannelFailureRequest request, CancellationToken _) =>
                       {
                           lock (failures)
                               failures.Add(request);
                       })
                      .ReturnsAsync(new ChannelFailureOutcome(ChannelFailureStatus.Broadcast, null));
        var services = new ServiceCollection();
        services.AddSingleton(failureService.Object);
        var monitor = new ClosingTimeoutMonitor(_registry, services.BuildServiceProvider(),
                                                Options.Create(new ChannelCloseOptions()),
                                                NullLogger<ClosingTimeoutMonitor>.Instance, clock);
        return (monitor, clock, failures);
    }

    /// <summary>A funder channel in Negotiating whose shutdowns both went over the current connection.</summary>
    private ChannelModel CreateFunderReadyToPropose()
    {
        var channel = CreateNegotiatingChannel(localSat: 60_000, remoteSat: 40_000, dustLimitSat: 546);
        var entry = _registry.Get(channel.ChannelId);
        entry.ShutdownSentOnConnection = true;
        entry.ShutdownReceivedOnConnection = true;
        return channel;
    }

    private static ShutdownMessage Shutdown(BitcoinScript script) =>
        new(new ShutdownPayload(new ChannelId(Enumerable.Repeat((byte)0x0e, 32).ToArray()), script));

    private static ClosingSignedMessage ClosingSigned(ulong feeSat) =>
        new(new ClosingSignedPayload(new ChannelId(Enumerable.Repeat((byte)0x0e, 32).ToArray()),
                                     LightningMoney.Satoshis(feeSat), s_signature));

    /// <summary>A channel in Negotiating with both shutdown scripts, no snapshot.</summary>
    private static ChannelModel CreateNegotiatingChannel(long localSat, long remoteSat, long dustLimitSat,
                                                         bool isInitiator = true)
    {
        var channel = CreateChannel(ChannelState.Negotiating, localSat: localSat, remoteSat: remoteSat,
                                    dustLimitSat: dustLimitSat, isInitiator: isInitiator);
        channel.SetLocalShutdownScript(s_localScript);
        channel.SetRemoteShutdownScript(s_remoteScript);
        return channel;
    }

    private static ChannelModel CreateChannel(ChannelState state, long localSat = 60_000, long remoteSat = 40_000,
                                              long dustLimitSat = 546, bool isInitiator = true,
                                              BitcoinScript? localUpfront = null, BitcoinScript? remoteUpfront = null,
                                              byte channelIdTag = 0x0e)
    {
        var local = new ChannelParty(LightningMoney.Satoshis(dustLimitSat), LightningMoney.Satoshis(1_000),
                                     LightningMoney.MilliSatoshis(1_000), 30,
                                     LightningMoney.Satoshis(localSat + remoteSat), 144, localUpfront);
        var remote = new ChannelParty(LightningMoney.Satoshis(dustLimitSat), LightningMoney.Satoshis(1_000),
                                      LightningMoney.MilliSatoshis(1_000), 30,
                                      LightningMoney.Satoshis(localSat + remoteSat), 144, remoteUpfront);
        var channelParams = new ChannelParams(local, remote, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(localSat + remoteSat),
                                                  NormalOperationTestContext.Point(0x01),
                                                  NormalOperationTestContext.Point(0x02))
        {
            TransactionId = new TxId(Enumerable.Repeat((byte)0x0f, 32).ToArray()),
            Index = 0
        };
        var localKeySet = new ChannelKeySetModel(0, NormalOperationTestContext.Point(0x01),
                                                 NormalOperationTestContext.Point(0x03),
                                                 NormalOperationTestContext.Point(0x04),
                                                 NormalOperationTestContext.Point(0x05),
                                                 NormalOperationTestContext.Point(0x06),
                                                 NormalOperationTestContext.Point(0x07));
        var remoteKeySet = new ChannelKeySetModel(0, NormalOperationTestContext.Point(0x02),
                                                  NormalOperationTestContext.Point(0x08),
                                                  NormalOperationTestContext.Point(0x09),
                                                  NormalOperationTestContext.Point(0x0b),
                                                  NormalOperationTestContext.Point(0x0c),
                                                  NormalOperationTestContext.Point(0x0d));
        return new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat(channelIdTag, 32).ToArray()), null,
                                fundingOutput, isInitiator, null, null, LightningMoney.Satoshis(localSat),
                                localKeySet, 0, 0, LightningMoney.Satoshis(remoteSat), remoteKeySet, 0,
                                NormalOperationTestContext.PeerNodeId, 0, state, ChannelVersion.V1);
    }

    private sealed class FixedProvider(BitcoinScript script)
        : ShutdownScriptProvider(Options.Create(new NodeOptions()), new Mock<IBitcoinWalletService>().Object)
    {
        public override Task<BitcoinScript> GetLocalScriptAsync(ChannelModel channel) => Task.FromResult(script);
    }
}
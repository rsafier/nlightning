using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Channels.Interfaces;
using Application.Channels.Services;
using Application.Onchain;
using Application.Payments.FinalHop;
using Application.Payments.Onion;
using Application.Payments.Policy;
using Application.Payments.Switch;
using Channels.Handlers;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using static Channels.Handlers.NormalOperationTestContext;

/// <summary>
/// BOLT 5 plan O3-T4: how <see cref="HtlcSwitch"/> propagates the on-chain resolution of an HTLC it forwarded. The
/// resolver raises its event again every block until the output is irrevocable, so the switch must act once.
/// </summary>
public class OnchainEventsTests
{
    private static readonly ChannelId s_downstreamChannelId = new(Enumerable.Repeat((byte)0x99, 32).ToArray());
    private static readonly Secret s_incomingSecret = SecretOf(0x5E);
    private static readonly Secret s_preimage = SecretOf(9);
    private const ulong DownstreamHtlcId = 4;

    private readonly NormalOperationTestContext _context = new();
    private readonly Mock<IChannelOperations> _operations = new();
    private readonly Mock<IForwardCircuitDbRepository> _circuits = new();
    private readonly Mock<IFailureOnionService> _failureOnions = new();
    private readonly List<FailureMessage> _createdFailures = [];
    private readonly HtlcRecord _incoming;

    public OnchainEventsTests()
    {
        _incoming = _context.LockIn(HtlcDirection.Incoming, 30_000_000, s_preimage);
        _context.UnitOfWork.SetupGet(u => u.ForwardCircuitDbRepository).Returns(_circuits.Object);
        _context.ChannelStateDbRepository
                .Setup(r => r.GetHtlcOriginAsync(s_downstreamChannelId,
                                                 new HtlcKey(HtlcDirection.Outgoing, DownstreamHtlcId)))
                .ReturnsAsync(HtlcOrigin.Forwarded(TestChannelId, _incoming.Id));
        _circuits.Setup(r => r.GetByIncomingAsync(TestChannelId, _incoming.Id))
                 .ReturnsAsync(() => Circuit(ForwardCircuitStatus.Offered));
        _circuits.Setup(r => r.UpdateAsync(It.IsAny<ForwardCircuitModel>())).Returns(Task.CompletedTask);
        _failureOnions.Setup(f => f.CreateErrorPacket(It.IsAny<Secret>(), It.IsAny<FailureMessage>(), It.IsAny<int>()))
                      .Callback((Secret _, FailureMessage failure, int _) => _createdFailures.Add(failure))
                      .Returns(new byte[292]);

        // The removal leaves the incoming HTLC's awaiting state, as the channel operations' persisted transition does
        _operations.Setup(o => o.FailHtlcAsync(TestChannelId, _incoming.Id, It.IsAny<ReadOnlyMemory<byte>>(),
                                               It.IsAny<CancellationToken>()))
                   .Callback(() => _context.SetState(_context.State.SendFail(_incoming.Id, new byte[292]).Next))
                   .Returns(Task.CompletedTask);
        _operations.Setup(o => o.FulfillHtlcAsync(TestChannelId, _incoming.Id, It.IsAny<Secret>(),
                                                  It.IsAny<CancellationToken>()))
                   .Callback(() => _context.SetState(_context.State
                                                             .SendFulfill(_incoming.Id, s_preimage,
                                                                          new Infrastructure.Crypto.Hashes.Sha256())
                                                             .Next))
                   .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Given_OnchainTimeoutAtDepth_When_RaisedEveryBlock_Then_UpstreamFailedOnceWithPermanentChannelFailure()
    {
        // Arrange
        var htlcSwitch = CreateSwitch();
        var failed = new OutgoingHtlcFailed(s_downstreamChannelId, DownstreamHtlcId, HashOf(s_preimage),
                                            HtlcRemoval.OnchainTimeout());

        // Act: the resolver's event in three consecutive blocks
        for (var i = 0; i < 3; i++)
            await htlcSwitch.HandleAsync(failed, TestContext.Current.CancellationToken);

        // Assert: one upstream update_fail_htlc, with our own permanent_channel_failure (no downstream error exists)
        _operations.Verify(o => o.FailHtlcAsync(TestChannelId, _incoming.Id, It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()), Times.Once);
        var failure = Assert.Single(_createdFailures);
        Assert.Equal(FailureCode.PermanentChannelFailure, failure.Code);
        _failureOnions.Verify(f => f.CreateErrorPacket(s_incomingSecret, It.IsAny<FailureMessage>(), It.IsAny<int>()),
                              Times.Once);
    }

    [Fact]
    public async Task Given_OnchainTimeoutRefusedUpstream_When_RaisedAgainNextBlock_Then_RetriedUntilSent()
    {
        // Arrange: the upstream peer is away for the first attempt
        var attempts = 0;
        _operations.Setup(o => o.FailHtlcAsync(TestChannelId, _incoming.Id, It.IsAny<ReadOnlyMemory<byte>>(),
                                               It.IsAny<CancellationToken>()))
                   .Callback(() =>
                    {
                        if (++attempts == 1)
                            throw new Domain.Exceptions.CommitmentRefusedException("N6", "peer away");

                        _context.SetState(_context.State.SendFail(_incoming.Id, new byte[292]).Next);
                    })
                   .Returns(Task.CompletedTask);
        var htlcSwitch = CreateSwitch();
        var failed = new OutgoingHtlcFailed(s_downstreamChannelId, DownstreamHtlcId, HashOf(s_preimage),
                                            HtlcRemoval.OnchainTimeout());

        // Act
        for (var i = 0; i < 3; i++)
            await htlcSwitch.HandleAsync(failed, TestContext.Current.CancellationToken);

        // Assert: refused once, sent on the next block's event, never again
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Given_PreimageSeenOnChain_When_Raised_Then_UpstreamFulfilledAtOnceAndOnlyOnce()
    {
        // Arrange
        var htlcSwitch = CreateSwitch();
        var fulfilled = new OutgoingHtlcFulfilled(s_downstreamChannelId, DownstreamHtlcId, HashOf(s_preimage),
                                                  s_preimage);

        // Act: the block that revealed it, and the next one
        await htlcSwitch.HandleAsync(fulfilled, TestContext.Current.CancellationToken);
        await htlcSwitch.HandleAsync(fulfilled, TestContext.Current.CancellationToken);

        // Assert
        _operations.Verify(o => o.FulfillHtlcAsync(TestChannelId, _incoming.Id, s_preimage,
                                                   It.IsAny<CancellationToken>()), Times.Once);
        _operations.Verify(o => o.FailHtlcAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(), It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_OnchainTimeoutRefusedAndNoLongerRaised_When_UpstreamLockInIsReplayed_Then_FailedWithPermanentChannelFailure()
    {
        // Arrange: the resolver's fail was refused (upstream peer away) and the circuit marked Failed; the settling
        // transaction is irrevocable now, so the resolver raises nothing any more, and the outgoing channel is closed
        // (not loaded), so its record derives no resolution
        var status = ForwardCircuitStatus.Offered;
        _circuits.Setup(r => r.GetByIncomingAsync(TestChannelId, _incoming.Id)).ReturnsAsync(() => Circuit(status));
        _circuits.Setup(r => r.UpdateAsync(It.IsAny<ForwardCircuitModel>()))
                 .Callback((ForwardCircuitModel c) => status = c.Status)
                 .Returns(Task.CompletedTask);
        var attempts = 0;
        _operations.Setup(o => o.FailHtlcAsync(TestChannelId, _incoming.Id, It.IsAny<ReadOnlyMemory<byte>>(),
                                               It.IsAny<CancellationToken>()))
                   .Callback(() =>
                    {
                        if (++attempts == 1)
                            throw new Domain.Exceptions.CommitmentRefusedException("N6", "peer away");

                        _context.SetState(_context.State.SendFail(_incoming.Id, new byte[292]).Next);
                    })
                   .Returns(Task.CompletedTask);
        var htlcSwitch = CreateSwitch();
        await htlcSwitch.HandleAsync(new OutgoingHtlcFailed(s_downstreamChannelId, DownstreamHtlcId,
                                                            HashOf(s_preimage), HtlcRemoval.OnchainTimeout()),
                                     TestContext.Current.CancellationToken);
        Assert.Equal(ForwardCircuitStatus.Failed, status);

        // Act: the upstream link comes up and the switch gets the upstream HTLC's lock-in again
        await htlcSwitch.HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _incoming),
                                     TestContext.Current.CancellationToken);
        await htlcSwitch.HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _incoming),
                                     TestContext.Current.CancellationToken);

        // Assert: failed upstream on the first replay (never twice), with our own permanent_channel_failure
        Assert.Equal(2, attempts);
        Assert.Equal(2, _createdFailures.Count);
        Assert.All(_createdFailures, f => Assert.Equal(FailureCode.PermanentChannelFailure, f.Code));
    }

    [Fact]
    public async Task Given_OfferedCircuitWithoutAResolution_When_UpstreamLockInIsReplayed_Then_NotFailed()
    {
        // Arrange: the downstream HTLC is still unresolved (circuit Offered, outgoing channel not loaded)
        var htlcSwitch = CreateSwitch();

        // Act
        await htlcSwitch.HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _incoming),
                                     TestContext.Current.CancellationToken);

        // Assert: it waits: failing upstream before the downstream is resolved could lose the HTLC amount
        _operations.Verify(o => o.FailHtlcAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(),
                                                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
                           Times.Never);
        _operations.Verify(o => o.FulfillHtlcAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(), It.IsAny<Secret>(),
                                                   It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_OfferedCircuitOnAClosedOutgoingChannel_When_ExpiredReasonablyDeepAndLockInReplayed_Then_FailedUpstream()
    {
        // Arrange (NL-320): the outgoing channel closed on chain (Closed, no longer loaded) and its record shows no
        // preimage; the resolver's upstream event never reached the switch, so the circuit is still Offered. The
        // outgoing HTLC's cltv_expiry is 600
        var status = ForwardCircuitStatus.Offered;
        _circuits.Setup(r => r.GetByIncomingAsync(TestChannelId, _incoming.Id)).ReturnsAsync(() => Circuit(status));
        _circuits.Setup(r => r.UpdateAsync(It.IsAny<ForwardCircuitModel>()))
                 .Callback((ForwardCircuitModel c) => status = c.Status)
                 .Returns(Task.CompletedTask);
        var closed = new NormalOperationTestContext(state: ChannelState.Closed).Channel;
        _context.ChannelDbRepository.Setup(r => r.GetByIdAsync(s_downstreamChannelId)).ReturnsAsync(closed);

        // Act: before cltv_expiry + 6 the replay waits
        await CreateSwitch(605).HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _incoming),
                                            TestContext.Current.CancellationToken);
        Assert.Empty(_createdFailures);
        var htlcSwitch = CreateSwitch(606);
        await htlcSwitch.HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _incoming),
                                     TestContext.Current.CancellationToken);
        await htlcSwitch.HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _incoming),
                                     TestContext.Current.CancellationToken);

        // Assert: failed once, with our own permanent_channel_failure, and the circuit Failed
        var failure = Assert.Single(_createdFailures);
        Assert.Equal(FailureCode.PermanentChannelFailure, failure.Code);
        Assert.Equal(ForwardCircuitStatus.Failed, status);
        _operations.Verify(o => o.FailHtlcAsync(TestChannelId, _incoming.Id, It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ConfiguredReasonableDepth_When_OfferedCircuitOnAClosedChannelReplayed_Then_ThatDepthApplies()
    {
        // Arrange (NL-320 review): Node:Onchain:ReasonableDepth = 10, as the resolvers use it; cltv_expiry is 600
        var status = ForwardCircuitStatus.Offered;
        _circuits.Setup(r => r.GetByIncomingAsync(TestChannelId, _incoming.Id)).ReturnsAsync(() => Circuit(status));
        _circuits.Setup(r => r.UpdateAsync(It.IsAny<ForwardCircuitModel>()))
                 .Callback((ForwardCircuitModel c) => status = c.Status)
                 .Returns(Task.CompletedTask);
        var closed = new NormalOperationTestContext(state: ChannelState.Closed).Channel;
        _context.ChannelDbRepository.Setup(r => r.GetByIdAsync(s_downstreamChannelId)).ReturnsAsync(closed);
        var onchainOptions = new OnchainOptions { ReasonableDepth = 10 };

        // Act: 606 (the default depth) and 609 wait
        await CreateSwitch(606, onchainOptions).HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _incoming),
                                                            TestContext.Current.CancellationToken);
        await CreateSwitch(609, onchainOptions).HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _incoming),
                                                            TestContext.Current.CancellationToken);
        Assert.Empty(_createdFailures);
        await CreateSwitch(610, onchainOptions).HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _incoming),
                                                            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FailureCode.PermanentChannelFailure, Assert.Single(_createdFailures).Code);
        Assert.Equal(ForwardCircuitStatus.Failed, status);
    }

    [Theory]
    [InlineData(ChannelState.OnchainResolving)]
    [InlineData(ChannelState.Failed)]
    public async Task Given_OfferedCircuitOnAnOutgoingChannelNotClosed_When_LongExpiredAndLockInReplayed_Then_NotFailed(
        ChannelState state)
    {
        // Arrange (NL-320): only a Closed outgoing channel is settled for good; one still resolving decides itself
        var stored = new NormalOperationTestContext(state: state).Channel;
        _context.ChannelDbRepository.Setup(r => r.GetByIdAsync(s_downstreamChannelId)).ReturnsAsync(stored);

        // Act
        await CreateSwitch(800).HandleAsync(new IncomingHtlcLockedIn(TestChannelId, _incoming),
                                            TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_createdFailures);
        _operations.Verify(o => o.FailHtlcAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(),
                                                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
                           Times.Never);
    }

    private HtlcSwitch CreateSwitch(uint? height = null, OnchainOptions? onchainOptions = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _context.UnitOfWork.Object);
        var provider = services.BuildServiceProvider();
        var options = Options.Create(new NodeOptions { EnableHtlcs = true });
        var onionProcessor = new IncomingOnionProcessor(new Mock<ISphinxService>().Object,
                                                        new Mock<IHopPayloadSerializer>().Object,
                                                        new Mock<IOnionReplayStore>().Object,
                                                        NullLogger<IncomingOnionProcessor>.Instance);
        return new HtlcSwitch(new ChannelLockProvider(), _context.ChannelMemoryRepository.Object, _operations.Object,
                              _failureOnions.Object, new FinalHopProcessor(NullLogger<FinalHopProcessor>.Instance),
                              new HtlcForwardingPolicy(options), NullLogger<HtlcSwitch>.Instance, onionProcessor,
                              new Mock<IPeerLivenessProbe>().Object, provider.GetRequiredService<IServiceScopeFactory>(),
                              blockchainMonitor: height is { } tip ? Monitor(tip) : null,
                              onchainOptions: onchainOptions is null ? null : Options.Create(onchainOptions));
    }

    private static IBlockchainMonitor Monitor(uint height)
    {
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(height);
        return monitor.Object;
    }

    private ForwardCircuitModel Circuit(ForwardCircuitStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return ForwardCircuitModel.Restore(TestChannelId, _incoming.Id, LightningMoney.MilliSatoshis(30_000_000), 640,
                                           HashOf(s_preimage), s_incomingSecret, new ShortChannelId(1, 2, 3),
                                           LightningMoney.MilliSatoshis(29_000_000), 600, now, status,
                                           s_downstreamChannelId, DownstreamHtlcId,
                                           status is ForwardCircuitStatus.Offered ? null : now);
    }
}
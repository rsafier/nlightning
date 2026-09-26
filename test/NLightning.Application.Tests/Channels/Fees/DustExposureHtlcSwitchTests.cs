using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Fees;

using Application.Channels.Fees;
using Application.Payments.Onion;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Serialization.Interfaces;
using Handlers;

/// <summary>
/// BOLT2 plan N9-T3, B2-DUST-01/02: an incoming trimmed HTLC that pushed a commitment over
/// <c>max_dust_htlc_exposure_msat</c> is failed once locked in and never reaches the switch (so no preimage is
/// revealed and nothing is forwarded). The context channel runs at 2,500 sat/kw, where a 2,000 sat HTLC is trimmed on
/// both commitments; the limit is 10,000 sat.
/// </summary>
public class DustExposureHtlcSwitchTests
{
    private const ulong DustHtlcMsat = 2_000_000;
    private static readonly Secret s_sharedSecret = new(Enumerable.Repeat((byte)0x5E, 32).ToArray());

    private readonly NormalOperationTestContext _context = new();
    private readonly Mock<IHtlcSwitch> _inner = new();
    private readonly Mock<IChannelOperations> _operations = new();
    private readonly Mock<IFailureOnionService> _failureOnion = new();
    private readonly Mock<IForwardCircuitDbRepository> _circuits = new();
    private readonly NodeOptions _nodeOptions = new() { EnableHtlcs = true, MaxDustHtlcExposureMsat = 10_000_000 };
    private readonly List<FailureMessage> _failures = [];
    private readonly List<HtlcRecord> _htlcs = [];
    private ISphinxService _sphinx = new FixedSphinx(s_sharedSecret);

    public DustExposureHtlcSwitchTests()
    {
        _context.UnitOfWork.SetupGet(u => u.ForwardCircuitDbRepository).Returns(_circuits.Object);
        _failureOnion.Setup(f => f.CreateErrorPacket(It.IsAny<Secret>(), It.IsAny<FailureMessage>(), It.IsAny<int>()))
                     .Callback((Secret _, FailureMessage message, int _) => _failures.Add(message))
                     .Returns(new byte[292]);

        // Six 2,000 sat HTLCs: the sixth takes both commitments to 12,000 sat of dust
        for (byte i = 0; i < 6; i++)
            _htlcs.Add(_context.LockIn(HtlcDirection.Incoming, DustHtlcMsat, NormalOperationTestContext.SecretOf(i)));
    }

    [Fact]
    public async Task Given_HtlcOverTheDustLimit_When_LockedIn_Then_FailedAndNeverPassedOn()
    {
        // Act
        await CreateSwitch().HandleAsync(LockedIn(5), TestContext.Current.CancellationToken);

        // Assert - failed with an error onion made with the HTLC's shared secret; the switch never saw it
        _operations.Verify(o => o.FailHtlcAsync(NormalOperationTestContext.TestChannelId, 5,
                                                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
                           Times.Once);
        _failureOnion.Verify(f => f.CreateErrorPacket(s_sharedSecret, It.IsAny<FailureMessage>(), It.IsAny<int>()),
                             Times.Once);
        Assert.Equal(FailureCode.TemporaryChannelFailure, Assert.Single(_failures).Code);
        _inner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_HtlcWithinTheDustLimit_When_LockedIn_Then_PassedToTheSwitch()
    {
        // Arrange - the fifth HTLC arrived at exactly 10,000 sat of dust
        var lockedIn = LockedIn(4);

        // Act
        await CreateSwitch().HandleAsync(lockedIn, TestContext.Current.CancellationToken);

        // Assert
        _inner.Verify(s => s.HandleAsync(lockedIn, It.IsAny<CancellationToken>()), Times.Once);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_NoLimit_When_OverExposedHtlcLocksIn_Then_PassedToTheSwitch()
    {
        // Arrange
        _nodeOptions.MaxDustHtlcExposureMsat = null;
        var lockedIn = LockedIn(5);

        // Act
        await CreateSwitch().HandleAsync(lockedIn, TestContext.Current.CancellationToken);

        // Assert
        _inner.Verify(s => s.HandleAsync(lockedIn, It.IsAny<CancellationToken>()), Times.Once);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_SwitchAlreadyStoredTheOnionSecret_When_Replayed_Then_LeftToTheSwitch()
    {
        // Arrange - the switch processed it before (it may be forwarded): never fail it behind its back
        _context.ChannelStateDbRepository
                .Setup(r => r.GetOnionSharedSecretAsync(NormalOperationTestContext.TestChannelId,
                                                        new HtlcKey(HtlcDirection.Incoming, 5)))
                .ReturnsAsync(s_sharedSecret);
        var lockedIn = LockedIn(5);

        // Act
        await CreateSwitch().HandleAsync(lockedIn, TestContext.Current.CancellationToken);

        // Assert
        _inner.Verify(s => s.HandleAsync(lockedIn, It.IsAny<CancellationToken>()), Times.Once);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ForwardCircuitExists_When_Replayed_Then_LeftToTheSwitch()
    {
        // Arrange
        _circuits.Setup(r => r.GetByIncomingAsync(NormalOperationTestContext.TestChannelId, 5))
                 .ReturnsAsync(new ForwardCircuitModel(NormalOperationTestContext.TestChannelId, 5,
                                                       DustHtlcMsat, 600, _htlcs[5].PaymentHash, s_sharedSecret,
                                                       new ShortChannelId(1, 2, 3), DustHtlcMsat - 1_000, 560,
                                                       DateTimeOffset.UnixEpoch));
        var lockedIn = LockedIn(5);

        // Act
        await CreateSwitch().HandleAsync(lockedIn, TestContext.Current.CancellationToken);

        // Assert
        _inner.Verify(s => s.HandleAsync(lockedIn, It.IsAny<CancellationToken>()), Times.Once);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_FailureRefused_When_OverExposedHtlcLocksIn_Then_NothingThrownAndSwitchNotCalled()
    {
        // Arrange - the peer is away: the replay on link-up tries again
        _operations.Setup(o => o.FailHtlcAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(),
                                               It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new CommitmentRefusedException("B2-NO-02", "peer away"));

        // Act
        await CreateSwitch().HandleAsync(LockedIn(5), TestContext.Current.CancellationToken);

        // Assert
        _inner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_MalformedOnion_When_OverExposedHtlcLocksIn_Then_TheSwitchFailsItAsMalformed()
    {
        // Arrange - no shared secret to encrypt a failure with
        _sphinx = new FixedSphinx(null);
        var lockedIn = LockedIn(5);

        // Act
        await CreateSwitch().HandleAsync(lockedIn, TestContext.Current.CancellationToken);

        // Assert
        _inner.Verify(s => s.HandleAsync(lockedIn, It.IsAny<CancellationToken>()), Times.Once);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_HtlcNoLongerWaiting_When_Replayed_Then_PassedToTheSwitch()
    {
        // Arrange - an id the channel does not hold (already removed)
        var lockedIn = new IncomingHtlcLockedIn(NormalOperationTestContext.TestChannelId,
                                                _htlcs[5] with { Id = 42 });

        // Act
        await CreateSwitch().HandleAsync(lockedIn, TestContext.Current.CancellationToken);

        // Assert
        _inner.Verify(s => s.HandleAsync(lockedIn, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_OtherEvent_When_Handled_Then_PassedToTheSwitch()
    {
        // Arrange
        var settled = new OutgoingHtlcSettled(NormalOperationTestContext.TestChannelId, 1, _htlcs[0].PaymentHash,
                                              HtlcRemovalKind.Fail);

        // Act
        await CreateSwitch().HandleAsync(settled, TestContext.Current.CancellationToken);

        // Assert
        _inner.Verify(s => s.HandleAsync(settled, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Given_Registrations_When_AddingTheFeeServicesTwice_Then_TheSwitchIsDecoratedOnce()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_inner.Object);
        services.AddSingleton(_context.ChannelMemoryRepository.Object);
        services.AddSingleton(_operations.Object);
        services.AddSingleton(_failureOnion.Object);
        services.AddSingleton(new Mock<Domain.Bitcoin.Interfaces.IFeeService>().Object);
        services.AddSingleton(CreateOnionProcessor());
        services.AddSingleton(Options.Create(_nodeOptions));

        // Act
        services.AddChannelFeeServices();
        services.AddChannelFeeServices();

        // Assert
        using var provider = services.BuildServiceProvider();
        var decorated = Assert.IsType<DustExposureHtlcSwitch>(provider.GetRequiredService<IHtlcSwitch>());
        Assert.Same(_inner.Object, decorated.Inner);
        Assert.IsType<FeeUpdateScheduler>(provider.GetRequiredService<IFeeUpdateScheduler>());
        Assert.Single(services, d => d.ServiceType == typeof(IFeeUpdateScheduler));
    }

    [Fact]
    public void Given_NoSwitch_When_AddingTheFeeServices_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddChannelFeeServices());
    }

    private IncomingHtlcLockedIn LockedIn(int index) =>
        new(NormalOperationTestContext.TestChannelId, _htlcs[index]);

    private DustExposureHtlcSwitch CreateSwitch()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _context.UnitOfWork.Object);
        var provider = services.BuildServiceProvider();
        return new DustExposureHtlcSwitch(_inner.Object, _context.ChannelMemoryRepository.Object, _operations.Object,
                                          _failureOnion.Object, CreateOnionProcessor(),
                                          provider.GetRequiredService<IServiceScopeFactory>(),
                                          Options.Create(_nodeOptions), NullLogger<DustExposureHtlcSwitch>.Instance);
    }

    private IncomingOnionProcessor CreateOnionProcessor()
    {
        // An empty payload fails validation, so the result carries the shared secret (IncomingOnionFailed)
        var payloads = new Mock<IHopPayloadSerializer>();
        payloads.Setup(p => p.DeserializeAsync(It.IsAny<ReadOnlyMemory<byte>>())).ReturnsAsync(new HopPayload());
        return new IncomingOnionProcessor(_sphinx, payloads.Object, new Mock<IOnionReplayCache>().Object,
                                          NullLogger<IncomingOnionProcessor>.Instance);
    }

    /// <summary>Peels every onion as a final hop with a fixed secret, or fails as a bad onion without one (Moq can't
    /// mock span arguments).</summary>
    private sealed class FixedSphinx(Secret? sharedSecret) : ISphinxService
    {
        public OnionPacket Construct(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                     ReadOnlySpan<byte> associatedData, int hopPayloadsLength,
                                     OnionPacketKind packetKind) => throw new NotSupportedException();

        public ConstructedOnion ConstructWithSharedSecrets(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                                           ReadOnlySpan<byte> associatedData, int hopPayloadsLength,
                                                           OnionPacketKind packetKind) =>
            throw new NotSupportedException();

        public IReadOnlyList<Secret> ComputeSharedSecrets(IReadOnlyList<CompactPubKey> nodeIds, PrivKey sessionKey) =>
            throw new NotSupportedException();

        public PeeledOnion PeelAsLocalNode(OnionPacket packet, ReadOnlySpan<byte> associatedData,
                                           CompactPubKey? pathKey, OnionPacketKind packetKind) =>
            sharedSecret is { } secret
                ? new PeeledOnion(new byte[] { 2, 0 }, secret, null)
                : throw new OnionException(FailureCode.InvalidOnionHmac, "bad hmac");

        public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                                CompactPubKey? pathKey, OnionPacketKind packetKind) =>
            throw new NotSupportedException();
    }
}
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Services;

using Application.Channels.Interfaces;
using Application.Channels.Services;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.ValueObjects;
using Handlers;
using static Handlers.NormalOperationTestContext;

/// <summary>
/// <see cref="ChannelOperationsService"/> (BOLT2 plan N6-T2): preconditions, persist-before-send (I1, I2), the
/// scheduler asked after the lock, and the failed-channel refusal (N6-T3).
/// </summary>
public class ChannelOperationsServiceTests
{
    private static readonly OnionPacket s_onion = new(Onion);

    private readonly NormalOperationTestContext _context;
    private readonly Mock<IChannelMessagePublisher> _publisher = new();
    private readonly Mock<ICommitScheduler> _scheduler = new();
    private readonly Mock<IPeerLivenessProbe> _probe = new();
    private readonly List<IChannelMessage> _published = [];

    public ChannelOperationsServiceTests() : this(new NormalOperationTestContext())
    {
    }

    private ChannelOperationsServiceTests(NormalOperationTestContext context)
    {
        _context = context;
        _publisher.Setup(p => p.Publish(It.IsAny<CompactPubKey>(), It.IsAny<IReadOnlyList<IChannelMessage>>()))
                  .Callback((CompactPubKey _, IReadOnlyList<IChannelMessage> messages) =>
                   {
                       _context.Calls.Add("publish");
                       _published.AddRange(messages);
                   });
        _scheduler.Setup(s => s.Schedule(It.IsAny<ChannelId>())).Callback(() => _context.Calls.Add("schedule"));
        _probe.Setup(p => p.IsAliveAsync(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                          It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
    }

    [Fact]
    public async Task Given_OpenChannel_When_Offering_Then_PersistedThenPublishedThenScheduled()
    {
        // Arrange
        var service = CreateService();
        var hash = HashOf(SecretOf(1));

        // Act
        var id = await service.OfferHtlcAsync(TestChannelId, LightningMoney.MilliSatoshis(40_000_000), hash, 600,
                                              s_onion, null, HtlcOrigin.Local(hash),
                                              TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0UL, id);
        Assert.Equal(["apply", "save", "publish", "schedule"], _context.Calls);
        var add = Assert.IsType<UpdateAddHtlcMessage>(Assert.Single(_published));
        Assert.Equal(40_000_000UL, add.Payload.Amount.MilliSatoshi);
        Assert.Equal(HtlcState.SentAddHtlc, _context.State.GetHtlc(HtlcDirection.Outgoing, 0)!.State);
        Assert.Equal(1UL, _context.State.LocalNextHtlcId);
    }

    [Fact]
    public async Task Given_PersistFails_When_Offering_Then_NothingIsSentAndTheStateIsUnchanged()
    {
        // Arrange - I1/I2: a failed save leaves the old snapshot and sends nothing
        _context.FailSaves();
        var service = CreateService();
        var before = _context.State;
        var hash = HashOf(SecretOf(1));

        // Act
        var offer = service.OfferHtlcAsync(TestChannelId, LightningMoney.MilliSatoshis(40_000_000), hash, 600,
                                           s_onion, null, HtlcOrigin.Local(hash),
                                           TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => offer);
        Assert.Same(before, _context.State);
        Assert.Empty(_published);
        _scheduler.Verify(s => s.Schedule(It.IsAny<ChannelId>()), Times.Never);
    }

    [Fact]
    public async Task Given_LockedInIncomingHtlc_When_Fulfilling_Then_ThePreimageIsPersistedBeforeTheFulfillIsSent()
    {
        // Arrange
        var preimage = SecretOf(9);
        var htlc = _context.LockIn(HtlcDirection.Incoming, 30_000_000, preimage);
        var service = CreateService();

        // Act
        await service.FulfillHtlcAsync(TestChannelId, htlc.Id, preimage, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["apply", "save", "publish", "schedule"], _context.Calls);
        var persisted = _context.Applied.Single().Next.GetHtlc(HtlcDirection.Incoming, htlc.Id)!;
        Assert.Equal(preimage, persisted.Removal!.PaymentPreimage);
        Assert.IsType<UpdateFulfillHtlcMessage>(Assert.Single(_published));
    }

    [Fact]
    public async Task Given_LockedInIncomingHtlc_When_FailingMalformed_Then_UpdateFailMalformedIsSent()
    {
        // Arrange
        var htlc = _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(9));
        var service = CreateService();
        var sha256OfOnion = HashOf(SecretOf(0x44));

        // Act
        await service.FailMalformedHtlcAsync(TestChannelId, htlc.Id, FailureCode.InvalidOnionHmac, sha256OfOnion,
                                             TestContext.Current.CancellationToken);

        // Assert
        var malformed = Assert.IsType<UpdateFailMalformedHtlcMessage>(Assert.Single(_published));
        Assert.Equal((ushort)FailureCode.InvalidOnionHmac, malformed.Payload.FailureCode);
        Assert.Equal((byte[])sha256OfOnion, malformed.Payload.Sha256OfOnion.ToArray());
    }

    [Fact]
    public async Task Given_WrongPreimage_When_Fulfilling_Then_RefusedAndNothingPersisted()
    {
        // Arrange - B2-DEL-R02 is a sender rule of the engine
        var htlc = _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(9));
        var service = CreateService();

        // Act
        var fulfill = service.FulfillHtlcAsync(TestChannelId, htlc.Id, SecretOf(8),
                                               TestContext.Current.CancellationToken);

        // Assert
        var refused = await Assert.ThrowsAsync<CommitmentRefusedException>(() => fulfill);
        Assert.Equal("B2-DEL-R02", refused.RequirementId);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_HtlcsDisabled_When_Offering_Then_Refused()
    {
        // Arrange
        _context.NodeOptions.EnableHtlcs = false;
        var service = CreateService();
        var hash = HashOf(SecretOf(1));

        // Act
        var offer = service.OfferHtlcAsync(TestChannelId, LightningMoney.MilliSatoshis(1_000_000), hash, 600, s_onion,
                                           null, HtlcOrigin.Local(hash), TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<CommitmentRefusedException>(() => offer);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_FailedChannel_When_UpdatingTheFee_Then_RefusedAndNothingPersisted()
    {
        // Arrange - N6-T3: a failed channel refuses every update
        var context = new NormalOperationTestContext(state: ChannelState.Failed);
        var test = new ChannelOperationsServiceTests(context);
        var service = test.CreateService();

        // Act
        var update = service.UpdateFeeAsync(TestChannelId, 5_000, TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<CommitmentRefusedException>(() => update);
        Assert.Empty(context.Calls);
        Assert.Empty(test._published);
    }

    [Fact]
    public async Task Given_PeerNotConnected_When_Offering_Then_Refused()
    {
        // Arrange
        _probe.Setup(p => p.IsAliveAsync(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                          It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);
        var service = CreateService();
        var hash = HashOf(SecretOf(1));

        // Act
        var offer = service.OfferHtlcAsync(TestChannelId, LightningMoney.MilliSatoshis(1_000_000), hash, 600, s_onion,
                                           null, HtlcOrigin.Local(hash), TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<CommitmentRefusedException>(() => offer);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_PeerNotConnected_When_FailingOrUpdatingFee_Then_RefusedAndNothingPersisted()
    {
        // Arrange - a message raised for an away peer is dropped and nothing re-sends it before N7, so a removal or a
        // fee update persisted now would be covered by a commitment_signed the peer can't verify
        var htlc = _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(9));
        _probe.Setup(p => p.IsAliveAsync(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                          It.IsAny<CancellationToken>()))
              .ReturnsAsync(false);
        var service = CreateService();

        // Act
        var fail = service.FailHtlcAsync(TestChannelId, htlc.Id, new byte[292],
                                         TestContext.Current.CancellationToken);
        var fulfill = service.FulfillHtlcAsync(TestChannelId, htlc.Id, SecretOf(9),
                                               TestContext.Current.CancellationToken);
        var fee = service.UpdateFeeAsync(TestChannelId, 5_000, TestContext.Current.CancellationToken);

        // Assert - the HTLC stays locked in, so the switch fails it again once the link is back
        await Assert.ThrowsAsync<CommitmentRefusedException>(() => fail);
        await Assert.ThrowsAsync<CommitmentRefusedException>(() => fulfill);
        await Assert.ThrowsAsync<CommitmentRefusedException>(() => fee);
        Assert.Empty(_context.Calls);
        Assert.Empty(_published);
        Assert.Equal(HtlcState.RcvdAddAckRevocation,
                     _context.State.GetHtlc(HtlcDirection.Incoming, htlc.Id)!.State);
    }

    [Fact]
    public async Task Given_UnknownChannel_When_Failing_Then_KeyNotFound()
    {
        // Arrange
        var service = CreateService();

        // Act
        var fail = service.FailHtlcAsync(new ChannelId(new byte[32]), 0, new byte[292],
                                         TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fail);
    }

    [Fact]
    public async Task Given_DefaultOrigin_When_Offering_Then_ArgumentException()
    {
        // Arrange
        var service = CreateService();

        // Act
        var offer = service.OfferHtlcAsync(TestChannelId, LightningMoney.MilliSatoshis(1_000_000),
                                           HashOf(SecretOf(1)), 600, s_onion, null, default,
                                           TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<ArgumentException>(() => offer);
        Assert.Empty(_context.Calls);
    }

    [Fact]
    public async Task Given_IncomingHtlc_When_RecordingItsOnionSecret_Then_ItIsStagedAndSaved()
    {
        // Arrange
        var htlc = _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(9));
        var service = CreateService();
        var secret = SecretOf(0x55);

        // Act
        await service.RecordOnionSecretAsync(TestChannelId, htlc.Id, secret, TestContext.Current.CancellationToken);

        // Assert
        _context.ChannelStateDbRepository.Verify(
            r => r.SetOnionSharedSecretAsync(TestChannelId, new HtlcKey(HtlcDirection.Incoming, htlc.Id), secret),
            Times.Once);
        Assert.Equal(["save"], _context.Calls);
        Assert.Empty(_published);
    }

    private ChannelOperationsService CreateService()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _context.UnitOfWork.Object);
        services.AddSingleton(_context.Events);
        services.AddScoped(_ => _context.CreateTransitions());
        var provider = services.BuildServiceProvider();

        return new ChannelOperationsService(new ChannelLockProvider(), _context.ChannelMemoryRepository.Object,
                                            _publisher.Object, _scheduler.Object,
                                            NullLogger<ChannelOperationsService>.Instance,
                                            Options.Create(_context.NodeOptions), _probe.Object,
                                            provider.GetRequiredService<IServiceScopeFactory>());
    }
}
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
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Models;
using static Handlers.NormalOperationTestContext;

/// <summary>
/// The attribution seam of <see cref="ChannelOperationsService"/> (NL-326, NL-022): the attributed fail and fulfill
/// persist <c>attribution_data</c>/<c>fulfillment_payload</c> with the removal before the TLVs are sent, and
/// <c>GetHoldTimeAsync</c> reports the time since the incoming add was stored, in units of 100 ms.
/// </summary>
public class ChannelOperationsAttributionTests
{
    private readonly Handlers.NormalOperationTestContext _context = new();
    private readonly Mock<IChannelMessagePublisher> _publisher = new();
    private readonly Mock<IPeerLivenessProbe> _probe = new();
    private readonly List<IChannelMessage> _published = [];
    private readonly ManualClock _clock = new();

    public ChannelOperationsAttributionTests()
    {
        _publisher.Setup(p => p.Publish(It.IsAny<CompactPubKey>(), It.IsAny<IReadOnlyList<IChannelMessage>>()))
                  .Callback((CompactPubKey _, IReadOnlyList<IChannelMessage> messages) =>
                   {
                       _context.Calls.Add("publish");
                       _published.AddRange(messages);
                   });
        _probe.Setup(p => p.IsAliveAsync(It.IsAny<ChannelId>(), It.IsAny<CompactPubKey>(),
                                          It.IsAny<CancellationToken>()))
              .ReturnsAsync(true);
    }

    [Fact]
    public async Task Given_AnAttributedErrorPacket_When_Failing_Then_BothArePersistedBeforeTheTlvIsSent()
    {
        // Arrange
        var htlc = _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(9));
        var packet = new AttributedErrorPacket(Bytes(292, 0x11), Bytes(OnionConstants.AttributionDataLength, 0x22));
        var service = CreateService();

        // Act
        await service.FailHtlcAsync(TestChannelId, htlc.Id, packet, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["apply", "save", "publish"], _context.Calls);
        var removal = _context.Applied.Single().Next.GetHtlc(HtlcDirection.Incoming, htlc.Id)!.Removal!;
        Assert.Equal(packet.Reason, removal.Reason.ToArray());
        Assert.Equal(packet.AttributionData, removal.AttributionData.ToArray());
        var fail = Assert.IsType<UpdateFailHtlcMessage>(Assert.Single(_published));
        Assert.Equal(packet.Reason, fail.Payload.Reason.ToArray());
        Assert.Equal(packet.AttributionData, fail.AttributionDataTlv!.AttributionData);
    }

    [Fact]
    public async Task Given_AnAttributedFulfillment_When_Fulfilling_Then_TheTlvsArePersistedStagedAndSent()
    {
        // Arrange
        var preimage = SecretOf(9);
        var htlc = _context.LockIn(HtlcDirection.Incoming, 30_000_000, preimage);
        var attributed = new AttributedFulfillment(Bytes(OnionConstants.AttributionDataLength, 0x33),
                                                   Bytes(272, 0x44));
        var service = CreateService();

        // Act
        await service.FulfillHtlcAsync(TestChannelId, htlc.Id, preimage, attributed, _ =>
        {
            _context.Calls.Add("staged");
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["apply", "staged", "save", "publish"], _context.Calls);
        var removal = _context.Applied.Single().Next.GetHtlc(HtlcDirection.Incoming, htlc.Id)!.Removal!;
        Assert.Equal(attributed.AttributionData, removal.AttributionData.ToArray());
        Assert.Equal(attributed.FulfillmentPayload, removal.FulfillmentPayload.ToArray());
        var fulfill = Assert.IsType<UpdateFulfillHtlcMessage>(Assert.Single(_published));
        Assert.Equal(attributed.AttributionData, fulfill.AttributionDataTlv!.AttributionData);
        Assert.Equal(attributed.FulfillmentPayload, fulfill.FulfillmentPayloadTlv!.FulfillmentPayload);
    }

    [Fact]
    public async Task Given_AnAttributedFulfillmentWithoutPayload_When_Fulfilling_Then_OnlyTlv1IsSent()
    {
        // Arrange
        var preimage = SecretOf(9);
        var htlc = _context.LockIn(HtlcDirection.Incoming, 30_000_000, preimage);
        var attributed = new AttributedFulfillment(Bytes(OnionConstants.AttributionDataLength, 0x33), null);
        var service = CreateService();

        // Act
        await service.FulfillHtlcAsync(TestChannelId, htlc.Id, preimage, attributed,
                                       cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var fulfill = Assert.IsType<UpdateFulfillHtlcMessage>(Assert.Single(_published));
        Assert.NotNull(fulfill.AttributionDataTlv);
        Assert.Null(fulfill.FulfillmentPayloadTlv);
    }

    [Fact]
    public async Task Given_APayloadAbove32KiB_When_Fulfilling_Then_ArgumentExceptionAndNothingIsPersisted()
    {
        // Arrange - the peer would fail the channel for it (BOLT 2)
        var preimage = SecretOf(9);
        var htlc = _context.LockIn(HtlcDirection.Incoming, 30_000_000, preimage);
        var attributed = new AttributedFulfillment(Bytes(OnionConstants.AttributionDataLength, 0x33),
                                                   new byte[OnionConstants.MaxFulfillmentPayloadLength + 1]);
        var service = CreateService();

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(
                            () => service.FulfillHtlcAsync(TestChannelId, htlc.Id, preimage, attributed,
                                                           cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("32769", exception.Message);
        Assert.Empty(_context.Calls);
        Assert.Empty(_published);
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(99, 0u)]
    [InlineData(100, 1u)]
    [InlineData(2_549, 25u)]
    [InlineData(60_000, 600u)]
    public async Task Given_TheAddWasStoredEarlier_When_GettingTheHoldTime_Then_ItIsTheElapsedTimeIn100MsUnits(
        int elapsedMs, uint expected)
    {
        // Arrange
        var addedAt = _clock.GetUtcNow();
        var key = new HtlcKey(HtlcDirection.Incoming, 4);
        _context.ChannelStateDbRepository.Setup(r => r.GetHtlcAddedAtAsync(TestChannelId, key)).ReturnsAsync(addedAt);
        _clock.Advance(TimeSpan.FromMilliseconds(elapsedMs));
        var service = CreateService();

        // Act
        var holdTime = await service.GetHoldTimeAsync(TestChannelId, 4, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, holdTime);
    }

    [Fact]
    public async Task Given_NoReceiptTime_When_GettingTheHoldTime_Then_ZeroMeansNoTimingInformation()
    {
        // Arrange - an HTLC stored before migration AddAttributionData
        _context.ChannelStateDbRepository.Setup(r => r.GetHtlcAddedAtAsync(It.IsAny<ChannelId>(), It.IsAny<HtlcKey>()))
                .ReturnsAsync((DateTimeOffset?)null);
        var service = CreateService();

        // Act
        var holdTime = await service.GetHoldTimeAsync(TestChannelId, 4, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0u, holdTime);
    }

    private ChannelOperationsService CreateService()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _context.UnitOfWork.Object);
        services.AddSingleton(_context.Events);
        services.AddScoped(_ => _context.CreateTransitions());
        var provider = services.BuildServiceProvider();

        return new ChannelOperationsService(new ChannelLockProvider(), _context.ChannelMemoryRepository.Object,
                                            _publisher.Object, new Mock<ICommitScheduler>().Object,
                                            NullLogger<ChannelOperationsService>.Instance,
                                            Options.Create(_context.NodeOptions), _probe.Object,
                                            provider.GetRequiredService<IServiceScopeFactory>(), null, _clock);
    }

    private static byte[] Bytes(int length, byte tag) => Enumerable.Repeat(tag, length).ToArray();

    /// <summary>
    /// A clock that moves only when advanced: a wall-clock based one adds the test's own run time, so 99 ms could read
    /// as one 100 ms unit.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.RoutingPolicies;

using Application.Channels.RoutingPolicies;
using Application.Channels.Services;
using Application.Gossip.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.RoutingPolicies;
using Domain.Channels.ValueObjects;

/// <summary>
/// Wave sp1 lane SP1-G: <see cref="ChannelPolicyService"/> validates, saves and announces a channel's policy.
/// </summary>
public class ChannelPolicyServiceTests : IAsyncDisposable
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private readonly Domain.Node.Options.NodeOptions _nodeOptions = ChannelPolicyTestKit.CreateNodeOptions();
    private readonly InMemoryChannelPolicyTable _table = new();
    private readonly List<ChannelModel> _channels = [ChannelPolicyTestKit.CreateChannel()];
    private readonly Mock<IChannelUpdateService> _updates = new();
    private readonly ChannelLockProvider _locks = new();
    private readonly List<IAsyncDisposable> _providers = [];

    private ChannelId ChannelId => _channels[0].ChannelId;

    [Fact]
    public async Task Given_APatch_When_Set_Then_ItIsSavedAndAChannelUpdateIsSentOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (service, store) = CreateService();

        // Act
        var effective = await service.SetAsync(ChannelId, new ChannelPolicyOverride(ChannelId, 2_500, 300, 72, 10_000,
                                                   400_000_000), ct);

        // Assert
        Assert.Equal(2_500u, effective.FeeBaseMsat);
        Assert.Equal(300u, effective.FeeProportionalMillionths);
        Assert.Equal((ushort)72, effective.CltvExpiryDelta);
        Assert.Equal(10_000ul, effective.HtlcMinimumMsat);
        Assert.Equal(400_000_000ul, effective.HtlcMaximumMsat);
        Assert.Equal(s_now, effective.Override?.UpdatedAt);
        var row = Assert.Single(_table.Rows).Value;
        Assert.Equal(2_500u, row.FeeBaseMsat);
        Assert.Equal(store.GetOverride(ChannelId), row);
        _updates.Verify(u => u.SendChannelUpdateAsync(ChannelId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_TwoPatches_When_Set_Then_TheSecondKeepsTheFirstsOtherValues()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (service, _) = CreateService();
        await service.SetAsync(ChannelId, new ChannelPolicyOverride(ChannelId, 2_500, CltvExpiryDelta: 72), ct);

        // Act
        var effective = await service.SetAsync(ChannelId, new ChannelPolicyOverride(ChannelId, 3_000), ct);

        // Assert
        Assert.Equal(3_000u, effective.FeeBaseMsat);
        Assert.Equal((ushort)72, effective.CltvExpiryDelta);
        Assert.Equal(1u, effective.FeeProportionalMillionths);
        _updates.Verify(u => u.SendChannelUpdateAsync(ChannelId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Given_TheSameValuesAgain_When_Set_Then_NothingIsSavedOrSent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (service, _) = CreateService();
        await service.SetAsync(ChannelId, new ChannelPolicyOverride(ChannelId, 2_500), ct);

        // Act
        await service.SetAsync(ChannelId, new ChannelPolicyOverride(ChannelId, 2_500), ct);

        // Assert
        Assert.Equal(1, _table.Saves);
        _updates.Verify(u => u.SendChannelUpdateAsync(ChannelId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData((ushort)33, null)]
    [InlineData(null, 1_000_000_001UL)]
    public async Task Given_AnInvalidValue_When_Set_Then_ArgumentExceptionAndNothingSavedOrSent(ushort? delta,
        ulong? maximum)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (service, store) = CreateService();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetAsync(
                                                        ChannelId, new ChannelPolicyOverride(ChannelId,
                                                            CltvExpiryDelta: delta, HtlcMaximumMsat: maximum), ct));
        Assert.Empty(_table.Rows);
        Assert.Null(store.GetOverride(ChannelId));
        _updates.Verify(u => u.SendChannelUpdateAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()),
                        Times.Never);
    }

    [Fact]
    public async Task Given_AnUnknownChannel_When_SetGetOrReset_Then_KeyNotFound()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (service, _) = CreateService();
        var unknown = ChannelPolicyTestKit.CreateChannel(9).ChannelId;

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.SetAsync(unknown, new ChannelPolicyOverride(unknown, 1), ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(unknown, ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ResetAsync(unknown, ct));
    }

    [Fact]
    public async Task Given_APatchForAnotherChannel_When_Set_Then_ArgumentException()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (service, _) = CreateService();
        var other = ChannelPolicyTestKit.CreateChannel(9).ChannelId;

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SetAsync(ChannelId, new ChannelPolicyOverride(other, 1), ct));
    }

    [Fact]
    public async Task Given_AnOverride_When_Reset_Then_NodeRoutingAppliesAgainAndAnUpdateIsSent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (service, _) = CreateService();
        await service.SetAsync(ChannelId, new ChannelPolicyOverride(ChannelId, 2_500, 300), ct);

        // Act
        await service.ResetAsync(ChannelId, ct);
        var effective = await service.GetAsync(ChannelId, ct);

        // Assert
        Assert.Equal(1_000u, effective.FeeBaseMsat);
        Assert.Equal(1u, effective.FeeProportionalMillionths);
        Assert.Null(effective.Override);
        Assert.Empty(_table.Rows);
        _updates.Verify(u => u.SendChannelUpdateAsync(ChannelId, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Given_NoOverride_When_Reset_Then_NothingIsSent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (service, _) = CreateService();

        // Act
        await service.ResetAsync(ChannelId, ct);

        // Assert
        _updates.Verify(u => u.SendChannelUpdateAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()),
                        Times.Never);
    }

    [Fact]
    public async Task Given_ASetPolicy_When_TheNodeRestarts_Then_ItStillApplies()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (service, _) = CreateService();
        await service.SetAsync(ChannelId, new ChannelPolicyOverride(ChannelId, 2_500, HtlcMaximumMsat: 50_000_000),
                               ct);

        // Act: a new store and service over the same table
        var (restarted, restartedStore) = CreateService();
        var effective = await restarted.GetAsync(ChannelId, ct);

        // Assert
        Assert.Equal(2_500u, effective.FeeBaseMsat);
        Assert.Equal(50_000_000ul, effective.HtlcMaximumMsat);
        Assert.Equal(50_000_000ul, restartedStore.GetConfiguredPolicy(ChannelId).HtlcMaximumMsat);
    }

    [Fact]
    public async Task Given_TheUpdateFails_When_Set_Then_ThePolicyIsSavedAnyway()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _updates.Setup(u => u.SendChannelUpdateAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("no alias yet"));
        var (service, store) = CreateService();

        // Act
        var effective = await service.SetAsync(ChannelId, new ChannelPolicyOverride(ChannelId, 2_500), ct);

        // Assert
        Assert.Equal(2_500u, effective.FeeBaseMsat);
        Assert.Equal(2_500u, store.GetOverride(ChannelId)?.FeeBaseMsat);
    }

    [Fact]
    public async Task Given_APatchClearingNothing_When_SetWithoutValues_Then_NothingChanges()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (service, _) = CreateService();

        // Act
        var effective = await service.SetAsync(ChannelId, new ChannelPolicyOverride(ChannelId), ct);

        // Assert
        Assert.Null(effective.Override);
        Assert.Equal(0, _table.Saves);
        _updates.Verify(u => u.SendChannelUpdateAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()),
                        Times.Never);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private (ChannelPolicyService Service, ChannelPolicyStore Store) CreateService()
    {
        var provider = ChannelPolicyTestKit.CreateProvider(_table);
        _providers.Add(provider);
        var store = ChannelPolicyTestKit.CreateStore(provider, _nodeOptions);
        var service = new ChannelPolicyService(store, ChannelPolicyTestKit.CreateMemory(_channels).Object, _locks,
                                               Options.Create(_nodeOptions), NullLogger<ChannelPolicyService>.Instance,
                                               _updates.Object, new FixedClock(s_now));
        return (service, store);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
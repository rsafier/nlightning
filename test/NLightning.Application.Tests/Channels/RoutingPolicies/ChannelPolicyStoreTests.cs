namespace NLightning.Application.Tests.Channels.RoutingPolicies;

using Domain.Channels.RoutingPolicies;

/// <summary>
/// Wave sp1 lane SP1-G: the override store over the unit of work, and its memory-only fallback.
/// </summary>
public class ChannelPolicyStoreTests
{
    private readonly Domain.Node.Options.NodeOptions _nodeOptions = ChannelPolicyTestKit.CreateNodeOptions();

    [Fact]
    public async Task Given_ASavedOverride_When_ANewStoreLoads_Then_ItIsBack()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var table = new InMemoryChannelPolicyTable();
        var channel = ChannelPolicyTestKit.CreateChannel();
        var policyOverride = new ChannelPolicyOverride(channel.ChannelId, 10, 20, 60);
        await using (var provider = ChannelPolicyTestKit.CreateProvider(table))
        {
            var store = ChannelPolicyTestKit.CreateStore(provider, _nodeOptions);
            await store.SaveAsync(policyOverride, ct);
            Assert.True(store.IsPersistent);
        }

        // Act: a restart
        await using var restarted = ChannelPolicyTestKit.CreateProvider(table);
        var reloaded = ChannelPolicyTestKit.CreateStore(restarted, _nodeOptions);

        // Assert: the synchronous reader loads on first use
        Assert.Equal(policyOverride, reloaded.GetOverride(channel.ChannelId));
        var configured = reloaded.GetConfiguredPolicy(channel.ChannelId);
        Assert.Equal(10u, configured.FeeBaseMsat);
        Assert.Equal(20u, configured.FeeProportionalMillionths);
        Assert.Equal((ushort)60, configured.CltvExpiryDelta);
        Assert.Equal(1_000ul, configured.HtlcMinimumMsat);
        Assert.Null(configured.HtlcMaximumMsat);
        Assert.Equal(1, table.Saves);
    }

    [Fact]
    public async Task Given_AStoredOverride_When_Deleted_Then_TheRowAndTheMemoryAreGone()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var table = new InMemoryChannelPolicyTable();
        var channel = ChannelPolicyTestKit.CreateChannel();
        await using var provider = ChannelPolicyTestKit.CreateProvider(table);
        var store = ChannelPolicyTestKit.CreateStore(provider, _nodeOptions);
        await store.SaveAsync(new ChannelPolicyOverride(channel.ChannelId, 10), ct);

        // Act
        var removed = await store.DeleteAsync(channel.ChannelId, ct);
        var removedAgain = await store.DeleteAsync(channel.ChannelId, ct);

        // Assert
        Assert.True(removed);
        Assert.False(removedAgain);
        Assert.Null(store.GetOverride(channel.ChannelId));
        Assert.Empty(table.Rows);
        Assert.Equal(2, table.Saves);
    }

    [Fact]
    public async Task Given_TheStore_When_ReadManyTimes_Then_TheTableIsLoadedOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var table = new InMemoryChannelPolicyTable();
        await using var provider = ChannelPolicyTestKit.CreateProvider(table);
        var store = ChannelPolicyTestKit.CreateStore(provider, _nodeOptions);
        var channel = ChannelPolicyTestKit.CreateChannel();

        // Act
        await store.LoadAsync(ct);
        for (var i = 0; i < 10; i++)
            store.GetOverride(channel.ChannelId);
        await store.LoadAsync(ct);

        // Assert
        Assert.Equal(1, table.Reads);
    }

    [Fact]
    public async Task Given_AUnitOfWorkWithoutTheRepository_When_Saving_Then_TheOverrideIsKeptInMemoryOnly()
    {
        // Arrange: lane SP1-C's table is not in this build (the contract's default throws NotSupportedException)
        var ct = TestContext.Current.CancellationToken;
        var channel = ChannelPolicyTestKit.CreateChannel();
        await using var provider = ChannelPolicyTestKit.CreateProvider(null);
        var store = ChannelPolicyTestKit.CreateStore(provider, _nodeOptions);

        // Act
        await store.SaveAsync(new ChannelPolicyOverride(channel.ChannelId, 10), ct);

        // Assert
        Assert.False(store.IsPersistent);
        Assert.Equal(10u, store.GetOverride(channel.ChannelId)?.FeeBaseMsat);
        Assert.True(await store.DeleteAsync(channel.ChannelId, ct));
        Assert.Null(store.GetOverride(channel.ChannelId));
    }

    [Fact]
    public async Task Given_ANodeRoutingChange_When_Read_Then_UnsetValuesFollowIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var channel = ChannelPolicyTestKit.CreateChannel();
        await using var provider = ChannelPolicyTestKit.CreateProvider(new InMemoryChannelPolicyTable());
        var store = ChannelPolicyTestKit.CreateStore(provider, _nodeOptions);
        await store.SaveAsync(new ChannelPolicyOverride(channel.ChannelId, FeeProportionalMillionths: 77), ct);

        // Act
        _nodeOptions.Routing.FeeBaseMsat = 4_321;
        var effective = store.GetEffectivePolicy(channel);

        // Assert
        Assert.Equal(4_321u, effective.FeeBaseMsat);
        Assert.Equal(77u, effective.FeeProportionalMillionths);
    }
}
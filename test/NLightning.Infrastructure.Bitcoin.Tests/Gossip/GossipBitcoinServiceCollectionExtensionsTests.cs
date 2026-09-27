using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

using Bitcoin.Gossip;
using Bitcoin.Wallet.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Models;
using Domain.Money;
using Domain.Onchain.Events;
using Domain.Onchain.Interfaces;

public class GossipBitcoinServiceCollectionExtensionsTests
{
    [Fact]
    public void Given_ChainAndWatcher_When_AddGossipBitcoinServices_Then_SingletonsResolve()
    {
        // Arrange
        var watcher = new Mock<IOutpointWatcher>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBitcoinChainService>(new FakeBitcoinChain());
        services.AddSingleton(watcher.Object);

        // Act
        services.AddGossipBitcoinServices();
        services.AddGossipBitcoinServices(); // idempotent
        using var provider = services.BuildServiceProvider();
        var lookup = provider.GetRequiredService<IFundingOutputLookup>();
        var verifier = provider.GetRequiredService<IGossipSignatureVerifier>();

        // Assert: one of each, and the lookup subscribed to disconnected blocks
        Assert.IsType<FundingOutputLookup>(lookup);
        Assert.IsType<GossipSignatureVerifier>(verifier);
        Assert.Same(lookup, provider.GetRequiredService<IFundingOutputLookup>());
        Assert.Single(services, d => d.ServiceType == typeof(IFundingOutputLookup));
        watcher.VerifyAdd(w => w.OnBlockDisconnected += It.IsAny<EventHandler<BlockDisconnectedEventArgs>>(),
                          Times.Once);
    }

    [Fact]
    public void Given_DecoratedFundingOutputLookup_When_ResolvingPendingChannels_Then_ConcreteLookupIsTheView()
    {
        // Arrange: the gossip probe replaces IFundingOutputLookup with a wrapper that is no IGossipPendingChannels
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBitcoinChainService>(new FakeBitcoinChain());
        services.AddGossipBitcoinServices();
        var descriptor = services.Last(d => d.ServiceType == typeof(IFundingOutputLookup));
        services.Replace(ServiceDescriptor.Singleton<IFundingOutputLookup>(
                             sp => new WrappingLookup((IFundingOutputLookup)descriptor.ImplementationFactory!(sp))));
        services.AddGossipBitcoinServices(); // idempotent: adds no second view

        // Act
        using var provider = services.BuildServiceProvider();
        var decorated = provider.GetRequiredService<IFundingOutputLookup>();
        var views = provider.GetServices<IGossipPendingChannels>().ToList();

        // Assert: the one view is the lookup the wrapper forwards to (NL-415)
        var wrapper = Assert.IsType<WrappingLookup>(decorated);
        var view = Assert.Single(views);
        Assert.IsType<FundingOutputLookup>(view);
        Assert.Same(wrapper.Inner, view);
        Assert.Same(provider.GetRequiredService<FundingOutputLookup>(), view);
    }

    private sealed class WrappingLookup(IFundingOutputLookup inner) : IFundingOutputLookup
    {
        public IFundingOutputLookup Inner { get; } = inner;

        public Task<FundingOutputLookupResult> LookupAsync(ShortChannelId shortChannelId,
                                                           CancellationToken cancellationToken = default) =>
            Inner.LookupAsync(shortChannelId, cancellationToken);

        public Task<FundingOutputLookupResult> VerifyAsync(ShortChannelId shortChannelId, CompactPubKey key1,
                                                           CompactPubKey key2, LightningMoney? expectedAmount = null,
                                                           CancellationToken cancellationToken = default) =>
            Inner.VerifyAsync(shortChannelId, key1, key2, expectedAmount, cancellationToken);

        public void InvalidateFrom(uint height) => Inner.InvalidateFrom(height);
    }
}
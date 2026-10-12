using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Tests.Channels.Quiescence;

using Application.Channels.Handlers;
using Application.Channels.Handlers.Interfaces;
using Application.InteractiveTx;
using Domain.Channels.Quiescence;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.Messages;
using Harness;
using InteractiveTx.TestDoubles;

/// <summary>
/// Wave qit integration (splicing plan D2, SP-Q-01): a <see cref="QuiescencePurpose.Probe"/> has no dependent protocol,
/// so once the channel is quiescent with us as initiator our <c>tx_abort</c> ends it. The peer echoes it (BOLT 2) and
/// ends its own quiescence; our driver takes the echo as the echo, so nothing bounces. Two <see cref="TwoNodeHarness"/>
/// nodes with the production quiescence services and the production interactive-tx driver over the Domain session.
/// </summary>
public class QuiescenceProbeEndTests
{
    [Fact]
    public async Task Given_OurProbe_When_TheChannelIsQuiescent_Then_OurTxAbortEndsItOnBothSidesWithOneEcho()
    {
        // Arrange
        using var pair = new QuiescenceTestPair(configureNode: AddInteractiveTx);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var initiator = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.PumpAsync();

        // Assert: quiescent with Alice the initiator, then Alice's tx_abort and Bob's echo, and nothing more
        Assert.Equal(QuiescenceInitiator.Local, await initiator.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Single(pair.Bob.Received.OfType<TxAbortMessage>());
        Assert.Single(pair.Alice.Received.OfType<TxAbortMessage>());
        Assert.Empty(pair.StfuWarnings);
        Assert.False(pair.State(pair.Alice).BlocksNewLocalUpdates);
        Assert.False(pair.State(pair.Bob).BlocksNewLocalUpdates);

        // ...and the channel works again: an HTLC goes through both ways
        await pair.OfferAsync(pair.Alice, 20_000_000, 1);
        await pair.OfferAsync(pair.Bob, 10_000_000, 2);
        await pair.PumpAsync();
        Assert.Empty(pair.FulfillRefusals);
        Assert.Equal(2, pair.Alice.Received.OfType<UpdateFulfillHtlcMessage>().Count()
                      + pair.Bob.Received.OfType<UpdateFulfillHtlcMessage>().Count());
    }

    [Fact]
    public async Task Given_ThePeersProbe_When_Quiescent_Then_OnlyThePeerSendsTxAbortAndWeEcho()
    {
        // Arrange: Bob requests; for Alice it is the peer's quiescence, which only its initiator ends
        using var pair = new QuiescenceTestPair(configureNode: AddInteractiveTx);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var initiator = pair.Quiescence(pair.Bob).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.PumpAsync();

        // Assert: Bob (initiator) ends it with one tx_abort, Alice only echoes
        Assert.Equal(QuiescenceInitiator.Local, await initiator.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Single(pair.Alice.Received.OfType<TxAbortMessage>());
        Assert.Single(pair.Bob.Received.OfType<TxAbortMessage>());
        Assert.False(pair.State(pair.Alice).BlocksNewLocalUpdates);
        Assert.False(pair.State(pair.Bob).BlocksNewLocalUpdates);
    }

    private static void AddInteractiveTx(HarnessNode node, IServiceCollection services)
    {
        // The harness's AddBitcoinInfrastructure registers the chain-backed inspector: replace the Bitcoin side
        services.Replace(ServiceDescriptor.Singleton<IInteractiveTxBuilder, FakeInteractiveTxBuilder>());
        services.Replace(ServiceDescriptor.Singleton<IPrevTxInspector, FakePrevTxInspector>());
        services.Replace(ServiceDescriptor.Singleton<IInteractiveTxContributor, FakeInteractiveTxContributor>());
        services.AddInteractiveTxServices();
        services.AddScoped<IChannelMessageHandler<TxAbortMessage>, TxAbortMessageHandler>();
    }
}
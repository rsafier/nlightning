using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Splicing;
using Domain.Channels.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Harness;

/// <summary>
/// NL-877 T5 (NL-965): a simple taproot channel is not spliced yet. <c>splicein</c>/<c>spliceout</c>/<c>bumpsplice</c>
/// are refused with a reason before anything is reserved or sent, and a peer's <c>splice_init</c> is answered with
/// <c>tx_abort</c>; the channel stays open and usable.
/// </summary>
public class SpliceTaprootRefusalTests
{
    [Theory]
    [InlineData(100_000)]
    [InlineData(-100_000)]
    public async Task Given_TaprootChannel_When_SpliceRequested_Then_RefusedWithAReasonAndNothingSent(long amount)
    {
        // Arrange
        using var harness = new SpliceHarness(simpleTaproot: true);
        harness.Alice.Fund(500_000);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
                          () => harness.Alice.Service.StartAsync(
                              new SpliceRequest(TwoNodeHarness.ChannelId, amount, SpliceHarness.FeeratePerKw), ct));
        var bump = await Assert.ThrowsAsync<InvalidOperationException>(
                       () => harness.Alice.Service.BumpAsync(
                           new SpliceBumpRequest(TwoNodeHarness.ChannelId, SpliceHarness.FeeratePerKw * 2), ct));

        // Assert
        Assert.Contains("simple taproot", refused.Message);
        Assert.Contains("simple taproot", bump.Message);
        Assert.Empty(harness.Transcript);
        Assert.Empty(harness.Alice.Contributor.ActiveReservations);
        Assert.Equal(ChannelState.Open, harness.Alice.Node.Channel.State);
    }

    [Fact]
    public async Task Given_TaprootChannel_When_PeerSendsSpliceInit_Then_TxAbortAndTheChannelStaysOpen()
    {
        // Arrange
        using var harness = new SpliceHarness(simpleTaproot: true);
        var bob = harness.Bob.Node;
        var spliceInit = harness.Alice.Node.Services
                                .GetRequiredService<Domain.Protocol.Interfaces.IMessageFactory>()
                                .CreateSpliceInitMessage(TwoNodeHarness.ChannelId, 100_000, SpliceHarness.FeeratePerKw,
                                                         0, new CompactPubKey(harness.Alice.Node.Basepoints.FundingPubKey));

        // Act
        await bob.ChannelManager.HandleChannelMessageAsync(spliceInit, bob.NegotiatedFeatures,
                                                           harness.Alice.Node.NodeId);

        // Assert: a tx_abort naming the reason, no splice_ack, no negotiation; the channel goes on
        var abort = Assert.IsType<TxAbortMessage>(Assert.Single(harness.Transcript, t => t.From == "Bob").Message);
        Assert.Contains("simple taproot", System.Text.Encoding.ASCII.GetString(abort.Payload.Data.ToArray()));
        Assert.Null(harness.Bob.Service.GetNegotiation(TwoNodeHarness.ChannelId));
        Assert.Equal(ChannelState.Open, bob.Channel.State);
        Assert.Contains(SpliceService.TaprootSpliceRefusal, System.Text.Encoding.ASCII.GetString(abort.Payload.Data.ToArray()));
    }
}
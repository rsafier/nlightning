namespace NLightning.Application.Tests.Channels.Acceptance;

using Domain.Channels.Acceptance;
using Domain.Channels.DualFunding.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Messages;
using DualFunding;
using InteractiveTx.TestDoubles;

/// <summary>
/// NL-1180 on the dual-funded accepter (<see cref="DualFundHarness"/>): an external decider answers before Bob makes
/// keys for the open; its rejection is Alice's error, its values are what <c>accept_channel2</c> announces, and a value
/// BOLT 2 fixes for v2 (the reserve) refuses the open.
/// </summary>
public class DualFundOpenDecisionTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);

    [Fact]
    public async Task Given_BobsDeciderRejects_When_AliceOpens_Then_SheGetsItsError()
    {
        // Arrange
        await using var harness = await CreateAsync(new ChannelOpenDecisionGateTests.FixedDecider(
                                                        ChannelOpenDecision.Rejected("no v2 today")));

        // Act
        var result = await OpenAsync(harness);

        // Assert
        Assert.NotNull(result.FailureReason);
        var error = Assert.IsType<ChannelErrorException>(Assert.Single(harness.Bob.Errors));
        Assert.Equal("no v2 today", error.PeerMessage);
        Assert.Empty(harness.Bob.Memory.FindChannels(_ => true));
        Assert.DoesNotContain(harness.Transcript, t => t.Message is AcceptChannel2Message);
    }

    [Fact]
    public async Task Given_BobsDeciderAcceptsWithValues_When_AliceOpens_Then_AcceptChannel2AnnouncesThemAndItOpens()
    {
        // Arrange
        await using var harness = await CreateAsync(new ChannelOpenDecisionGateTests.FixedDecider(
                                                        new ChannelOpenDecision
                                                        {
                                                            Accept = true,
                                                            ToSelfDelay = 300,
                                                            MaxAcceptedHtlcs = 25,
                                                            MinimumDepth = 4
                                                        }), refused: false);

        // Act
        var result = await OpenAsync(harness);

        // Assert
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var accept = (AcceptChannel2Message)Assert.Single(harness.Transcript, t => t.Message is AcceptChannel2Message)
                                                  .Message;
        Assert.Equal((ushort)300, accept.Payload.ToSelfDelay);
        Assert.Equal((ushort)25, accept.Payload.MaxAcceptedHtlcs);
        Assert.Equal(4U, accept.Payload.MinimumDepth);
        var channel = harness.Bob.Channel(result.ChannelId);
        Assert.Equal((ushort)300, channel.ChannelParams.Local.ToSelfDelay);
        Assert.Equal(4U, channel.ChannelParams.MinimumDepth);
    }

    [Fact]
    public async Task Given_BobsDeciderSetsAReserve_When_AliceOpens_Then_TheOpenIsRefused()
    {
        // Arrange
        await using var harness = await CreateAsync(new ChannelOpenDecisionGateTests.FixedDecider(
                                                        new ChannelOpenDecision
                                                        {
                                                            Accept = true,
                                                            ChannelReserve = LightningMoney.Satoshis(20_000)
                                                        }));

        // Act
        var result = await OpenAsync(harness);

        // Assert
        Assert.NotNull(result.FailureReason);
        Assert.IsType<ChannelErrorException>(Assert.Single(harness.Bob.Errors));
        Assert.Empty(harness.Bob.Memory.FindChannels(_ => true));
    }

    /// <param name="refused">The open is refused: Alice gives it up after a short open timeout (as the other refusal
    /// tests).</param>
    private static async Task<DualFundHarness> CreateAsync(IChannelOpenDecider decider, bool refused = true)
    {
        var harness = await DualFundHarness.CreateAsync(400_000, refused ? TimeSpan.FromSeconds(1) : null);
        harness.Bob.OpenDecisionGate.Register(decider);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        return harness;
    }

    private static async Task<DualFundedOpenResult> OpenAsync(DualFundHarness harness) =>
        await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                   new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                   TestContext.Current.CancellationToken));
}
namespace NLightning.Application.Tests.Channels.Acceptance;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Acceptance;
using Domain.Channels.DualFunding.Models;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using DualFunding;
using InteractiveTx.TestDoubles;

/// <summary>
/// NL-1180/NL-1181 on the dual-funded accepter (<see cref="DualFundHarness"/>): an external decider answers before Bob
/// makes keys for the open; its rejection is Alice's error, its values are what <c>accept_channel2</c> announces (its
/// upfront_shutdown included), and a reserve_sat applies when the reserve BOLT 2 fixes for v2 meets it.
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
    public async Task Given_BobsDeciderSetsAReserveTheV2ReserveMeets_When_AliceOpens_Then_ItOpensWithTheV2Reserve()
    {
        // Arrange: 1 % of 600,000 + 400,000 sat is 10,000 sat, above the acceptor's 5,000 sat (and 1 % of Alice's
        // share alone, 6,000 sat, too: Bob's contribution is not needed for it)
        await using var harness = await CreateAsync(new ChannelOpenDecisionGateTests.FixedDecider(
                                                        new ChannelOpenDecision
                                                        {
                                                            Accept = true,
                                                            ChannelReserve = LightningMoney.Satoshis(5_000)
                                                        }), refused: false);

        // Act
        var result = await OpenAsync(harness);

        // Assert
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var channel = harness.Bob.Channel(result.ChannelId);
        Assert.Equal(LightningMoney.Satoshis(10_000), channel.ChannelParams.Local.ChannelReserveAmount);
        Assert.Equal(LightningMoney.Satoshis(10_000), channel.ChannelParams.Remote.ChannelReserveAmount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AReserveOnlyBobsShareMeets_When_AliceOpens_Then_BobMustFundIt(bool bobHasFunds)
    {
        // Arrange: 8,000 sat is met by the 10,000 sat reserve of the whole channel, not by the 6,000 sat of Alice's
        // share alone, so Bob's contribution is all or nothing
        await using var harness = await CreateAsync(new ChannelOpenDecisionGateTests.FixedDecider(
                                                        new ChannelOpenDecision
                                                        {
                                                            Accept = true,
                                                            ChannelReserve = LightningMoney.Satoshis(8_000)
                                                        }), refused: !bobHasFunds, bobHasFunds: bobHasFunds);

        // Act
        var result = await OpenAsync(harness);

        // Assert
        if (bobHasFunds)
        {
            Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
            Assert.Equal(LightningMoney.Satoshis(10_000),
                         harness.Bob.Channel(result.ChannelId).ChannelParams.Local.ChannelReserveAmount);
        }
        else
        {
            Assert.NotNull(result.FailureReason);
            var error = Assert.IsType<ChannelErrorException>(Assert.Single(harness.Bob.Errors));
            Assert.Equal(ChannelOpenDecision.GenericRejection, error.PeerMessage);
            Assert.Empty(harness.Bob.Memory.FindChannels(_ => true));
        }
    }

    [Fact]
    public async Task Given_BobsDeciderSetsAnUpfrontScript_When_AliceOpens_Then_AcceptChannel2CarriesIt()
    {
        // Arrange
        var script = new BitcoinScript([0x00, 0x14, .. Enumerable.Repeat((byte)0x07, 20)]);
        await using var harness = await CreateAsync(new ChannelOpenDecisionGateTests.FixedDecider(
                                                        new ChannelOpenDecision
                                                        {
                                                            Accept = true,
                                                            UpfrontShutdownScript = script
                                                        }), refused: false);
        harness.NegotiatedFeatures = new FeatureOptions
        {
            DualFund = FeatureSupport.Optional,
            UpfrontShutdownScript = FeatureSupport.Optional
        };

        // Act
        var result = await OpenAsync(harness);

        // Assert
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var accept = (AcceptChannel2Message)Assert.Single(harness.Transcript, t => t.Message is AcceptChannel2Message)
                                                  .Message;
        Assert.Equal(script, accept.UpfrontShutdownScriptTlv!.ShutdownScriptPubkey);
        Assert.Equal(script, harness.Bob.Channel(result.ChannelId).LocalUpfrontShutdownScript);
        Assert.Equal(script, harness.Alice.Channel(result.ChannelId).ChannelParams.Remote.UpfrontShutdownScript);
    }

    [Fact]
    public async Task Given_AnUpfrontScriptWithoutTheFeature_When_AliceOpens_Then_TheOpenIsRefused()
    {
        // Arrange: LND's errUpfrontShutdownScriptNotSupported
        await using var harness = await CreateAsync(new ChannelOpenDecisionGateTests.FixedDecider(
                                                        new ChannelOpenDecision
                                                        {
                                                            Accept = true,
                                                            UpfrontShutdownScript = new BitcoinScript(
                                                                [0x00, 0x14, .. Enumerable.Repeat((byte)0x07, 20)])
                                                        }));

        // Act
        var result = await OpenAsync(harness);

        // Assert
        Assert.NotNull(result.FailureReason);
        Assert.IsType<ChannelErrorException>(Assert.Single(harness.Bob.Errors));
        Assert.Empty(harness.Bob.Memory.FindChannels(_ => true));
    }

    [Fact]
    public async Task Given_BobsDeciderSetsAReserveAboveTheV2Reserve_When_AliceOpens_Then_TheOpenIsRefused()
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
    /// <param name="bobHasFunds">Bob's wallet can fund his 400,000 sat contribution.</param>
    private static async Task<DualFundHarness> CreateAsync(IChannelOpenDecider decider, bool refused = true,
                                                           bool bobHasFunds = true)
    {
        var harness = await DualFundHarness.CreateAsync(400_000, refused ? TimeSpan.FromSeconds(1) : null);
        harness.Bob.OpenDecisionGate.Register(decider);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        if (bobHasFunds)
            harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        return harness;
    }

    private static async Task<DualFundedOpenResult> OpenAsync(DualFundHarness harness) =>
        await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                   new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                   TestContext.Current.CancellationToken));
}
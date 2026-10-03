namespace NLightning.Application.Tests.Channels.DualFunding;

using Application.Channels.RoutingPolicies;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using InteractiveTx.TestDoubles;

/// <summary>
/// The <c>max_htlc_value_in_flight_msat</c> of a dual-funded open (NL-880, NL-881) on <see cref="DualFundHarness"/>.
/// BOLT 2 fixes the value each side announces in <c>open_channel2</c>/<c>accept_channel2</c> for the channel's
/// lifetime (no RBF attempt or splice changes it), and BOLT 7 caps the <c>channel_update</c>'s
/// <c>htlc_maximum_msat</c> at the peer's value. The 2026-10-03 Mutinynet liquidity-ads test: FAFO opened 60k and
/// bought 80k from FAFO2, announced 80 % of its own 60k (48,000,000 msat) while it stored 80 % of the 140k channel,
/// and after a splice to 210k FAFO2 still could send at most 48,000,000 msat.
/// </summary>
public class DualFundInFlightLimitTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);
    private const long BobShareSat = 400_000;

    [Fact]
    public async Task Given_NoSplice_When_BobContributes_Then_EachSideStoresTheInFlightLimitItAnnounced()
    {
        // Arrange: no option_splice, so each side announces 80 % of what it knows of the channel
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        PinNoSplice(harness);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));

        // Act
        var result = await OpenAsync(harness);

        // Assert: Alice announced 80 % of her 600,000 sat (Bob's share was unknown), Bob 80 % of the 1,000,000 sat
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var (open, accept) = Announced(harness);
        Assert.Equal(LightningMoney.Satoshis(480_000), open);
        Assert.Equal(LightningMoney.Satoshis(800_000), accept);
        // ...and each side's channel holds exactly those values: what binds the peer is what went on the wire (before
        // NL-881 Alice stored 80 % of the whole channel, 800,000 sat, while Bob was told 480,000 sat)
        await AssertInFlightAsync(harness, result.ChannelId, open, accept);
    }

    [Fact]
    public async Task Given_NoSplice_When_AliceBuysLiquidity_Then_HerInFlightLimitCountsTheLiquidityBought()
    {
        // Arrange: the seller contributes at least the amount bought, so Alice knows the channel is at least her share
        // plus it when she sends open_channel2
        await using var harness = await LiquidityAdsKit.CreateAsync();
        PinNoSplice(harness);

        // Act
        var result = await LiquidityAdsKit.OpenAsync(harness);

        // Assert: 80 % of 600,000 + 400,000 sat (before NL-881: 80 % of her 600,000 sat, 480,000 sat)
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var (open, accept) = Announced(harness);
        Assert.Equal(LightningMoney.Satoshis(800_000), open);
        await AssertInFlightAsync(harness, result.ChannelId, open, accept);
    }

    [Fact]
    public async Task Given_NoSplice_When_AnRbfChangesTheCapacity_Then_BothInFlightLimitsStayTheAnnouncedOnes()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        PinNoSplice(harness);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var (open, accept) = Announced(harness);

        // Act: Alice adds 50,000 sat in her RBF; the capacity becomes 1,050,000 sat
        var bump = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(first.ChannelId, 5_000,
                                                                           LightningMoney.Satoshis(650_000),
                                                                           TestContext.Current.CancellationToken));

        // Assert: the reserve followed the capacity, the in-flight limits did not (no message carries them again;
        // before NL-881 both sides recomputed theirs to 80 % of the new capacity)
        Assert.True(bump.FailureReason is null, $"{bump.FailureReason}\n{harness.Describe()}");
        Assert.Equal(LightningMoney.Satoshis(1_050_000), harness.Alice.Channel(first.ChannelId).FundingOutput!.Amount);
        await AssertInFlightAsync(harness, first.ChannelId, open, accept);
    }

    [Fact]
    public async Task Given_SpliceNegotiated_When_Opened_Then_NeitherSideCapsTheInFlightAndTheHtlcMaximumIsTheCapacity()
    {
        // Arrange: option_splice is on by default (D13): the channel can grow, the announced value cannot (NL-880)
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));

        // Act
        var result = await OpenAsync(harness);

        // Assert: both announce the u64 maximum, both store it (the database round trip included), and each side's
        // channel_update may announce the whole capacity
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var (open, accept) = Announced(harness);
        Assert.Equal(ulong.MaxValue, open.MilliSatoshi);
        Assert.Equal(ulong.MaxValue, accept.MilliSatoshi);
        await AssertInFlightAsync(harness, result.ChannelId, open, accept);
        foreach (var node in harness.Nodes)
        {
            var channel = node.Channel(result.ChannelId);
            var policy = ChannelPolicyRules.Resolve(channel, new RoutingOptions(), null);
            Assert.Equal(LightningMoney.Satoshis(1_000_000).MilliSatoshi, policy.HtlcMaximumMsat);
        }
    }

    private static void PinNoSplice(DualFundHarness harness)
    {
        harness.NegotiatedFeatures = new FeatureOptions
        {
            DualFund = FeatureSupport.Optional,
            OptionSplice = FeatureSupport.No
        };
        foreach (var node in harness.Nodes)
            node.Options.Features.OptionSplice = FeatureSupport.No;
    }

    private static (LightningMoney Open, LightningMoney Accept) Announced(DualFundHarness harness)
    {
        var open = (OpenChannel2Message)Assert.Single(harness.Transcript, t => t.Message is OpenChannel2Message).Message;
        var accept = (AcceptChannel2Message)Assert.Single(harness.Transcript, t => t.Message is AcceptChannel2Message)
                                                  .Message;
        return (open.Payload.MaxHtlcValueInFlightAmount, accept.Payload.MaxHtlcValueInFlightAmount);
    }

    /// <summary>
    /// Alice's local limit and Bob's remote one are <paramref name="open"/>, Bob's local and Alice's remote
    /// <paramref name="accept"/>: in memory and in the database.
    /// </summary>
    private static async Task AssertInFlightAsync(DualFundHarness harness, ChannelId channelId, LightningMoney open,
                                                  LightningMoney accept)
    {
        foreach (var (node, local, remote) in new[] { (harness.Alice, open, accept), (harness.Bob, accept, open) })
        {
            var channel = node.Channel(channelId);
            Assert.Equal(local, channel.ChannelParams.Local.MaxHtlcValueInFlight);
            Assert.Equal(remote, channel.ChannelParams.Remote.MaxHtlcValueInFlight);
            var stored = await node.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
            Assert.Equal(local, stored!.ChannelParams.Local.MaxHtlcValueInFlight);
            Assert.Equal(remote, stored.ChannelParams.Remote.MaxHtlcValueInFlight);
        }
    }

    private static async Task<DualFundedOpenResult> OpenAsync(DualFundHarness harness) =>
        await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                   new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                   TestContext.Current.CancellationToken));
}
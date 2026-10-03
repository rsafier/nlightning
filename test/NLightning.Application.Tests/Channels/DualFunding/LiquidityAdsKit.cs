using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Application.LiquidityAds;
using Domain.Accounting.Models;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.ValueObjects;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Node.Options;
using InteractiveTx.TestDoubles;

/// <summary>
/// Liquidity ads (NL-850) on <see cref="DualFundHarness"/>: Bob sells at <see cref="Rate"/>, Alice buys with her
/// dual-funded open, each node seeing the other's rates as its <c>init</c> would carry them (mocked peer services).
/// </summary>
[ExcludeFromCodeCoverage]
internal static class LiquidityAdsKit
{
    public static readonly LightningMoney AliceShare = LightningMoney.Satoshis(600_000);
    public const ulong RequestedSat = 400_000;

    /// <summary>Bob's rate: 100,000-1,000,000 sat, weight 400, 1 %, 500 sat base, 1,000 sat for a new channel.</summary>
    public static FundingRateOptions Rate() => new()
    {
        MinAmountSat = 100_000,
        MaxAmountSat = 1_000_000,
        FundingWeight = 400,
        FeeBasis = 100,
        FeeBaseSat = 500,
        ChannelCreationFeeSat = 1_000
    };

    /// <summary>The fee of <paramref name="requestedSat"/> at Bob's rate and <paramref name="feeratePerKw"/>.</summary>
    public static LiquidityFees Fees(uint feeratePerKw, ulong requestedSat = RequestedSat) =>
        LiquidityAdsRules.ComputeFees(Rate().ToFundingRate(), feeratePerKw, requestedSat, requestedSat, true);

    /// <summary>A harness where Bob sells, Alice has 1,000,000 sat and Bob <paramref name="bobWalletSat"/>.</summary>
    public static async Task<DualFundHarness> CreateAsync(long bobWalletSat = 700_000, TimeSpan? openTimeout = null,
                                                          bool bobSells = true)
    {
        var harness = await DualFundHarness.CreateAsync(0, openTimeout, withPeerServices: true);
        if (bobSells)
            harness.Bob.Options.LiquidityAds.FundingRates.Add(Rate());
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        if (bobWalletSat > 0)
            harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(bobWalletSat));
        return harness;
    }

    /// <summary>Alice opens with her 600,000 sat and buys <paramref name="liquidity"/> (400,000 sat by default).</summary>
    public static Task<DualFundedOpenResult> OpenAsync(DualFundHarness harness, LiquidityRequest? liquidity = null,
                                                       uint feeratePerKw = 2_500) =>
        harness.RunAsync(StartOpen(harness, liquidity, feeratePerKw));

    /// <summary>Alice's open, started and not pumped.</summary>
    public static Task<DualFundedOpenResult> StartOpen(DualFundHarness harness, LiquidityRequest? liquidity = null,
                                                       uint feeratePerKw = 2_500) =>
        harness.Alice.DualFund.OpenAsync(new DualFundedOpenRequest(harness.Bob.NodeId, AliceShare, feeratePerKw)
        {
            Liquidity = liquidity ?? new LiquidityRequest(RequestedSat)
        }, TestContext.Current.CancellationToken);

    /// <summary>
    /// Both nodes' channel balances, in memory and stored: the shares with the fee <paramref name="feeMsat"/> moved
    /// from Alice (the buyer) to Bob (the seller).
    /// </summary>
    public static async Task AssertBalancesAsync(DualFundHarness harness, ChannelId channelId, ulong aliceShareSat,
                                                 ulong bobShareSat, ulong feeMsat)
    {
        var alice = LightningMoney.MilliSatoshis(aliceShareSat * 1_000 - feeMsat);
        var bob = LightningMoney.MilliSatoshis(bobShareSat * 1_000 + feeMsat);
        foreach (var (node, local, remote) in new[] { (harness.Alice, alice, bob), (harness.Bob, bob, alice) })
        {
            var channel = node.Channel(channelId);
            Assert.Equal(local, channel.LocalBalance);
            Assert.Equal(remote, channel.RemoteBalance);
            Assert.Equal(LightningMoney.Satoshis(aliceShareSat + bobShareSat), channel.FundingOutput!.Amount);
            var stored = await node.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
            Assert.Equal(local, stored!.LocalBalance);
            Assert.Equal(remote, stored.RemoteBalance);
        }
    }

    public static Task<IReadOnlyList<LiquidityPurchaseModel>> PurchasesAsync(DualFundNode node, ChannelId channelId) =>
        node.InScopeAsync(u => u.LiquidityPurchaseDbRepository.GetByChannelIdAsync(channelId));

    public static Task<IReadOnlyList<AccountingEventModel>> EventsAsync(DualFundNode node) =>
        node.InScopeAsync(u => u.AccountingEventDbRepository.GetUnsealedAsync(100));

    public static LiquidityAdsService LiquidityAds(DualFundNode node) =>
        node.Services.GetRequiredService<LiquidityAdsService>();
}
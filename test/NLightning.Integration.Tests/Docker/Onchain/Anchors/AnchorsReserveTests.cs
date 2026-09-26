using System.Globalization;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Abcd;
using Domain.Bitcoin.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Fixtures;
using Utils;

/// <summary>
/// The on-chain reserve of anchors channels (NL-379, O7-T4 gap (c)), against LND david: with anchors our commitment
/// pays a low fee and our HTLC transactions none, so every anchors channel needs wallet coins for its CPFP child and
/// fee inputs. With <c>Node:Anchors:ReservePerChannel</c> set to <see cref="ReservePerChannelSat"/>:
/// <list type="bullet">
///   <item>(a) an anchors open that would leave the wallet below the reserve is refused before anything is sent, with
///   an error that names the reserve; nothing stays locked, and once the wallet has the reserve on top the same open
///   goes through and leaves at least the reserve.</item>
///   <item>(b) with one anchors channel open, a second funding that would spend into that channel's reserve is
///   refused the same way; a smaller one that leaves the reserve of both channels goes through.</item>
/// </list>
/// </summary>
/// <remarks>
/// The amounts leave a margin of tens of thousands of sat around every threshold (the funding fee at the 10 sat/vB
/// estimate is about 1,500 sat), and hold whether or not the total reserve is capped (LND caps it at 100,000 sat; the
/// cases need at most two channels' worth). The refusal is read from the open's exception message, which must mention
/// the reserve; nothing reads the reserve service itself. Run with <c>ONCHAIN_SUITE=anchors scripts/run-onchain.sh</c>.
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
[Trait("Category", AnchorsChannelTests.AnchorsCategory)]
public class AnchorsReserveTests : IAsyncLifetime
{
    /// <summary>The configuration key of the per-channel reserve (satoshis).</summary>
    public const string ReservePerChannelKey = "Node:Anchors:ReservePerChannel";

    /// <summary>
    /// 100,000 sat per anchors channel: ten times LND's default, so the thresholds sit well clear of the funding fee.
    /// </summary>
    private const long ReservePerChannelSat = 100_000;

    private readonly AnchorsHarness _harness;
    private NLightningTestNode? _node;

    public AnchorsReserveTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await _harness.CreateNodeAsync("anchors-reserve", TestContext.Current.CancellationToken,
                                               beforeStart: node => node.ExtraConfiguration[ReservePerChannelKey] =
                                                                        ReservePerChannelSat.ToString(
                                                                            CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Given_WalletWithoutTheReserve_When_OpeningAnAnchorsChannel_Then_RefusedWithAReserveErrorAndOpenedOnceTheReserveIsThere()
    {
        // Arrange: 1,050,000 sat for a 1,000,000 sat channel: about 48,500 sat would be left, below the reserve
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        await _harness.EnsureLndWalletFundedAsync(david, ct);
        await Node.FundWalletAsync(LightningMoney.Satoshis(1_050_000), Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        var peerAddress = await Node.ConnectToAsync(david, ct);
        var before = ConfirmedBalance(Node);

        // Act
        var refusal = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(peerAddress, AnchorsHarness.Capacity,
                                                                               ct));

        // Assert: a reserve error, no channel, no coin locked
        Console.WriteLine($"Open refused: {refusal.GetType().Name}: {refusal.Message}");
        AssertReserveRefusal(refusal);
        Assert.Empty((await Node.ListChannelsAsync(ct)).Channels);
        Assert.Equal(before, ConfirmedBalance(Node));

        // With the reserve on top the same open goes through (the refused one locked nothing) and leaves the reserve
        await Node.FundWalletAsync(LightningMoney.Satoshis(100_000), Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        var channel = await OpenUsableAsync(david, peerAddress, AnchorsHarness.Capacity, ct);
        Assert.True(AnchorsHarness.GetModel(Node, channel.ChannelId).ChannelParams.OptionAnchorOutputs,
                    "not an anchors channel");
        AssertWalletKeepsReserve(1);
    }

    [Fact]
    public async Task Given_AnOpenAnchorsChannel_When_ASecondFundingWouldSpendIntoItsReserve_Then_RefusedAndASmallerOneGoesThrough()
    {
        // Arrange: one anchors channel (the harness funds 2,000,000 sat, about 998,500 sat are left)
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        await _harness.OpenAnchorsChannelAsync(Node, david, null, ct);
        var peerAddress = Convert.ToHexString(david.LocalNodePubKeyBytes); // already connected: the node id is enough
        var before = ConfirmedBalance(Node);
        Console.WriteLine($"Wallet after the first channel: {before} confirmed");

        // Act: 950,000 sat would leave about 47,000 sat, below one channel's reserve already
        var refusal = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(peerAddress,
                                                                               LightningMoney.Satoshis(950_000), ct));

        // Assert: refused, nothing locked; 700,000 sat leave about 297,000 sat (both channels' reserve) and go through
        Console.WriteLine($"Second open refused: {refusal.GetType().Name}: {refusal.Message}");
        AssertReserveRefusal(refusal);
        Assert.Single((await Node.ListChannelsAsync(ct)).Channels);
        Assert.Equal(before, ConfirmedBalance(Node));

        await OpenUsableAsync(david, peerAddress, LightningMoney.Satoshis(700_000), ct);
        AssertWalletKeepsReserve(2);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync(["david"]);
        GC.SuppressFinalize(this);
    }

    private static void AssertReserveRefusal(Exception refusal) =>
        Assert.Contains("reserve", refusal.Message, StringComparison.OrdinalIgnoreCase);

    private static LightningMoney ConfirmedBalance(NLightningTestNode node) =>
        node.Services.GetRequiredService<IUtxoMemoryRepository>()
            .GetConfirmedBalance(node.BlockchainMonitor.LastProcessedBlockHeight);

    /// <summary>The wallet (confirmed and unconfirmed) still holds the reserve of <paramref name="channels"/> channels.</summary>
    private void AssertWalletKeepsReserve(int channels)
    {
        var balance = AnchorsHarness.WalletBalance(Node);
        var reserve = LightningMoney.Satoshis(ReservePerChannelSat * channels);
        Console.WriteLine($"Wallet after the open: {balance}, reserve of {channels} anchors channel(s) {reserve}");
        Assert.True(balance >= reserve, $"wallet {balance} below the reserve {reserve}");
    }

    private Task<OpenChannelClientSubscriptionResponse> OpenAsync(string peerAddress, LightningMoney amount,
                                                                  CancellationToken ct) =>
        Node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress, amount)
        {
            FeeRatePerKw = AnchorsHarness.EstimateFeeRatePerKw
        }, ct);

    /// <summary>Opens a channel and waits until both ends use it.</summary>
    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableAsync(
        LNDNodeConnection david, string peerAddress, LightningMoney amount, CancellationToken ct)
    {
        var channel = await OpenAsync(peerAddress, amount, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) of {amount}");
        await Poll.UntilAsync(async () =>
        {
            var ours = await Node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(david, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
                return true;

            await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [Node], ct);
            return false;
        }, AnchorsHarness.Timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        return channel;
    }
}
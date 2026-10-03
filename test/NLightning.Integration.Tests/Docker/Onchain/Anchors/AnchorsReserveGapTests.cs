using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Testing.Lnd;

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
/// fee inputs. With <c>Node:Anchors:ReservePerChannel</c> set to <see cref="ReservePerChannelSat"/> and
/// <c>Node:Anchors:MaxReserve</c> to <see cref="MaxReserveSat"/> (so the reserve of two channels, 80,000 sat, stays
/// below the cap and the second channel counts):
/// <list type="bullet">
///   <item>(a) an anchors open that would leave the wallet below the reserve is refused before anything is sent, with
///   an error that names the reserve; nothing stays locked, and once the wallet has the reserve on top the same open
///   goes through and leaves at least the reserve.</item>
///   <item>(b) with one anchors channel open, a second funding that would spend into that channel's reserve is
///   refused the same way although it leaves the new channel's own reserve (it leaves 60,000 sat, between one and
///   two channels' reserve); a smaller one that leaves the reserve of both channels goes through.</item>
/// </list>
/// </summary>
/// <remarks>
/// The reserve is <c>min(ReservePerChannel x anchors channels, MaxReserve)</c> in satoshis, checked against the
/// confirmed wallet balance that is not locked (<c>available &gt;= funding + reserve</c>; the funding fee is not
/// counted). The amounts leave a margin of about 20,000 sat around every threshold (the funding fee at the 10 sat/vB
/// estimate is about 1,500 sat). The refusal is read from the open's exception message, which must name the anchors
/// reserve; that nothing stays locked is read from <c>IUtxoMemoryRepository.GetLockedBalance</c> (channel locks and
/// fee reservations), since the confirmed balance counts locked outputs too. Run with
/// <c>scripts/run-cluster.sh -n 1 --suite anchors</c>.
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
[Trait("Category", AnchorsChannelTests.AnchorsCategory)]
public class AnchorsReserveGapTests : IAsyncLifetime
{
    /// <summary>The configuration key of the per-channel reserve (satoshis).</summary>
    public const string ReservePerChannelKey = "Node:Anchors:ReservePerChannel";

    /// <summary>The configuration key of the reserve cap (satoshis).</summary>
    public const string MaxReserveKey = "Node:Anchors:MaxReserve";

    /// <summary>
    /// 40,000 sat per anchors channel: four times LND's default, so the thresholds sit well clear of the funding fee,
    /// and two channels' worth (80,000 sat) stays below <see cref="MaxReserveSat"/>.
    /// </summary>
    private const long ReservePerChannelSat = 40_000;

    /// <summary>The reserve cap, set explicitly (it is also the default) so that the second channel counts.</summary>
    private const long MaxReserveSat = 100_000;

    private readonly AnchorsHarness _harness;
    private NLightningTestNode? _node;

    public AnchorsReserveGapTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await _harness.CreateNodeAsync("anchors-reserve", TestContext.Current.CancellationToken,
                                               beforeStart: node =>
                                               {
                                                   node.ExtraConfiguration[ReservePerChannelKey] =
                                                       ReservePerChannelSat.ToString(CultureInfo.InvariantCulture);
                                                   node.ExtraConfiguration[MaxReserveKey] =
                                                       MaxReserveSat.ToString(CultureInfo.InvariantCulture);
                                               });
    }

    [Fact]
    public async Task Given_WalletWithoutTheReserve_When_OpeningAnAnchorsChannel_Then_RefusedWithAReserveErrorAndOpenedOnceTheReserveIsThere()
    {
        // Arrange: 1,020,000 sat for a 1,000,000 sat channel: 20,000 sat would be left, below the 40,000 sat reserve
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        await _harness.EnsureLndWalletFundedAsync(david, ct);
        await Node.FundWalletAsync(LightningMoney.Satoshis(1_020_000), Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        var peerAddress = await Node.ConnectToAsync(david, ct);
        await WaitAvailableAsync(david, LightningMoney.Satoshis(1_020_000), ct);
        var before = ConfirmedBalance(Node);
        Assert.Equal(LightningMoney.Zero, LockedBalance(Node));

        // Act
        var refusal = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(peerAddress, AnchorsHarness.Capacity,
                                                                               ct));

        // Assert: a reserve error, no channel, no coin locked or reserved
        Console.WriteLine($"Open refused: {refusal.GetType().Name}: {refusal.Message}");
        AssertReserveRefusal(refusal);
        Assert.Empty((await Node.ListChannelsAsync(ct)).Channels);
        Assert.Equal(before, ConfirmedBalance(Node));
        Assert.Equal(LightningMoney.Zero, LockedBalance(Node));

        // With the reserve on top the same open goes through (the refused one locked nothing) and leaves the reserve:
        // 1,120,000 sat leave about 118,500 sat after the fee
        await Node.FundWalletAsync(LightningMoney.Satoshis(100_000), Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        await WaitAvailableAsync(david, LightningMoney.Satoshis(1_120_000), ct);
        var channel = await OpenUsableAsync(david, peerAddress, AnchorsHarness.Capacity, ct);
        Assert.True(AnchorsHarness.GetModel(Node, channel.ChannelId).ChannelParams.OptionAnchorOutputs,
                    "not an anchors channel");
        AssertWalletKeepsReserve(1);
    }

    [Fact]
    public async Task Given_AnOpenAnchorsChannel_When_ASecondFundingWouldSpendIntoItsReserve_Then_RefusedAndASmallerOneGoesThrough()
    {
        // Arrange: one anchors channel (the harness funds 2,000,000 sat, about 998,500 sat are left once the change
        // is confirmed)
        var ct = TestContext.Current.CancellationToken;
        var david = _harness.Fixture.GetLndNode("david");
        await _harness.OpenAnchorsChannelAsync(Node, david, null, ct);
        var peerAddress = Convert.ToHexString(david.LocalNodePubKeyBytes); // already connected: the node id is enough
        await WaitAvailableAsync(david, LightningMoney.Satoshis(990_000), ct);
        var available = AvailableBalance(Node);
        var before = ConfirmedBalance(Node);
        var lockedBefore = LockedBalance(Node);
        Console.WriteLine($"Wallet after the first channel: {available} available, {lockedBefore} locked");
        var refusedAmount = available - LightningMoney.Satoshis(60_000);
        var acceptedAmount = available - LightningMoney.Satoshis(100_000);

        // Act: the refused funding leaves 60,000 sat: enough for the new channel's own reserve (40,000 sat) but not
        // for both channels' (80,000 sat), so only an implementation that counts the open channel refuses it
        var refusal = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(peerAddress, refusedAmount, ct));

        // Assert: refused, nothing locked; a funding that leaves 100,000 sat (about 98,500 after the fee, above both
        // channels' reserve) goes through
        Console.WriteLine($"Second open of {refusedAmount} refused: {refusal.GetType().Name}: {refusal.Message}");
        AssertReserveRefusal(refusal);
        Assert.Single((await Node.ListChannelsAsync(ct)).Channels);
        Assert.Equal(before, ConfirmedBalance(Node));
        Assert.Equal(lockedBefore, LockedBalance(Node));

        await OpenUsableAsync(david, peerAddress, acceptedAmount, ct);
        AssertWalletKeepsReserve(2);
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync(["david"]);
        GC.SuppressFinalize(this);
    }

    private static void AssertReserveRefusal(Exception refusal) =>
        Assert.Contains("anchors reserve", refusal.Message, StringComparison.OrdinalIgnoreCase);

    /// <summary>Channel-locked and fee-reserved wallet outputs.</summary>
    private static LightningMoney LockedBalance(NLightningTestNode node) =>
        node.Services.GetRequiredService<IUtxoMemoryRepository>().GetLockedBalance();

    /// <summary>
    /// The confirmed wallet balance less the locked and fee-reserved outputs: what the reserve check counts as
    /// available (it also leaves out outputs spent by our pending broadcasts, none here).
    /// </summary>
    private static LightningMoney AvailableBalance(NLightningTestNode node)
    {
        var confirmed = ConfirmedBalance(node);
        var locked = LockedBalance(node);
        return confirmed > locked ? confirmed - locked : LightningMoney.Zero;
    }

    /// <summary>
    /// Mines until <see cref="AvailableBalance"/> reaches <paramref name="amount"/> (the wallet counts an output as
    /// confirmed three blocks after the one that includes it).
    /// </summary>
    private Task WaitAvailableAsync(LndNodeConnection david, LightningMoney amount, CancellationToken ct) =>
        Poll.UntilAsync(async () =>
        {
            if (AvailableBalance(Node) >= amount)
                return true;

            await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [david], [Node], ct);
            return false;
        }, AnchorsHarness.Timeout, $"{amount} confirmed and available", ct);

    private static LightningMoney ConfirmedBalance(NLightningTestNode node) =>
        node.Services.GetRequiredService<IUtxoMemoryRepository>()
            .GetConfirmedBalance(node.BlockchainMonitor.LastProcessedBlockHeight);

    /// <summary>
    /// The wallet (confirmed and unconfirmed) still holds the reserve of <paramref name="channels"/> channels,
    /// <c>min(ReservePerChannel x channels, MaxReserve)</c>.
    /// </summary>
    private void AssertWalletKeepsReserve(int channels)
    {
        var balance = AnchorsHarness.WalletBalance(Node);
        var reserve = LightningMoney.Satoshis(Math.Min(ReservePerChannelSat * channels, MaxReserveSat));
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
        LndNodeConnection david, string peerAddress, LightningMoney amount, CancellationToken ct)
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
using System.Security.Cryptography;
using Google.Protobuf;
using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Application.Channels.Fees;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Money;
using Domain.Payments.Enums;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// BOLT2 plan Proof N9 (fees, N9-T1): the funder's <see cref="FeeUpdateScheduler"/> sends <c>update_fee</c> when the
/// estimate moves, the peer accepts it, and payments keep working on the channel afterwards, both on a channel we fund
/// to LND and on an LND-funded channel to us.
/// </summary>
/// <remarks>
/// The scheduler is built from the node's own services with a fixed estimate (<see cref="FixedFeeService"/>), and its
/// rounds are run by hand. An <c>update_fee</c> <b>from LND</b> is not provoked: LND's link samples the fee on a random
/// 10-60 minute timer (<c>DefaultMin/MaxLinkFeeUpdateTimeout</c>, not configurable in a release build), so the
/// receiving side is proven between two of our nodes instead (<see cref="Given_OurNodeFundsAChannelToOurNode_When_FunderSendsUpdateFee_Then_TheFundeeAcceptsAndPaymentsWork"/>),
/// and the LND-funded channel shows that we are a correct non-funder (payments both ways, balances agree to LND's
/// commitment fee, and we never send <c>update_fee</c>).
/// </remarks>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class FeeUpdateFlowTests : IAsyncLifetime
{
    private const uint OpeningFeeratePerKw = 10_000;

    /// <summary>LND 0.20 asks for a <c>to_self_delay</c> that grows with the capacity; accept it (see AbcdNetwork).
    /// </summary>
    private const ushort ToSelfDelay = 240;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_mineInterval = TimeSpan.FromSeconds(1);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public FeeUpdateFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// We fund a (legacy, non-anchor) channel to alice at 10,000 sat/kw. The estimate rises to 6,250 sat/kw: with the
    /// default 200 % non-anchor margin our scheduler sends one <c>update_fee</c> of 12,500 sat/kw, LND commits it (its
    /// <c>fee_per_kw</c> follows) and payments work both ways; then the estimate falls to 2,500 sat/kw (5,000 with the
    /// margin) and LND follows again.
    /// </summary>
    [Fact]
    public async Task Given_OurFundedChannel_When_EstimateMoves_Then_LndAcceptsOurUpdateFeeAndPaymentsWork()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var node = await StartNodeAsync("fee-funder", ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [alice], [node], ct);
        var aliceAddress = await node.ConnectToAsync(alice, ct);
        var opened = await node.OpenChannelAsync(new OpenChannelClientRequest(aliceAddress, s_capacity)
        {
            PushAmount = s_push,
            FeeRatePerKw = LightningMoney.Satoshis(OpeningFeeratePerKw)
        }, ct);
        var channelId = opened.ChannelId;
        var channelPoint = opened.ChannelPoint();
        await MineUntilUsableAsync(node, alice, channelId, channelPoint, ct);
        var feeService = new FixedFeeService(6_250);
        var scheduler = CreateScheduler(node, feeService);

        // Act 1 - the estimate, with the default margin, is 25 % above the feerate
        var outcome = Assert.Single(await scheduler.RunOnceAsync(ct));

        // Assert 1 - LND committed our feerate and the channel is still usable
        Assert.True(outcome.Sent, outcome.Reason);
        Assert.Equal(12_500U, outcome.Decision.FeeratePerKw);
        await WaitForLndFeerateAsync(alice, channelPoint, 12_500, ct);
        await WaitForOurFeerateAsync(node, channelId, 12_500, ct);
        await AssertBalancesAgreeAsync(node, alice, channelId, channelPoint, weAreFunder: true, ct);

        // Act 2 - payments both ways at the new feerate
        await AssertLndPaysUsAsync(node, alice, channelId, channelPoint, LightningMoney.Satoshis(40_000), ct);
        await AssertWePayLndAsync(node, alice, LightningMoney.Satoshis(15_000), ct);
        await AssertBalancesAgreeAsync(node, alice, channelId, channelPoint, weAreFunder: true, ct);

        // Act 3 - the estimate falls
        feeService.FeeratePerKw = 2_500;
        var lower = Assert.Single(await scheduler.RunOnceAsync(ct));

        // Assert 3
        Assert.True(lower.Sent, lower.Reason);
        await WaitForLndFeerateAsync(alice, channelPoint, 5_000, ct);
        await WaitForOurFeerateAsync(node, channelId, 5_000, ct);
        await AssertBalancesAgreeAsync(node, alice, channelId, channelPoint, weAreFunder: true, ct);
        await AssertLndPaysUsAsync(node, alice, channelId, channelPoint, LightningMoney.Satoshis(10_000), ct);
    }

    /// <summary>
    /// Alice opens a channel to us (1,000,000 sat, 300,000 pushed): we accept as the non-funder, payments work both
    /// ways, both sides agree on the balances with LND paying the commitment fee, and our scheduler never sends
    /// <c>update_fee</c> on it however the estimate moves (BOLT 2: the non-funder MUST NOT).
    /// </summary>
    [Fact]
    public async Task Given_LndFundedChannel_When_PaymentsFlow_Then_TheyWorkAndWeNeverSendUpdateFee()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var node = await StartNodeAsync("fee-fundee", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [alice], [node], ct);
        await node.ConnectToAsync(alice, ct);

        // Act 1 - alice funds a private channel to us over the connection we made
        var channelPointResponse = await alice.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest
        {
            NodePubkey = ByteString.CopyFrom((byte[])node.NodeId),
            LocalFundingAmount = (long)s_capacity.Satoshi,
            PushSat = (long)s_push.Satoshi,
            Private = true
        }, cancellationToken: ct);
        var channelPoint = ToChannelPoint(channelPointResponse);
        Console.WriteLine($"alice opened {channelPoint} to us");
        var ourEnd = await Poll.ForAsync(async () =>
        {
            var channels = await node.ListChannelsAsync(ct);
            return channels.Channels.Count == 1 ? channels.Channels[0] : null;
        }, s_timeout, "our end of alice's channel", ct);
        var channelId = ourEnd.ChannelId;
        await MineUntilUsableAsync(node, alice, channelId, channelPoint, ct);

        // Assert 1 - LND is the funder
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channelPoint, ct);
        Assert.NotNull(lndChannel);
        Assert.True(lndChannel.Initiator);
        Console.WriteLine($"LND channel type {lndChannel.CommitmentType}, fee_per_kw {lndChannel.FeePerKw}");
        await AssertBalancesAgreeAsync(node, alice, channelId, channelPoint, weAreFunder: false, ct);

        // Act 2 - payments both ways
        await AssertLndPaysUsAsync(node, alice, channelId, channelPoint, LightningMoney.Satoshis(50_000), ct);
        await AssertWePayLndAsync(node, alice, LightningMoney.Satoshis(20_000), ct);

        // Assert 2 - balances agree, and the scheduler leaves a channel we don't fund alone
        await AssertBalancesAgreeAsync(node, alice, channelId, channelPoint, weAreFunder: false, ct);
        var feerateBefore = (await LndTestHelpers.GetChannelByPointAsync(alice, channelPoint, ct))!.FeePerKw;
        var outcomes = await CreateScheduler(node, new FixedFeeService((uint)feerateBefore * 3)).RunOnceAsync(ct);
        Assert.Empty(outcomes);
        Assert.Equal(feerateBefore, (await LndTestHelpers.GetChannelByPointAsync(alice, channelPoint, ct))!.FeePerKw);
    }

    /// <summary>
    /// The receiving side of <c>update_fee</c> on the real stack (TCP, BOLT 8, SQLite): one of our nodes funds a
    /// channel to another; the funder's scheduler raises the feerate (7,500 sat/kw estimate, 15,000 with the default
    /// non-anchor margin), the fundee accepts it and both commit it, and
    /// payments work both ways afterwards.
    /// </summary>
    [Fact]
    public async Task Given_OurNodeFundsAChannelToOurNode_When_FunderSendsUpdateFee_Then_TheFundeeAcceptsAndPaymentsWork()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var funder = await StartNodeAsync("fee-a", ct);
        var fundee = await StartNodeAsync("fee-b", ct);
        await funder.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [funder, fundee], ct);
        await funder.ConnectToAsync(fundee, ct);
        var opened = await funder.OpenChannelAsync(new OpenChannelClientRequest(fundee.Address, s_capacity)
        {
            PushAmount = s_push,
            FeeRatePerKw = LightningMoney.Satoshis(OpeningFeeratePerKw)
        }, ct);
        var channelId = opened.ChannelId;
        await Poll.UntilAsync(async () =>
        {
            var a = (await funder.ListChannelsAsync(ct)).Channels.FirstOrDefault(c => c.ChannelId == channelId);
            var b = (await fundee.ListChannelsAsync(ct)).Channels.FirstOrDefault(c => c.ChannelId == channelId);
            if (a is not null && a.IsUsable() && b is not null && b.IsUsable())
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, _fixture.LndNodes, [funder, fundee], ct);
            return false;
        }, s_timeout, $"channel {channelId} usable on both of our nodes", ct, s_mineInterval);
        await ChainSync.WaitAllAtTipAsync(_fixture, [funder, fundee], ct);

        // Act 1
        var outcome = Assert.Single(await CreateScheduler(funder, new FixedFeeService(7_500)).RunOnceAsync(ct));

        // Assert 1 - the fundee received update_fee and both commitments carry it on both sides
        Assert.True(outcome.Sent, outcome.Reason);
        await WaitForOurFeerateAsync(funder, channelId, 15_000, ct);
        await WaitForOurFeerateAsync(fundee, channelId, 15_000, ct);
        Assert.Empty(await CreateScheduler(fundee, new FixedFeeService(30_000)).RunOnceAsync(ct));

        // Act 2 - payments both ways
        var funderBefore = await funder.GetChannelAsync(channelId, ct);
        var toFundee = await fundee.CreateInvoiceAsync(LightningMoney.Satoshis(30_000), "fee flow a->b", ct);
        var paid = await funder.PayInvoiceAsync(toFundee.Bolt11, ct);
        var toFunder = await funder.CreateInvoiceAsync(LightningMoney.Satoshis(12_000), "fee flow b->a", ct);
        var paidBack = await fundee.PayInvoiceAsync(toFunder.Bolt11, ct);

        // Assert 2
        Assert.Equal(PaymentStatus.Succeeded, paid.Status);
        Assert.Equal(PaymentStatus.Succeeded, paidBack.Status);
        await Poll.UntilAsync(async () =>
        {
            var a = await funder.GetChannelAsync(channelId, ct);
            var b = await fundee.GetChannelAsync(channelId, ct);
            return a.OfferedHtlcCount + a.ReceivedHtlcCount + b.OfferedHtlcCount + b.ReceivedHtlcCount == 0
                && a.LocalBalance == b.RemoteBalance && a.RemoteBalance == b.LocalBalance;
        }, s_timeout, "no HTLC pending and both ends agree", ct);
        var funderAfter = await funder.GetChannelAsync(channelId, ct);
        Assert.Equal(-18_000_000L,
                     (long)funderAfter.LocalBalance.MilliSatoshi - (long)funderBefore.LocalBalance.MilliSatoshi);
        Assert.True(funderAfter.IsUsable(), funderAfter.Describe());
        Assert.True((await fundee.GetChannelAsync(channelId, ct)).IsUsable());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes)
        {
            try
            {
                await node.DisposeAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not dispose {node.Name}: {e.Message}");
            }
        }
    }

    private async Task<NLightningTestNode> StartNodeAsync(string name, CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name,
                                                        configureNodeOptions: o => o.ToSelfDelay = ToSelfDelay);
        _nodes.Add(node);
        await node.StartAsync(ct);
        return node;
    }

    /// <summary>
    /// A scheduler over the node's own channel memory and channel operations, with a fixed estimate.
    /// </summary>
    private static FeeUpdateScheduler CreateScheduler(NLightningTestNode node, IFeeService feeService) =>
        ActivatorUtilities.CreateInstance<FeeUpdateScheduler>(node.Services, feeService);

    private async Task MineUntilUsableAsync(NLightningTestNode node, LNDNodeConnection lnd, ChannelId channelId,
                                            string channelPoint, CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var ours = (await node.ListChannelsAsync(ct)).Channels.FirstOrDefault(c => c.ChannelId == channelId);
            var theirs = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
            if (ours is not null && ours.IsUsable() && theirs is { Active: true })
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [node], ct);
            return false;
        }, s_timeout, $"channel {channelPoint} usable on both ends", ct, s_mineInterval);
        await ChainSync.WaitAllAtTipAsync(_fixture, [lnd], [node], ct);
    }

    private static async Task WaitForLndFeerateAsync(LNDNodeConnection lnd, string channelPoint, long feeratePerKw,
                                                     CancellationToken ct) =>
        await Poll.UntilAsync(async () =>
        {
            var channel = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
            return channel is { FeePerKw: var feerate, PendingHtlcs.Count: 0 } && feerate == feeratePerKw;
        }, s_timeout, $"LND's fee_per_kw at {feeratePerKw}", ct);

    private static async Task WaitForOurFeerateAsync(NLightningTestNode node, ChannelId channelId, uint feeratePerKw,
                                                     CancellationToken ct) =>
        await Poll.UntilAsync(() => node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel)
                                 && channel.Commitments is { } c
                                 && c.LocalCommit.Spec.FeeratePerKw == feeratePerKw
                                 && c.RemoteCommit.Spec.FeeratePerKw == feeratePerKw
                                 && c.RemoteNextCommit is null,
                              s_timeout, $"{node.Name}'s commitments at {feeratePerKw} sat/kw", ct);

    /// <summary>
    /// Once nothing is pending: the non-funder's balance is its whole to_local on LND's side; the funder's also pays
    /// the commitment fee.
    /// </summary>
    private static async Task AssertBalancesAgreeAsync(NLightningTestNode node, LNDNodeConnection lnd,
                                                       ChannelId channelId, string channelPoint, bool weAreFunder,
                                                       CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channelId, ct);
            var theirs = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
            return ours.OfferedHtlcCount + ours.ReceivedHtlcCount == 0 && theirs is { PendingHtlcs.Count: 0 };
        }, s_timeout, "no HTLC pending on either side", ct);

        var ours = await node.GetChannelAsync(channelId, ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
        Assert.NotNull(lndChannel);
        Console.WriteLine($"Ours: local {ours.LocalBalance.MilliSatoshi} remote {ours.RemoteBalance.MilliSatoshi} msat; "
                        + $"LND: local {lndChannel.LocalBalance} remote {lndChannel.RemoteBalance} commit fee "
                        + $"{lndChannel.CommitFee} fee_per_kw {lndChannel.FeePerKw}");
        if (weAreFunder)
        {
            Assert.Equal(ours.RemoteBalance.Satoshi, lndChannel.LocalBalance);
            Assert.Equal(ours.LocalBalance.Satoshi, lndChannel.RemoteBalance + lndChannel.CommitFee);
        }
        else
        {
            Assert.Equal(ours.LocalBalance.Satoshi, lndChannel.RemoteBalance);
            Assert.Equal(ours.RemoteBalance.Satoshi, lndChannel.LocalBalance + lndChannel.CommitFee);
        }

        Assert.True(lndChannel.Active, "LND no longer lists the channel as active");
        Assert.True(ours.IsUsable(), ours.Describe());
    }

    private static async Task AssertLndPaysUsAsync(NLightningTestNode node, LNDNodeConnection lnd,
                                                   ChannelId channelId, string channelPoint, LightningMoney amount,
                                                   CancellationToken ct)
    {
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
        Assert.NotNull(lndChannel);
        var before = await node.GetChannelAsync(channelId, ct);
        var invoice = await node.CreateInvoiceAsync(amount, $"fee flow lnd pays {amount.Satoshi} sat", ct);
        await LndTestHelpers.ResetMissionControlAsync(lnd, ct);

        var payment = await LndTestHelpers.SendPaymentV2Async(
                          lnd, LndTestHelpers.PinnedPayment(invoice.Bolt11, [lndChannel.ChanId]), ct);

        Console.WriteLine($"LND's payment: {payment.Status}, reason {payment.FailureReason}");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(Convert.FromHexString(payment.PaymentPreimage)));
        await Poll.UntilAsync(async () =>
        {
            var after = await node.GetChannelAsync(channelId, ct);
            return after.OfferedHtlcCount + after.ReceivedHtlcCount == 0
                && after.LocalBalance.MilliSatoshi == before.LocalBalance.MilliSatoshi + amount.MilliSatoshi;
        }, s_timeout, $"our balance up by {amount.MilliSatoshi} msat", ct);
    }

    private static async Task AssertWePayLndAsync(NLightningTestNode node, LNDNodeConnection lnd,
                                                  LightningMoney amount, CancellationToken ct)
    {
        var invoice = await LndTestHelpers.AddInvoiceAsync(lnd, (long)amount.MilliSatoshi, [], ct,
                                                           $"fee flow we pay {amount.Satoshi} sat");
        var payment = await node.PayInvoiceAsync(invoice.PaymentRequest, ct);
        Console.WriteLine($"Our payment: {payment.Status}, failure {payment.FailureCode}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        await LndTestHelpers.WaitForInvoiceStateAsync(lnd, invoice.RHash.ToByteArray(),
                                                      Invoice.Types.InvoiceState.Settled, s_timeout, ct);
    }

    /// <summary>LND's <c>txid:index</c> from an <c>OpenChannelSync</c> answer (the bytes are in internal order).</summary>
    private static string ToChannelPoint(ChannelPoint channelPoint)
    {
        var txid = channelPoint.FundingTxidBytes.ToByteArray().Reverse().ToArray();
        return $"{Convert.ToHexString(txid).ToLowerInvariant()}:{channelPoint.OutputIndex}";
    }

    /// <summary>A fee estimate the test sets.</summary>
    private sealed class FixedFeeService(uint feeratePerKw) : IFeeService
    {
        public uint FeeratePerKw { get; set; } = feeratePerKw;

        public Task<LightningMoney> GetFeeRatePerKwAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LightningMoney.Satoshis(FeeratePerKw));

        public LightningMoney GetCachedFeeRatePerKw() => LightningMoney.Satoshis(FeeratePerKw);

        public Task RefreshFeeRateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;
    }
}
using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Channels.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Payments.Enums;
using Fixtures;
using Mock;
using TestCollections;
using Utils;

/// <summary>
/// NL-270 proof against LND, through the daemon's <c>payinvoice</c> handler: (a) we pay an LND invoice larger than
/// any one of our channels by splitting it over two channels to the payee (BOLT 4 <c>basic_mpp</c>: one hash,
/// <c>payment_secret</c> and <c>total_msat</c>); (b) our first hop (alice) refuses our HTLC with
/// <c>incorrect_cltv_expiry</c> because bob's invoice hints her channel with too small a CLTV delta, and the retry with
/// alice's signed <c>channel_update</c> succeeds; (c) the per-call fee limit decides whether a hinted fee is paid.
/// The node routes without the gossip graph (<c>Node:Payments:UseGraph = false</c>).
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class PaymentRetryFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_activeTimeout = TimeSpan.FromSeconds(120);
    private const uint PayTimeoutSeconds = 90;

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly NLightningTestNode _node;
    private readonly Dictionary<string, string> _addresses = [];

    public PaymentRetryFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));

        var port = PortPoolUtil.GetAvailablePortAsync().GetAwaiter().GetResult();
        Assert.True(port > 0);
        _node = new NLightningTestNode(fixture, $"nlightning_payment_retry_{Guid.NewGuid()}.db",
                                       new FakeSecureKeyManager(), port);

        // These proofs are about direct channels and route hints (NL-270). With the graph, a hint that does not fit
        // (e.g. (c)'s fee above the call's limit) brings in the gossip graph (BOLT 7 plan D7), which knows alice's
        // public channel to bob at her real, lower fee; graph routing has its own proofs (GraphPaymentHarnessTests)
        _node.ExtraConfiguration["Node:Payments:UseGraph"] = "false";
    }

    public async ValueTask InitializeAsync()
    {
        await _node.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
            await DockerDiagnostics.DumpContainerLogsAsync(["alice", "bob", "david"]);

        await _node.DisposeAsync();
        _node.DeleteFiles();
        PortPoolUtil.ReleasePort(_node.Port);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// (a) Two 300,000 sat channels to david and a 450,000 sat invoice: no channel can carry it, so it goes as two
    /// parts, and david's invoice is settled with both HTLCs.
    /// </summary>
    [Fact]
    public async Task Given_AnInvoiceLargerThanEveryChannel_When_WePay_Then_ItIsSplitOverTwoChannelsAndSettled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var david = GetLnd("david");
        var capacity = LightningMoney.Satoshis(300_000);
        await _node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var first = await OpenUsableChannelAsync(david, capacity, ct);
        var second = await OpenUsableChannelAsync(david, capacity, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [david], [_node], ct);

        var amount = LightningMoney.Satoshis(450_000);
        Assert.True(amount > capacity);
        var firstBefore = await _node.GetChannelAsync(first.ChannelId, ct);
        var secondBefore = await _node.GetChannelAsync(second.ChannelId, ct);
        var invoice = await LndTestHelpers.AddInvoiceAsync(david, (long)amount.MilliSatoshi, [], ct, "mpp");

        // Act
        var response = await PayAsync(new PayInvoiceClientRequest(invoice.PaymentRequest)
        {
            TimeoutSeconds = PayTimeoutSeconds
        }, ct);

        // Assert
        var payment = response.Payment;
        Console.WriteLine($"MPP payment: {payment.Status} after {response.Attempts} HTLC(s), {response.Parts} at once; "
                        + $"{payment.FailureCode}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.True(response.Parts >= 2, $"{response.Parts} part(s) in flight at once");
        Assert.True(payment.Fee.IsZero);
        var lndInvoice = await LndTestHelpers.WaitForInvoiceStateAsync(david, invoice.RHash.ToByteArray(),
                                                                       Invoice.Types.InvoiceState.Settled,
                                                                       s_activeTimeout, ct);
        Assert.Equal(lndInvoice.RPreimage.ToByteArray(), (byte[])payment.Preimage!.Value);
        Assert.Equal((long)amount.MilliSatoshi, lndInvoice.AmtPaidMsat);
        var settledHtlcs = lndInvoice.Htlcs.Where(h => h.State == InvoiceHTLCState.Settled).ToList();
        Assert.True(settledHtlcs.Count >= 2, $"{settledHtlcs.Count} settled HTLC(s)");
        Assert.All(settledHtlcs, h => Assert.Equal((ulong)amount.MilliSatoshi, h.MppTotalAmtMsat));
        Assert.True(settledHtlcs.Select(h => h.ChanId).Distinct().Count() >= 2, "every part used the same channel");

        // Our balances move once both fulfills are irrevocably committed
        ulong spent = 0;
        await Poll.UntilAsync(async () =>
        {
            var firstAfter = await _node.GetChannelAsync(first.ChannelId, ct);
            var secondAfter = await _node.GetChannelAsync(second.ChannelId, ct);
            var firstSpent = firstBefore.LocalBalance.MilliSatoshi - firstAfter.LocalBalance.MilliSatoshi;
            var secondSpent = secondBefore.LocalBalance.MilliSatoshi - secondAfter.LocalBalance.MilliSatoshi;
            spent = firstSpent + secondSpent;
            return firstSpent > 0 && secondSpent > 0 && spent == amount.MilliSatoshi;
        }, s_activeTimeout, "both channels' balances moved by the amount", ct);
        Assert.Equal(amount.MilliSatoshi, spent);
    }

    /// <summary>
    /// (b) Bob's invoice hints alice's channel to bob with her fee but a CLTV delta 20 blocks below hers; alice (our
    /// peer, the first hop) fails our HTLC with <c>incorrect_cltv_expiry</c> and her signed <c>channel_update</c>, and
    /// the second HTLC, built with her real delta, is settled.
    /// </summary>
    [Fact]
    public async Task Given_OurFirstHopRefusesTheHintsCltvDelta_When_WePay_Then_TheRetryWithHerChannelUpdateSucceeds()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = GetLnd("alice");
        var bob = GetLnd("bob");
        var amount = LightningMoney.Satoshis(20_000);
        var (aliceIdHex, toBob, policy) = await AliceChannelToBobAsync(alice, bob, amount, ct);
        Assert.True(policy.TimeLockDelta > 20, $"alice's delta {policy.TimeLockDelta} leaves no room below it");
        var hint = LndTestHelpers.RouteHint(LndTestHelpers.HopHint(aliceIdHex, toBob, (uint)policy.FeeBaseMsat,
                                                                   (uint)policy.FeeRateMilliMsat,
                                                                   policy.TimeLockDelta - 20));
        var invoice = await LndTestHelpers.AddInvoiceAsync(bob, (long)amount.MilliSatoshi, [hint], ct, "retry");
        var expectedFee = (ulong)policy.FeeBaseMsat + amount.MilliSatoshi * (ulong)policy.FeeRateMilliMsat / 1_000_000;

        // Act
        var response = await PayAsync(new PayInvoiceClientRequest(invoice.PaymentRequest)
        {
            TimeoutSeconds = PayTimeoutSeconds
        }, ct);

        // Assert
        var payment = response.Payment;
        Console.WriteLine($"Retried payment: {payment.Status} after {response.Attempts} HTLC(s), fee "
                        + $"{payment.Fee.MilliSatoshi} msat; {payment.FailureCode}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.Equal(2, response.Attempts);
        Assert.Equal(expectedFee, payment.Fee.MilliSatoshi);
        var lndInvoice = await LndTestHelpers.WaitForInvoiceStateAsync(bob, invoice.RHash.ToByteArray(),
                                                                       Invoice.Types.InvoiceState.Settled,
                                                                       s_activeTimeout, ct);
        Assert.Equal(lndInvoice.RPreimage.ToByteArray(), (byte[])payment.Preimage!.Value);
    }

    /// <summary>
    /// (c) NL-270 against LND: bob's invoice hints alice's channel with a 5,000 msat fee. A call whose fee limit is
    /// 4,999 msat fails without offering anything; the same invoice paid with a limit of 5,000 msat succeeds and pays
    /// that fee.
    /// </summary>
    [Fact]
    public async Task Given_AHintFee_When_TheCallsFeeLimitIsBelowOrAtIt_Then_RefusedOrPaid()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = GetLnd("alice");
        var bob = GetLnd("bob");
        var amount = LightningMoney.Satoshis(20_000);
        var (aliceIdHex, toBob, policy) = await AliceChannelToBobAsync(alice, bob, amount, ct);
        var hintFee = Math.Max(5_000UL, (ulong)policy.FeeBaseMsat
                                      + amount.MilliSatoshi * (ulong)policy.FeeRateMilliMsat / 1_000_000);
        var hint = LndTestHelpers.RouteHint(LndTestHelpers.HopHint(aliceIdHex, toBob, (uint)hintFee, 0,
                                                                   policy.TimeLockDelta));
        var invoice = await LndTestHelpers.AddInvoiceAsync(bob, (long)amount.MilliSatoshi, [hint], ct, "fee limit");

        // Act
        var refused = await PayAsync(new PayInvoiceClientRequest(invoice.PaymentRequest)
        {
            TimeoutSeconds = PayTimeoutSeconds,
            MaxFee = LightningMoney.MilliSatoshis(hintFee - 1)
        }, ct);
        var paid = await PayAsync(new PayInvoiceClientRequest(invoice.PaymentRequest)
        {
            TimeoutSeconds = PayTimeoutSeconds,
            MaxFee = LightningMoney.MilliSatoshis(hintFee)
        }, ct);

        // Assert
        Console.WriteLine($"Fee-limited payment: {refused.Payment.Status}; {refused.Payment.FailureReason}");
        Assert.Equal(PaymentStatus.Failed, refused.Payment.Status);
        Assert.Contains($"exceeds the limit of {hintFee - 1} msat", refused.Payment.FailureReason);
        Assert.Equal(0, refused.Attempts);
        Assert.Equal(PaymentStatus.Succeeded, paid.Payment.Status);
        Assert.Equal(hintFee, paid.Payment.Fee.MilliSatoshi);
        Assert.Equal(1, paid.Attempts);
    }

    /// <summary>
    /// Opens a channel to alice and returns alice's channel to bob with room for the amount and alice's policy on it
    /// (read from LND, never changed: the fixture's alice is shared).
    /// </summary>
    private async Task<(string AliceIdHex, ulong ChanId, RoutingPolicy Policy)> AliceChannelToBobAsync(
        LNDNodeConnection alice, LNDNodeConnection bob, LightningMoney amount, CancellationToken ct)
    {
        await _node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await OpenUsableChannelAsync(alice, LightningMoney.Satoshis(1_000_000), ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [alice, bob], [_node], ct);

        var bobIdHex = Convert.ToHexString(bob.LocalNodePubKeyBytes).ToLowerInvariant();
        var aliceIdHex = Convert.ToHexString(alice.LocalNodePubKeyBytes).ToLowerInvariant();
        var channels = await alice.LightningClient.ListChannelsAsync(new ListChannelsRequest(), cancellationToken: ct);
        var toBob = channels.Channels
                            .Where(c => c.RemotePubkey.Equals(bobIdHex, StringComparison.OrdinalIgnoreCase)
                                     && c.Active && c.LocalBalance > amount.Satoshi * 2)
                            .OrderByDescending(c => c.LocalBalance)
                            .FirstOrDefault();
        Assert.NotNull(toBob);

        var info = await alice.LightningClient.GetChanInfoAsync(new ChanInfoRequest { ChanId = toBob.ChanId },
                                                                cancellationToken: ct);
        var policy = info.Node1Pub.Equals(aliceIdHex, StringComparison.OrdinalIgnoreCase)
                         ? info.Node1Policy
                         : info.Node2Policy;
        Assert.NotNull(policy);
        Console.WriteLine($"alice → bob channel {toBob.ChanId}: fee {policy.FeeBaseMsat} msat + "
                        + $"{policy.FeeRateMilliMsat} ppm, delta {policy.TimeLockDelta}");
        return (aliceIdHex, toBob.ChanId, policy);
    }

    private LNDNodeConnection GetLnd(string alias)
    {
        var node = _fixture.Builder?.LNDNodePool?.ReadyNodes.First(x => x.LocalAlias == alias);
        Assert.NotNull(node);
        return node;
    }

    /// <summary>
    /// <c>payinvoice</c> through the daemon's client handler (the path of <c>nltg payinvoice</c>).
    /// </summary>
    private async Task<PayInvoiceClientResponse> PayAsync(PayInvoiceClientRequest request, CancellationToken ct)
    {
        using var scope = _node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<PayInvoiceClientRequest, PayInvoiceClientResponse>>();
        return await handler.HandleAsync(request, ct);
    }

    /// <summary>
    /// Connects, opens a channel we fund and mines until both ends consider it usable.
    /// </summary>
    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(LNDNodeConnection peer,
        LightningMoney capacity, CancellationToken ct)
    {
        // Connect once per peer: a second connect to a connected peer is refused
        if (!_addresses.TryGetValue(peer.LocalAlias, out var address))
            _addresses[peer.LocalAlias] = address = await _node.ConnectToAsync(peer, ct);

        var channel = await _node.OpenChannelAsync(new OpenChannelClientRequest(address, capacity)
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {peer.LocalAlias}");

        var deadline = DateTime.UtcNow + s_activeTimeout;
        while (true)
        {
            var ours = await _node.GetChannelAsync(channel.ChannelId, ct);
            var lndChannel = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && lndChannel is { Active: true })
                return channel;

            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Channel not usable in time: LND active={lndChannel?.Active}, ours={ours.Describe()}");

            // LND may want more confirmations than we do
            if (ours.State != ChannelState.Open || lndChannel is null)
                await _node.MineBlocksAsync(1, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
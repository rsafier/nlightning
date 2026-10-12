using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// NL-1082 cluster proof of <c>payroute</c> against a real LND peer: we pay alice's invoice over a route we quoted
/// with <c>getroute</c> and hand back verbatim (single route), and over a hand-built two-shard MPP set spread across
/// two channels to her (each too small alone), through the daemon's own IPC handler.
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class PayRouteFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);

    /// <summary>Our spendable side per channel of the MPP pair (capacity 1M minus the 700k push): two needed.</summary>
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(700_000);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public PayRouteFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "payroute");
        await Node.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
        {
            await _node.DisposeAsync();
            _node = null;
        }
    }

    [Fact]
    public async Task Given_AliceInvoiceAndAQuotedRoute_When_WePayItVerbatim_Then_ItSettles()
    {
        // Arrange: one channel with alice, enough on our side
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var peerAddress = await Node.ConnectToAsync(alice, ct);
        var channel = await OpenUsableChannelAsync(alice, peerAddress, LightningMoney.Satoshis(300_000), ct);

        var amount = LightningMoney.Satoshis(100_000);
        var invoice = await LndTestHelpers.AddInvoiceAsync(alice, (long)amount.MilliSatoshi, [], ct, "payroute",
                                                           cltvExpiry: 40);

        // Act: quote the route (getroute, IPC 19) and pay exactly it (payroute, IPC 48) through the daemon handlers
        // The quote must price the invoice's min_final_cltv_expiry_delta (40, below); the daemon's payroute
        // validation enforces the same floor on what we hand it
        var quote = await Node.GetRouteAsync(new CompactPubKey(alice.LocalNodePubKeyBytes), amount, ct, 40);
        var payment = await Node.PayRouteAsync(invoice.PaymentRequest, [FromQuote(quote, channel.ChannelId)], ct);

        // Assert: succeeded with the preimage, alice's invoice settled, our channel drained of the HTLC
        Assert.Equal(PaymentStatus.Succeeded, payment.Payment.Status);
        Assert.Equal(invoice.RHash.ToByteArray(), (byte[])payment.Payment.PaymentHash);
        Assert.NotNull(payment.Payment.Preimage);
        Assert.Single(payment.RouteOutcomes);
        Assert.Equal(PaymentPartState.Succeeded, payment.RouteOutcomes[0].Status);
        Assert.NotNull(payment.RouteOutcomes[0].HtlcId);
        await AssertInvoiceSettledAndChannelsIdleAsync(alice, invoice.RHash.ToByteArray(), [channel.ChannelId], ct);
    }

    [Fact]
    public async Task Given_TwoChannelsToAlice_When_WePayAShardSetOverBoth_Then_TheWholeInvoiceSettles()
    {
        // Arrange: two channels with alice, each with 300k on our side — more than either can carry alone
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var peerAddress = await Node.ConnectToAsync(alice, ct);
        var first = await OpenUsableChannelAsync(alice, peerAddress, s_push, ct);
        var second = await OpenUsableChannelAsync(alice, peerAddress, s_push, ct);

        var amount = LightningMoney.Satoshis(450_000);
        var invoice = await LndTestHelpers.AddInvoiceAsync(alice, (long)amount.MilliSatoshi, [], ct, "payroute mpp",
                                                           cltvExpiry: 40);

        // Act: hand-build the two single-hop shards (250k + 200k = the 450k total). The final cltv of a quoted
        // route (height + the invoice's 40) does not depend on the amount, so a small quote fixes it for both
        var quote = await Node.GetRouteAsync(new CompactPubKey(alice.LocalNodePubKeyBytes),
                                              LightningMoney.Satoshis(50_000), ct, 40);
        var finalCltv = quote.Hops[^1].CltvExpiry;
        var aliceNodeId = quote.Hops[^1].NodeId;
        var shards = new List<PayRouteRouteClientInfo>
        {
            Shard(first.ChannelId, aliceNodeId, LightningMoney.Satoshis(250_000), finalCltv),
            Shard(second.ChannelId, aliceNodeId, LightningMoney.Satoshis(200_000), finalCltv)
        };
        var payment = await Node.PayRouteAsync(invoice.PaymentRequest, shards, ct);

        // Assert: the set settled together — both routes succeeded, alice holds the full amount, nothing pending
        Assert.Equal(PaymentStatus.Succeeded, payment.Payment.Status);
        Assert.Equal(PaymentPartState.Succeeded, payment.RouteOutcomes[0].Status);
        Assert.Equal(PaymentPartState.Succeeded, payment.RouteOutcomes[1].Status);
        // HTLC ids are per channel, so both shards may be id 0 on their own channel
        Assert.All(payment.RouteOutcomes, o => Assert.NotNull(o.HtlcId));
        await AssertInvoiceSettledAndChannelsIdleAsync(alice, invoice.RHash.ToByteArray(),
                                                          [first.ChannelId, second.ChannelId], ct);
    }

    /// <summary>A <c>payroute</c> route echoing a <c>getroute</c> quote for a single-hop (direct) channel.</summary>
    private static PayRouteRouteClientInfo FromQuote(GetRouteClientResponse quote, ChannelId firstHopChannel) =>
        new(Convert.ToHexStringLower((byte[])firstHopChannel), quote.Amount.MilliSatoshi, quote.CltvExpiry,
            quote.Hops.Select((h, i) => new PayRouteHopClientInfo(
                                  h.NodeId,
                                  i == quote.Hops.Count - 1 ? null : h.ShortChannelId.ToUInt64(),
                                  h.Amount.MilliSatoshi, h.CltvExpiry))
                      .ToList());

    private static PayRouteRouteClientInfo Shard(ChannelId firstHopChannel,
                                                 CompactPubKey payee,
                                                 LightningMoney amount, uint finalCltv) =>
        new(Convert.ToHexStringLower((byte[])firstHopChannel), amount.MilliSatoshi, finalCltv,
            [new PayRouteHopClientInfo(payee, null, amount.MilliSatoshi, finalCltv)]);

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(LndNodeConnection peer,
        string peerAddress, LightningMoney push, CancellationToken ct)
    {
        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var channel = await Node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress, s_capacity)
        {
            PushAmount = push,
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {peer.LocalAlias}");

        await Poll.UntilAsync(async () =>
        {
            var ours = await Node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
                return true;

            // LND may want more confirmations than we do
            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [Node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [peer], [Node], ct);
        return channel;
    }

    private async Task AssertInvoiceSettledAndChannelsIdleAsync(LndNodeConnection peer, byte[] rHash,
                                                                IReadOnlyList<ChannelId> channels, CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
                             (await LndTestHelpers.LookupInvoiceAsync(peer, rHash, ct)).State
                          == Invoice.Types.InvoiceState.Settled, s_timeout, "LND's invoice settled", ct);
        await Poll.UntilAsync(async () =>
        {
            foreach (var id in channels)
            {
                var channel = await Node.GetChannelAsync(id, ct);
                if (channel.OfferedHtlcCount + channel.ReceivedHtlcCount != 0)
                    return false;
            }

            return true;
        }, s_timeout, "no HTLC pending on our channels", ct);
    }
}
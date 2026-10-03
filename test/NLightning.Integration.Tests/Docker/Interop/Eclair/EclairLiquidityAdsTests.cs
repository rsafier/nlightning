using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Client.Printers;
using Daemon.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing.Enums;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Money;
using Domain.Protocol.Constants;
using Fixtures;
using Transport.Ipc.Responses;
using Utils;

/// <summary>
/// Liquidity ads against Eclair 0.14.3 as the seller (BOLT PR #1153 as Eclair speaks it, NL-771, plan LA6): the
/// fixture's second Eclair (<see cref="EclairFixture.GetSellerAsync"/>) sells at
/// <see cref="EclairFixture.SellerRates"/>, paid from the channel balance, and our node buys through the daemon's own
/// client handlers: (a) we see its rates in its <c>init</c> and through <c>liquidityads sellers</c>; (b)
/// <c>openchannel --request-inbound</c> (a dual-funded open): Eclair contributes at least what we asked, both ends'
/// balances carry the fee and <c>liquidityads purchases</c> lists it; (c) <c>splicein --request-inbound</c>; (d) a
/// <c>bumpopen</c> that buys again; (e) a payment each way on each channel. (f), <c>Explicit</c>, records Eclair's
/// <c>init</c> rates and <c>provide_funding</c> as <c>VECTOR</c> lines for <c>LiquidityAdsEclairVectors</c>.
/// </summary>
/// <remarks>
/// Eclair 0.14.3's API can only sell (<c>open</c>, <c>rbfopen</c> and <c>splicein</c> never request funding), so
/// every purchase here is ours. The seller Eclair is a separate container on the fixture's chain, so the shared Eclair
/// the other classes use keeps its default configuration. Run with <c>scripts/run-interop.sh eclair Release -class
/// NLightning.Integration.Tests.Docker.Interop.Eclair.EclairLiquidityAdsTests</c>.
/// </remarks>
[Collection(EclairInteropCollection.Name)]
[Trait("Category", EclairInteropCollection.Category)]
public sealed class EclairLiquidityAdsTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 10 * 60 * 1_000;

    /// <summary>Eclair's <c>remote-rbf-limits.attempt-delta-blocks</c>.</summary>
    private const int EclairRbfDeltaBlocks = 3;

    private const int MaxLockBlocks = 15;

    private static readonly TimeSpan s_stepTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The seller's rate in our wire form.</summary>
    private static readonly FundingRate s_sellerRate =
        new(EclairFixture.SellerRates.MinFundingSat, EclairFixture.SellerRates.MaxFundingSat,
            EclairFixture.SellerRates.FundingWeight, EclairFixture.SellerRates.FeeBasisPoints,
            EclairFixture.SellerRates.FeeBaseSat, EclairFixture.SellerRates.ChannelCreationFeeSat);

    private readonly EclairFixture _fixture;
    private readonly LiquidityAdsWireRecorder _wire = new();
    private EclairChannelSession? _session;
    private EclairEndpoint? _seller;

    public EclairLiquidityAdsTests(EclairFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_session is null)
            return;

        if (DockerDiagnostics.CurrentTestFailed)
        {
            Console.WriteLine($"[eclair-seller] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
            await DockerDiagnostics.DumpContainerLogsAsync([EclairFixture.SellerContainerName], 400);
        }

        await _session.DisposeAsync();
    }

    /// <summary>
    /// (a) The seller Eclair's <c>init</c> carries its rate (TLV 1339, <c>from_channel_balance</c>), which our node
    /// keeps as the peer's <c>LiquidityRates</c>, and <c>liquidityads sellers</c> lists it from that <c>init</c>.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ASellerEclair_When_Connected_Then_WeSeeItsRatesInItsInitAndInLiquidityAdsSellers()
    {
        // Arrange / Act
        var ct = TestContext.Current.CancellationToken;
        var session = await ConnectAsync("nltg-la-a", LightningMoney.Satoshis(200_000), ct);

        // Assert: the rates in the peer's init
        var rates = await Poll.ForAsync(() => Task.FromResult(PeerRates(session)), s_stepTimeout,
                                        "the seller's rates from its init", ct);
        Console.WriteLine($"[proof] Eclair's init rates: {string.Join("; ", rates.Rates)}, payment types "
                        + Convert.ToHexStringLower(rates.EncodedPaymentTypes));
        Assert.Contains(s_sellerRate, rates.Rates);
        Assert.True(rates.Supports(LiquidityPaymentType.FromChannelBalance));
        Assert.NotNull(_wire.Of(MessageTypes.Init, inbound: true)
                            .Select(m => LiquidityAdsWireRecorder.FindTlv(
                                        m.Wire, v => LiquidityAdsCodec.TryDecodeWillFundRates(v, out _)))
                            .FirstOrDefault(v => v is not null));

        // Assert: liquidityads sellers (ClientCommand 46) through the daemon's client handler
        var sellers = await HandleAsync<LiquidityAdsClientRequest, LiquidityAdsClientResponse>(
                          session, new LiquidityAdsClientRequest(LiquidityAdsAction.Sellers), ct);
        PrintThroughClient(sellers);
        var seller = Assert.Single(sellers.Sellers, s => s.NodeId == session.EclairPubKey);
        Assert.Equal(LiquiditySellerSource.Init, seller.Source);
        Assert.True(seller.IsConnected);
        Assert.Contains(s_sellerRate, seller.Rates.Rates);
    }

    /// <summary>
    /// (b) + (e) <c>openchannel --request-inbound 400000</c> of 500k sat (no <c>--dual-fund</c>: the purchase makes
    /// it v2): Eclair contributes at least 400k sat, our balance is our 500k less the fee, Eclair's is its contribution
    /// plus the fee (both from its <c>channel</c> JSON), <c>liquidityads purchases</c> lists the purchase, and payments
    /// go both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ASellerEclair_When_WeOpenRequestingInbound_Then_EclairContributesAndBothBalancesCarryTheFee()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await ConnectAsync("nltg-la-b", LightningMoney.Satoshis(2_000_000), ct);

        // Act
        var opened = await HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                         session, new OpenChannelClientRequest(session.EclairAddress, LightningMoney.Satoshis(500_000))
                         {
                             RequestInboundSat = 400_000
                         }, ct);
        session.ChannelId = opened.ChannelId;
        PrintThroughClient(opened);

        // Assert: the purchase and the funding
        var purchase = AssertBought(opened.Purchase, LiquidityPurchaseKind.ChannelOpen, 400_000);
        await session.MineUntilUsableAsync(ct);
        await AssertBalancesAsync(session, 500_000, purchase, ct);
        var listed = await HandleAsync<LiquidityAdsClientRequest, LiquidityAdsClientResponse>(
                         session, new LiquidityAdsClientRequest(LiquidityAdsAction.Purchases)
                         {
                             Role = LiquidityPurchaseRole.Buyer
                         }, ct);
        PrintThroughClient(listed);
        var row = Assert.Single(listed.Purchases, p => p.ChannelId == session.ChannelId);
        Assert.Equal(LiquidityPurchaseStatus.Active, row.Status);
        Assert.Equal(purchase.TotalFeeMsat, row.TotalFeeMsat);

        // (e) payments both ways
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(30_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(20_000), ct);
    }

    /// <summary>
    /// (c) + (e) On a dual-funded channel to the seller (no purchase), <c>splicein 100000 --request-inbound 300000</c>:
    /// Eclair adds at least 300k sat to the splice, the new capacity is the old one plus both contributions, the fee
    /// moves from our balance to Eclair's, and payments go both ways after the lock.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AChannelToTheSeller_When_WeSpliceInRequestingInbound_Then_EclairAddsTheLiquidity()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await ConnectAsync("nltg-la-c", LightningMoney.Satoshis(2_000_000), ct);
        var opened = await HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                         session, new OpenChannelClientRequest(session.EclairAddress, LightningMoney.Satoshis(500_000))
                         {
                             IsDualFunded = true
                         }, ct);
        session.ChannelId = opened.ChannelId;
        Assert.Null(opened.Purchase);
        await session.MineUntilUsableAsync(ct);
        var before = Channel(session);
        var capacityBefore = before.FundingOutput!.Amount.Satoshi;
        var localBefore = before.LocalBalance;

        // Act
        var spliced = await HandleAsync<SpliceInClientRequest, SpliceClientResponse>(
                          session, new SpliceInClientRequest(session.ChannelId, 100_000) { RequestInboundSat = 300_000 },
                          ct);
        PrintThroughClient(spliced);

        // Assert
        Assert.Equal(SpliceNegotiationState.Signed, spliced.State);
        var purchase = AssertBought(spliced.Purchase, LiquidityPurchaseKind.Splice, 300_000);
        Assert.NotNull(spliced.SpliceTxId);
        await MineUntilLockedAsync(session, spliced.SpliceTxId.Value, ct);
        var after = Channel(session);
        Assert.Equal(capacityBefore + 100_000 + (long)purchase.ContributedSat, after.FundingOutput!.Amount.Satoshi);
        Assert.Equal(localBefore.MilliSatoshi + 100_000_000UL - purchase.TotalFeeMsat, after.LocalBalance.MilliSatoshi);
        var eclair = await session.GetEclairChannelAsync(ct);
        Console.WriteLine($"[proof] after the splice: {EclairChannelSession.DescribeEclair(eclair)}");

        // (e)
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(30_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(20_000), ct);
    }

    /// <summary>
    /// (d) + (e) <c>openchannel --request-inbound 400000</c> at 1,000 sat/kw, then, after Eclair's three-block delta,
    /// <c>bumpopen</c> at 5,000 sat/kw with <c>--request-inbound 400000</c>: the replacement buys again (an
    /// <c>OpenRbf</c> purchase whose mining fee is at the new feerate), it confirms, both balances carry its fee, and
    /// payments go both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurUnconfirmedPurchase_When_WeBumpTheOpen_Then_TheReplacementBuysAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await ConnectAsync("nltg-la-d", LightningMoney.Satoshis(2_000_000), ct);
        var opened = await HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                         session, new OpenChannelClientRequest(session.EclairAddress, LightningMoney.Satoshis(500_000))
                         {
                             RequestInboundSat = 400_000,
                             FeeRatePerKw = LightningMoney.Satoshis(1_000)
                         }, ct);
        session.ChannelId = opened.ChannelId;
        var first = AssertBought(opened.Purchase, LiquidityPurchaseKind.ChannelOpen, 400_000);
        Assert.NotNull(opened.FundingTxId);
        await WaitInMempoolAsync(Display(opened.FundingTxId.Value), ct);
        await MineEmptyBlocksAsync(session, EclairRbfDeltaBlocks, ct);

        // Act
        var bumped = await HandleAsync<BumpOpenClientRequest, BumpOpenClientResponse>(
                         session, new BumpOpenClientRequest(session.ChannelId, 5_000) { RequestInboundSat = 400_000 },
                         ct);
        Console.WriteLine($"[proof] bumpopen replaced {Display(opened.FundingTxId.Value)} with "
                        + Display(bumped.FundingTxId));

        // Assert
        var second = AssertBought(bumped.Purchase, LiquidityPurchaseKind.OpenRbf, 400_000);
        Assert.True(second.MiningFeeSat > first.MiningFeeSat,
                    $"the bump's mining fee {second.MiningFeeSat} is at the higher feerate (first {first.MiningFeeSat})");
        await session.MineUntilUsableAsync(ct);
        Assert.Equal(Display(bumped.FundingTxId), Display(Channel(session).FundingOutput!.TransactionId!.Value));
        await AssertBalancesAsync(session, 500_000, second, ct);

        // (e)
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(30_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(20_000), ct);
    }

    /// <summary>
    /// (f) Records Eclair's <c>init</c> rates (TLV 1339 value) and its <c>provide_funding</c> answering our
    /// <c>request_funding</c> (in <c>accept_channel2</c>), with our <c>request_funding</c>, as <c>VECTOR</c> lines for
    /// <c>Tests.Utils/Vectors/LiquidityAdsEclairVectors</c>, and checks that <c>will_fund.funding_script</c> is the new
    /// funding output's script. Explicit: it only records (<c>-explicit only</c>, from the host).
    /// </summary>
    [Fact(Explicit = true, Timeout = TestTimeoutMs)]
    public async Task Given_ASellerEclair_When_WeBuy_Then_ItsInitRatesAndProvideFundingAreRecorded()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await ConnectAsync("nltg-la-capture", LightningMoney.Satoshis(2_000_000), ct);
        var tag = $"eclair-{EclairFixture.EclairTag}";
        Console.WriteLine($"VECTOR {tag} node_id {session.EclairPubKeyHex}");

        // Act: init rates
        await Poll.UntilAsync(() => Task.FromResult(PeerRates(session) is not null), s_stepTimeout,
                              "the seller's rates from its init", ct);
        foreach (var init in _wire.Of(MessageTypes.Init, inbound: true))
        {
            Console.WriteLine($"VECTOR {tag} init {Convert.ToHexStringLower(init.Wire.AsSpan(2))}");
            if (LiquidityAdsWireRecorder.FindTlv(init.Wire, v => LiquidityAdsCodec.TryDecodeWillFundRates(v, out _))
                is { } rates)
                Console.WriteLine($"VECTOR {tag} init_rates {Convert.ToHexStringLower(rates)}");
        }

        // Act: a purchase at the open
        var opened = await HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                         session, new OpenChannelClientRequest(session.EclairAddress, LightningMoney.Satoshis(500_000))
                         {
                             RequestInboundSat = 400_000
                         }, ct);
        session.ChannelId = opened.ChannelId;
        foreach (var open in _wire.Of(MessageTypes.OpenChannel2, inbound: false))
            if (LiquidityAdsWireRecorder.FindTlv(open.Wire, v => LiquidityAdsCodec.TryDecodeRequestFunding(v, out _))
                is { } request)
                Console.WriteLine($"VECTOR nltg open_request_funding {Convert.ToHexStringLower(request)}");

        WillFund? willFund = null;
        foreach (var accept in _wire.Of(MessageTypes.AcceptChannel2, inbound: true))
        {
            Console.WriteLine($"VECTOR {tag} accept_channel2 {Convert.ToHexStringLower(accept.Wire.AsSpan(2))}");
            if (LiquidityAdsWireRecorder.FindTlv(accept.Wire, v => LiquidityAdsCodec.TryDecodeWillFund(v, out _))
                is not { } provide)
                continue;

            Console.WriteLine($"VECTOR {tag} accept_provide_funding {Convert.ToHexStringLower(provide)}");
            LiquidityAdsCodec.TryDecodeWillFund(provide, out willFund);
        }

        // Assert: what funding_script is (expected: the new funding output's P2WSH script)
        Assert.NotNull(willFund);
        Assert.NotNull(opened.FundingTxId);
        var funding = await _fixture.Bitcoin.Rpc.GetRawTransactionAsync(new uint256((byte[])opened.FundingTxId.Value),
                                                                        true, ct);
        var output = funding.Outputs[(int)(opened.FundingOutputIndex ?? 0)];
        Console.WriteLine($"[capture] funding output script {output.ScriptPubKey.ToHex()}, will_fund.funding_script "
                        + Convert.ToHexStringLower(willFund.FundingScript));
        Assert.Equal(output.ScriptPubKey.ToBytes(), willFund.FundingScript);
    }

    private async Task<EclairChannelSession> ConnectAsync(string nodeName, LightningMoney walletSat,
                                                          CancellationToken ct)
    {
        _seller = await _fixture.GetSellerAsync(ct);
        var session = _session = await EclairChannelSession.CreateConnectedAsync(
                                     _fixture, nodeName, walletSat, ct,
                                     configureNode: node => node.ConfigureServices = _wire.Install, eclair: _seller);
        return session;
    }

    private static WillFundRates? PeerRates(EclairChannelSession session) =>
        session.Node.PeerManager.GetPeer(session.EclairPubKey) is { } peer && peer.TryGetPeerService(out var service)
            ? service.LiquidityRates
            : null;

    /// <summary>The purchase is ours, of the kind, for the amount, with Eclair contributing at least that much.</summary>
    private static LiquidityPurchaseModel AssertBought(LiquidityPurchaseModel? purchase, LiquidityPurchaseKind kind,
                                                       ulong requestedSat)
    {
        Assert.NotNull(purchase);
        Console.WriteLine($"[proof] {kind} purchase: requested {purchase.RequestedSat}, contributed "
                        + $"{purchase.ContributedSat}, mining fee {purchase.MiningFeeSat}, service fee "
                        + $"{purchase.ServiceFeeSat}, rate {purchase.Rate}");
        Assert.Equal(LiquidityPurchaseRole.Buyer, purchase.Role);
        Assert.Equal(kind, purchase.Kind);
        Assert.Equal(requestedSat, purchase.RequestedSat);
        Assert.True(purchase.ContributedSat >= requestedSat,
                    $"Eclair contributed {purchase.ContributedSat} sat, below the {requestedSat} requested");
        Assert.Equal(s_sellerRate, purchase.Rate);
        Assert.Equal(LiquidityPaymentType.FromChannelBalance, purchase.PaymentType);
        Assert.True(purchase.TotalFeeMsat > 0);
        return purchase;
    }

    /// <summary>
    /// Our balance is our contribution less the purchase's fee; Eclair's (its <c>channel</c> JSON, its local
    /// commitment's spec) is its contribution plus the fee, and ours there is the same as ours here.
    /// </summary>
    private static async Task AssertBalancesAsync(EclairChannelSession session, ulong ourContributionSat,
                                                  LiquidityPurchaseModel purchase, CancellationToken ct)
    {
        var ours = Channel(session);
        Assert.Equal((long)(ourContributionSat + purchase.ContributedSat), ours.FundingOutput!.Amount.Satoshi);
        Assert.Equal(ourContributionSat * 1_000 - purchase.TotalFeeMsat, ours.LocalBalance.MilliSatoshi);
        Assert.Equal(purchase.ContributedSat * 1_000 + purchase.TotalFeeMsat, ours.RemoteBalance.MilliSatoshi);

        var eclair = await session.GetEclairChannelAsync(ct);
        var spec = EclairJson.Active(eclair)?["localCommit"]?["spec"];
        Console.WriteLine($"[proof] Eclair's channel: {EclairChannelSession.DescribeEclair(eclair)}");
        Assert.NotNull(spec);
        Assert.Equal((long)(purchase.ContributedSat * 1_000 + purchase.TotalFeeMsat), ReadMsat(spec["toLocal"]));
        Assert.Equal((long)(ourContributionSat * 1_000 - purchase.TotalFeeMsat), ReadMsat(spec["toRemote"]));
    }

    private static long ReadMsat(JsonNode? node) =>
        node?.GetValue<long>() ?? throw new InvalidOperationException("Eclair's spec has no balance");

    /// <summary>Mines one block at a time until both ends moved to the splice's funding, then waits until usable.</summary>
    private async Task MineUntilLockedAsync(EclairChannelSession session, TxId spliceTxId, CancellationToken ct)
    {
        var expected = Display(spliceTxId);
        for (var block = 0; ; block++)
        {
            var eclair = await session.GetEclairChannelAsync(ct);
            if (Channel(session).FundingOutput?.TransactionId is { } ours && Display(ours) == expected
             && EclairJson.FundingTxId(eclair) == expected && EclairJson.ActiveCount(eclair) == 1)
                break;

            Assert.True(block < MaxLockBlocks, $"the splice was not locked by both ends in {MaxLockBlocks} blocks");
            await _fixture.MineAndWaitAsync(1, [session.Node], ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
    }

    private async Task MineEmptyBlocksAsync(EclairChannelSession session, int count, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            var address = await _fixture.Bitcoin.Rpc.GetNewAddressAsync(ct);
            await _fixture.Bitcoin.Rpc.SendCommandAsync("generateblock", ct, address.ToString(), Array.Empty<string>());
        }

        await _fixture.WaitAllAtTipAsync([session.Node], ct);
    }

    private async Task WaitInMempoolAsync(string txId, CancellationToken ct) =>
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct)).Contains(uint256.Parse(txId)),
                              s_stepTimeout, $"{txId} in bitcoind's mempool", ct);

    private static async Task<TResponse> HandleAsync<TRequest, TResponse>(EclairChannelSession session,
                                                                         TRequest request, CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }

    /// <summary>Prints a response as the CLI does (the IPC response through the client's printer).</summary>
    private static void PrintThroughClient(object response)
    {
        using var output = new StringWriter();
        switch (response)
        {
            case LiquidityAdsClientResponse liquidity:
                new LiquidityAdsPrinter(output).Print(LiquidityAdsIpcResponse.FromClientResponse(liquidity));
                break;
            case OpenChannelClientResponse open:
                new OpenChannelPrinter(output).Print(OpenChannelIpcResponse.FromClientResponse(open));
                break;
            case SpliceClientResponse splice:
                new SplicePrinter(output).Print(SpliceIpcResponse.FromClientResponse(splice));
                break;
        }

        Console.WriteLine(output.ToString());
    }

    /// <summary>The txid as bitcoind and Eclair print it.</summary>
    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();

    private static ChannelModel Channel(EclairChannelSession session) =>
        session.Node.Services.GetRequiredService<IChannelMemoryRepository>().TryGetChannel(session.ChannelId,
                                                                                          out var channel)
            ? channel
            : throw new InvalidOperationException($"{session.Node.Name} has no channel {session.ChannelId}");
}
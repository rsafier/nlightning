using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

using Application.Channels.DualFunding;
using Daemon.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.DualFunding;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.ValueObjects;
using Domain.Protocol.Messages;
using Fixtures;
using Onchain.Anchors;
using Utils;

/// <summary>
/// Basic interop with Eclair (<see cref="EclairFixture"/>, v0.14.3; NL-180): BOLT 8 + <c>init</c> both ways with the
/// negotiated features logged, anchors channels we fund (a plain <c>openchannel</c> that goes dual-funded, NL-551, and
/// v1 with a push from a node without <c>option_dual_fund</c>) and Eclair funds (<c>open_channel2</c> by default, we
/// contribute nothing; v1 to a node without <c>option_dual_fund</c>) reaching <c>NORMAL</c> with payments both ways,
/// cooperative closes started by either side (legacy <c>closing_signed</c>: our <c>option_simple_close</c> is off),
/// <c>channel_reestablish</c> after Eclair restarts, and Eclair's refusal of a v1 open once <c>option_dual_fund</c> is
/// negotiated (NL-557).
/// </summary>
/// <remarks>
/// Eclair runs with its default channel policy: it asks us for a <c>to_self_delay</c> of 720
/// (<see cref="EclairFixture.EclairDefaultToRemoteDelayBlocks"/>, accepted since NL-550) and offers a
/// <c>max_htlc_value_in_flight_msat</c> of 45 % of the channel (accepted since NL-552). Eclair's log is printed when a
/// test fails.
/// </remarks>
[Collection(EclairInteropCollection.Name)]
[Trait("Category", EclairInteropCollection.Category)]
public sealed class EclairInteropTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 8 * 60 * 1_000;

    /// <summary>Eclair's default <c>max-htlc-value-in-flight-percent</c>.</summary>
    private const int EclairDefaultInFlightPercent = 45;

    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(90);

    private readonly EclairFixture _fixture;
    private readonly List<EclairChannelSession> _ownSessions = [];
    private EclairChannelSession? _session;

    public EclairInteropTests(EclairFixture fixture, ITestOutputHelper output)
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (TestDiagnostics.CurrentTestFailed)
        {
            foreach (var session in _ownSessions.Append(_session).OfType<EclairChannelSession>())
                Console.WriteLine($"[eclair] channel at failure: {await session.DescribeAsync(CancellationToken.None)}");

            await _fixture.DumpEclairLogAsync(400);
        }

        foreach (var session in _ownSessions)
            await session.DisposeAsync();
    }

    /// <summary>
    /// 1a: BOLT 8 handshake and <c>init</c> with us as initiator; both ends list each other, the connection stays up,
    /// and the features both ends offer are negotiated (<see cref="AssertNegotiatedFeatures"/>).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_EclairNode_When_WeConnect_Then_InitExchangedAndAnchorsNegotiated()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, "nltg-connect");
        await node.StartAsync(ct);
        CompactPubKey eclairId = Convert.FromHexString(_fixture.EclairNodeId);

        // Act
        await node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.EclairAddress)).WaitAsync(ct);

        // Assert
        await Poll.UntilAsync(async () => node.IsConnectedTo(eclairId)
                                       && await _fixture.Eclair.IsConnectedAsync(node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(30), "both ends list each other", ct);
        LogFeatures(node, eclairId, await _fixture.Eclair.GetInfoAsync(ct));
        AssertNegotiatedFeatures(node, eclairId);
        Assert.True(await Poll.HoldsAsync(() => node.IsConnectedTo(eclairId), TimeSpan.FromSeconds(5), ct),
                    "the connection to Eclair dropped");
        Assert.True(await _fixture.Eclair.IsConnectedAsync(node.NodeIdHex, ct), "Eclair dropped the connection");
        Assert.Equal(0, node.CountLogLines(NLightningTestNode.InitLostLogFragment));
    }

    /// <summary>
    /// 1b: Eclair dials us at <see cref="EclairFixture.HostAddressForEclair"/> (<c>host.orb.internal</c> on
    /// OrbStack's cluster; our listener on every interface): we are the BOLT 8 responder, the
    /// connection stays up and the same features are negotiated.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurListeningNode_When_EclairConnectsToUs_Then_InitExchangedAndConnectionStable()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NLightningTestNode.CreateAsync(
                                   _fixture.Bitcoin, "nltg-inbound",
                                   configureNodeOptions: o => o.ListenAddresses =
                                                                  o.ListenAddresses
                                                                   .Select(a => a.Replace("127.0.0.1", "0.0.0.0"))
                                                                   .ToList());
        await node.StartAsync(ct);
        CompactPubKey eclairId = Convert.FromHexString(_fixture.EclairNodeId);

        // Act
        await _fixture.Eclair.ConnectAsync($"{node.NodeIdHex}@{_fixture.HostAddressForEclair}:{node.Port}", ct);

        // Assert
        await Poll.UntilAsync(async () => node.IsConnectedTo(eclairId)
                                       && await _fixture.Eclair.IsConnectedAsync(node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(30), "both ends list each other", ct);
        LogFeatures(node, eclairId, await _fixture.Eclair.GetInfoAsync(ct));
        AssertNegotiatedFeatures(node, eclairId);
        Assert.True(await Poll.HoldsAsync(() => node.IsConnectedTo(eclairId), TimeSpan.FromSeconds(5), ct),
                    "the connection from Eclair dropped");
        Assert.True(await _fixture.Eclair.IsConnectedAsync(node.NodeIdHex, ct), "Eclair dropped the connection");
    }

    /// <summary>
    /// 3 and NL-551: the channel we fund with a plain <c>openchannel</c> (1M sat, no <c>--dual-fund</c>, our default
    /// features) opens v2 because <c>option_dual_fund</c> is negotiated (Eclair refuses a v1 open then, NL-557; it
    /// contributes nothing and accepts with its default 720-block delay, NL-550). It is <c>NORMAL</c> at Eclair and
    /// usable at our end with the anchors channel type on both ends; we pay Eclair's invoice and Eclair pays ours.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_PlainOpenWithDefaultFeatures_When_Normal_Then_DualFundedAndPaymentsWorkBothWays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await GetSessionAsync(ct);

        // Act
        var ours = await session.GetOurChannelAsync(ct);
        var theirs = await session.GetEclairChannelAsync(ct);

        // Assert
        Console.WriteLine($"[eclair] channel: {EclairChannelSession.DescribeEclair(theirs)}");
        Assert.Equal("NORMAL", theirs["state"]!.GetValue<string>());
        Assert.True(ours.IsInitiator);
        Assert.Equal(EclairChannelSession.Capacity, ours.Capacity);
        var model = Channel(session);
        Assert.Equal(ChannelVersion.V2, model.Version);
        AssertV2ChannelId(model);
        Assert.True(session.Node.CountLogLines("option_dual_fund is negotiated") > 0,
                    "the plain openchannel did not take the NL-551 default");
        Assert.Equal(EclairFixture.EclairDefaultToRemoteDelayBlocks, model.ChannelParams.Remote.ToSelfDelay);
        Assert.True(model.ChannelParams.OptionAnchorOutputs, "not an anchors channel at our end");
        AssertEclairAnchors(theirs);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(40_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(25_000), ct);
    }

    /// <summary>
    /// 5: Eclair restarts (same data, same host port): we reconnect, both ends reestablish (we send
    /// <c>channel_reestablish</c>, Eclair is back to <c>NORMAL</c>) and payments work both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_NormalChannel_When_EclairRestarts_Then_ReestablishedAndPaymentsWork()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await GetSessionAsync(ct);
        var mark = session.Sent.Mark;
        var before = await session.GetOurChannelAsync(ct);

        // Act
        await _fixture.RestartEclairAsync(ct);

        // Assert
        await Poll.UntilAsync(() => session.Sent.CountSent<ChannelReestablishMessage>(session.ChannelId, mark) > 0,
                              EclairChannelSession.UsableTimeout, "we sent channel_reestablish on a new connection",
                              ct);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await session.GetOurChannelAsync(ct);
        Assert.Equal(before.LocalBalance, after.LocalBalance);
        Assert.False(after.DataLossDetected);
        // We pay first and more than Eclair pays back: the test may run before any other payment on the shared channel,
        // and Eclair keeps its channel reserve (1 % of the v2 channel)
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(30_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
    }

    /// <summary>
    /// 4 (and the v1 opener path): a node with <c>option_dual_fund</c> off opens v1 to Eclair (500k sat, 100k pushed;
    /// with <c>option_dual_fund</c> negotiated Eclair would refuse the v1 open, NL-557),
    /// then, after a payment each way, closes it: legacy <c>shutdown</c>/<c>closing_signed</c>, the
    /// closing transaction in the mempool, our channel Closed after 6 blocks, Eclair's <c>CLOSED</c>, and our wallet
    /// grown by our balance less the closing fee.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ChannelWeFunded_When_WeClose_Then_BothCloseAndOurFundsAreOnChain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await OwnAsync(EclairChannelSession.BuildOurFundedAsync(
                                         _fixture, "nltg-close-we", LightningMoney.Satoshis(500_000),
                                         LightningMoney.Satoshis(100_000), ct));
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(5_000), ct);
        Assert.Equal(ChannelVersion.V1, Channel(session).Version);
        var ours = await session.GetOurChannelAsync(ct);
        var walletBefore = AnchorsHarness.WalletBalance(session.Node);

        // Act
        CloseChannelClientResponse closed;
        using (var scope = session.Node.Services.CreateScope())
        {
            var handler = scope.ServiceProvider
                               .GetRequiredService<IClientCommandHandler<CloseChannelClientRequest,
                                    CloseChannelClientResponse>>();
            closed = await handler.HandleAsync(new CloseChannelClientRequest(session.ChannelId)
            {
                WaitSeconds = (uint)s_closeTimeout.TotalSeconds
            }, ct);
        }

        // Assert
        Assert.Equal(ChannelState.Closing, closed.State);
        Assert.NotNull(closed.ClosingTxId);
        var fee = await AssertClosedOnBothSidesAsync(session, closed.ClosingTxId.Value, ct);
        var expected = (long)ours.LocalBalance.Satoshi - (long)fee;
        await Poll.UntilAsync(() => (long)(AnchorsHarness.WalletBalance(session.Node) - walletBefore).Satoshi
                                 >= expected,
                              s_closeTimeout, "our wallet holds our channel balance less the closing fee", ct);
        Console.WriteLine($"[eclair] closed: our balance {ours.LocalBalance.Satoshi} sat, closing fee {fee} sat, "
                        + $"wallet +{(AnchorsHarness.WalletBalance(session.Node) - walletBefore).Satoshi} sat");
    }

    /// <summary>
    /// 2 (and 4 from Eclair's side): Eclair funds a 1M sat anchors channel to us. Both offer <c>option_dual_fund</c>,
    /// so it is <c>open_channel2</c> and we contribute nothing (<c>AcceptContributionSat</c> 0): a v2 channel id from
    /// both revocation basepoints, and Eclair's default 720-block <c>to_self_delay</c> accepted by the v2 accepter
    /// (NL-550). Payments both ways, then Eclair closes it (it is the funder: legacy <c>closing_signed</c>).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_EclairFundsDualFunded_When_Normal_Then_PaymentsWorkAndEclairCloses()
    {
        // Arrange + Act
        var ct = TestContext.Current.CancellationToken;
        var session = await OwnAsync(EclairChannelSession.BuildEclairFundedAsync(
                                         _fixture, "nltg-fundee-v2", EclairChannelSession.Capacity, ct));

        // Assert
        var model = Channel(session);
        Assert.Equal(ChannelVersion.V2, model.Version);
        Assert.False(model.IsInitiator);
        AssertV2ChannelId(model);
        Assert.True(model.ChannelParams.OptionAnchorOutputs, "not an anchors channel at our end");
        Assert.Equal(LightningMoney.Zero, model.LocalBalance);
        Assert.Equal(EclairChannelSession.Capacity, model.FundingOutput!.Amount);
        Assert.Equal(EclairFixture.EclairDefaultToRemoteDelayBlocks, model.ChannelParams.Remote.ToSelfDelay);
        AssertEclairAnchors(await session.GetEclairChannelAsync(ct));
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(30_000), ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(10_000), ct);

        // Act: Eclair closes
        var close = await _fixture.Eclair.CloseAsync(session.ChannelIdHex, ct);
        Console.WriteLine($"[eclair] close: {close?.ToJsonString()}");

        // Assert
        var closingTx = await Poll.ForAsync(() =>
        {
            var memory = session.Node.Services.GetRequiredService<IChannelMemoryRepository>();
            return Task.FromResult(memory.TryGetChannel(session.ChannelId, out var channel)
                                && channel.State == ChannelState.Closing
                                       ? channel.ClosingTransaction
                                       : null);
        }, s_closeTimeout, "our channel is Closing", ct);
        await AssertClosedOnBothSidesAsync(session, closingTx.TxId, ct);
    }

    /// <summary>
    /// The v1 accept path with our default open policy (NL-550, NL-552): our node without <c>option_dual_fund</c>, so
    /// Eclair opens with <c>open_channel</c> and its defaults, a <c>to_self_delay</c> of 720 and a
    /// <c>max_htlc_value_in_flight_msat</c> of 45 % of the channel, both accepted (they were refused before, by the
    /// 1.5 x 144 delay limit and the 64 % in-flight rule); the channel is usable and payments work both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurDefaultOpenPolicy_When_EclairOpensV1WithItsDefaults_Then_WeAcceptAndPaymentsWork()
    {
        // Arrange + Act
        var ct = TestContext.Current.CancellationToken;
        var capacity = LightningMoney.Satoshis(500_000);
        var session = await OwnAsync(EclairChannelSession.BuildEclairFundedAsync(
                                         _fixture, "nltg-fundee-v1", capacity, ct,
                                         o => o.Features.DualFund = FeatureSupport.No));

        // Assert
        var model = Channel(session);
        Assert.Equal(ChannelVersion.V1, model.Version);
        Assert.False(model.IsInitiator);
        Assert.True(model.ChannelParams.OptionAnchorOutputs, "not an anchors channel at our end");
        Console.WriteLine($"[eclair] Eclair's v1 open: to_self_delay {model.ChannelParams.Remote.ToSelfDelay}, "
                        + $"max_htlc_value_in_flight {model.ChannelParams.Remote.MaxHtlcValueInFlight.MilliSatoshi} msat");
        Assert.Equal(EclairFixture.EclairDefaultToRemoteDelayBlocks, model.ChannelParams.Remote.ToSelfDelay);
        Assert.Equal(capacity.MilliSatoshi * EclairDefaultInFlightPercent / 100,
                     model.ChannelParams.Remote.MaxHtlcValueInFlight.MilliSatoshi);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(5_000), ct);
    }

    /// <summary>
    /// NL-557 evidence: a v1 open to Eclair from a node with our default features (a push amount keeps
    /// <c>openchannel</c> on v1, NL-551) is refused by Eclair, which treats the channel as dual-funded once
    /// <c>option_dual_fund</c> is negotiated. This is why the push opens of this class come from a node with
    /// <c>DualFund = No</c>. Eclair 0.14.3 (and its master) takes DualFunding as a channel feature from both
    /// <c>init</c>s and then refuses any custom reserve; our client error names the dual-funded open as the way out.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_DefaultFeatures_When_WeOpenWithAPush_Then_EclairRefusesTheV1Open()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, "nltg-push-v1");
        await node.StartAsync(ct);
        await node.FundWalletAsync(LightningMoney.Satoshis(1_000_000), Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        await _fixture.WaitAllAtTipAsync([node], ct);
        await node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.EclairAddress)).WaitAsync(ct);
        await Poll.UntilAsync(async () => await _fixture.Eclair.IsConnectedAsync(node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(30), "Eclair lists us", ct);
        using var scope = node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<OpenChannelClientRequest,
                                OpenChannelClientResponse>>();

        // Act
        var refusal = await Record.ExceptionAsync(() => handler.HandleAsync(
                                                      new OpenChannelClientRequest(_fixture.EclairAddress,
                                                                                   LightningMoney.Satoshis(500_000))
                                                      {
                                                          PushAmount = LightningMoney.Satoshis(100_000)
                                                      }, ct));

        // Assert
        Console.WriteLine($"[eclair] v1 open with a push from a node with option_dual_fund: {refusal}");
        Assert.NotNull(refusal);
        Assert.Contains("dual-funded", refusal.Message);
        // NL-557: the client error says why and how to open instead
        Assert.Contains("negotiated option_dual_fund", refusal.Message);
        Assert.Contains("without a push amount, zero-conf or --v1", refusal.Message);
        Assert.Empty((await node.ListChannelsAsync(ct)).Channels);
    }

    /// <summary>
    /// E-X1: a real dual-funded open: Eclair opens 500k sat and we contribute 200k sat; both shares are in the funding
    /// output and payments work both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs, Explicit = true)]
    public async Task Given_EclairOpensDualFunded_When_WeContribute_Then_BothSharesAndPaymentsWork()
    {
        // Arrange + Act
        var ct = TestContext.Current.CancellationToken;
        var session = await OwnAsync(EclairChannelSession.BuildEclairFundedAsync(
                                         _fixture, "nltg-df-contrib", LightningMoney.Satoshis(500_000), ct,
                                         configureNode: n => n.ConfigureServices = services =>
                                             services.Configure<DualFundingOptions>(o =>
                                                 o.AcceptContributionSat = 200_000)));

        // Assert
        var model = Channel(session);
        Assert.Equal(ChannelVersion.V2, model.Version);
        AssertV2ChannelId(model);
        Assert.Equal(LightningMoney.Satoshis(200_000), model.LocalBalance);
        Assert.Equal(LightningMoney.Satoshis(700_000), model.FundingOutput!.Amount);
        await session.AssertWePayEclairAsync(LightningMoney.Satoshis(10_000), ct);
        await session.AssertEclairPaysUsAsync(LightningMoney.Satoshis(20_000), ct);
    }

    private async Task<EclairChannelSession> GetSessionAsync(CancellationToken ct)
    {
        _session = await EclairChannelSession.GetAsync(_fixture, ct);
        await _session.PrepareAsync(ct);
        return _session;
    }

    private async Task<EclairChannelSession> OwnAsync(Task<EclairChannelSession> build)
    {
        var session = await build;
        _ownSessions.Add(session);
        return session;
    }

    /// <summary>
    /// The closing transaction in the mempool, 6 blocks, our channel Closed and Eclair's <c>CLOSED</c> (or gone to
    /// <c>closedchannels</c>).
    /// </summary>
    /// <returns>The closing transaction's fee in satoshis.</returns>
    private async Task<ulong> AssertClosedOnBothSidesAsync(EclairChannelSession session, TxId closingTxId,
                                                           CancellationToken ct)
    {
        var closingHex = new NBitcoin.uint256((byte[])closingTxId).ToString();
        await Poll.UntilAsync(async () =>
        {
            var mempool = await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct);
            return mempool.Any(t => t.ToString() == closingHex);
        }, s_closeTimeout, "the closing transaction in bitcoind's mempool", ct);
        var entry = await _fixture.Bitcoin.Rpc.GetMempoolEntryAsync(NBitcoin.uint256.Parse(closingHex), true, ct);
        var fee = (ulong)entry.BaseFee.Satoshi;

        await _fixture.MineAndWaitAsync(6, [session.Node], ct);

        await Poll.UntilAsync(async () =>
        {
            var ours = (await session.Node.ListChannelsAsync(ct)).Channels
                                                                   .FirstOrDefault(c => c.ChannelId
                                                                                     == session.ChannelId);
            return ours is null || ours.State == ChannelState.Closed;
        }, s_closeTimeout, "our channel is Closed", ct);
        await Poll.UntilAsync(async () =>
        {
            var channel = await session.Eclair.ChannelAsync(session.ChannelIdHex, ct);
            if (channel?["state"]?.GetValue<string>() == "CLOSED")
                return true;

            var closed = await session.Eclair.ClosedChannelsAsync(session.Node.NodeIdHex, ct);
            return closed.Any(c => c?["channelId"]?.GetValue<string>() == session.ChannelIdHex);
        }, s_closeTimeout, "Eclair lists the channel closed", ct);
        Console.WriteLine($"[eclair] closed on both ends; closing tx {closingHex}, fee {fee} sat");
        return fee;
    }

    private static ChannelModel Channel(EclairChannelSession session) =>
        session.Node.ChannelMemoryRepository.TryGetChannel(session.ChannelId, out var channel)
            ? channel
            : throw new InvalidOperationException($"{session.Node.Name} has no channel {session.ChannelId}");

    private static void AssertV2ChannelId(ChannelModel channel)
    {
        using var sha256 = new Infrastructure.Crypto.Hashes.Sha256();
        Assert.Equal(ChannelIdV2.Derive(sha256, channel.LocalKeySet.RevocationCompactBasepoint,
                                        channel.RemoteKeySet!.RevocationCompactBasepoint), channel.ChannelId);
    }

    /// <summary>Eclair's channel says it is an anchors (zero-fee HTLC) channel.</summary>
    private static void AssertEclairAnchors(System.Text.Json.Nodes.JsonNode channel)
    {
        var json = channel.ToJsonString();
        Assert.True(json.Contains("anchor", StringComparison.OrdinalIgnoreCase),
                    "Eclair's channel does not mention anchors: " + EclairChannelSession.DescribeEclair(channel));
    }

    private static void LogFeatures(NLightningTestNode node, CompactPubKey eclairId,
                                    System.Text.Json.Nodes.JsonNode eclairInfo)
    {
        var peer = node.PeerManager.GetPeer(eclairId);
        Console.WriteLine($"[eclair] Eclair's features (getinfo): {eclairInfo["features"]?.ToJsonString()}");
        if (peer is not null)
        {
            var negotiated = peer.NegotiatedFeatures;
            Console.WriteLine($"[eclair] negotiated at our end: anchors={negotiated.OptionAnchors} "
                            + $"dual_fund={negotiated.DualFund} splice={negotiated.OptionSplice} "
                            + $"quiesce={negotiated.OptionQuiesce} route_blinding={negotiated.OptionRouteBlinding} "
                            + $"onion_messages={negotiated.OptionOnionMessages} "
                            + $"simple_close={negotiated.OptionSimpleClose} basic_mpp={negotiated.BasicMpp} "
                            + $"provide_storage={negotiated.OptionProvideStorage}; peer's init features "
                            + $"{Convert.ToHexString(peer.Features.GetWireBytes() ?? []).ToLowerInvariant()}");
        }
    }

    /// <summary>
    /// The features both ends offer are negotiated: anchors, dual funding, splice, quiesce, route blinding and onion
    /// messages.
    /// </summary>
    private static void AssertNegotiatedFeatures(NLightningTestNode node, CompactPubKey peerId)
    {
        var peer = node.PeerManager.GetPeer(peerId);
        Assert.NotNull(peer);
        var negotiated = peer.NegotiatedFeatures;
        Assert.NotEqual(FeatureSupport.No, negotiated.OptionAnchors);
        Assert.NotEqual(FeatureSupport.No, negotiated.DualFund);
        Assert.NotEqual(FeatureSupport.No, negotiated.OptionSplice);
        Assert.NotEqual(FeatureSupport.No, negotiated.OptionQuiesce);
        Assert.NotEqual(FeatureSupport.No, negotiated.OptionRouteBlinding);
        Assert.NotEqual(FeatureSupport.No, negotiated.OptionOnionMessages);
    }
}
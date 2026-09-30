using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Ldk;

using Daemon.Interfaces;
using Domain.Bitcoin.ValueObjects;
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
/// Basic interop with ldk-server (<see cref="LdkFixture"/>, LDK Node over rust-lightning 0.3; NL-180): BOLT 8 +
/// <c>init</c> both ways with the negotiated features logged, anchors channels we fund and LDK funds (v1 only: LDK has
/// no dual funding, NL-556) reaching usable on both ends with payments both ways, cooperative closes started by either
/// side (legacy <c>closing_signed</c>: our <c>option_simple_close</c> is off), and <c>channel_reestablish</c> after LDK
/// restarts. LDK's log is printed when a test fails.
/// </summary>
[Collection(LdkInteropCollection.Name)]
[Trait("Category", LdkInteropCollection.Category)]
public sealed class LdkInteropTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 10 * 60 * 1_000;

    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(120);

    private readonly LdkFixture _fixture;
    private readonly List<LdkChannelSession> _ownSessions = [];
    private LdkChannelSession? _session;

    public LdkInteropTests(LdkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var session in _ownSessions.Append(_session).OfType<LdkChannelSession>())
                Console.WriteLine($"[ldk] channel at failure: {await session.DescribeAsync(CancellationToken.None)}");

            await DockerDiagnostics.DumpContainerLogsAsync([LdkFixture.LdkContainerName], 400);
        }

        foreach (var session in _ownSessions)
            await session.DisposeAsync();
    }

    /// <summary>
    /// 1a: BOLT 8 handshake and <c>init</c> with us as initiator; both ends list each other, the connection stays up,
    /// and the features both ends offer are negotiated (<see cref="AssertNegotiatedFeatures"/>).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_LdkNode_When_WeConnect_Then_InitExchangedAndAnchorsNegotiated()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, "nltg-connect");
        await node.StartAsync(ct);
        CompactPubKey ldkId = Convert.FromHexString(_fixture.LdkNodeId);

        // Act
        await node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.LdkAddress)).WaitAsync(ct);

        // Assert
        await Poll.UntilAsync(async () => node.IsConnectedTo(ldkId)
                                       && await _fixture.Ldk.IsConnectedAsync(node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(30), "both ends list each other", ct);
        await LogFeaturesAsync(node, ldkId, ct);
        AssertNegotiatedFeatures(node, ldkId);
        Assert.True(await Poll.HoldsAsync(() => node.IsConnectedTo(ldkId), TimeSpan.FromSeconds(5), ct),
                    "the connection to LDK dropped");
        Assert.True(await _fixture.Ldk.IsConnectedAsync(node.NodeIdHex, ct), "LDK dropped the connection");
        Assert.Equal(0, node.CountLogLines(NLightningTestNode.InitLostLogFragment));
    }

    /// <summary>
    /// 1b: LDK dials us at <c>host.docker.internal</c> (our listener on every interface): we are the BOLT 8 responder,
    /// the connection stays up and the same features are negotiated.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurListeningNode_When_LdkConnectsToUs_Then_InitExchangedAndConnectionStable()
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
        CompactPubKey ldkId = Convert.FromHexString(_fixture.LdkNodeId);

        // Act
        await _fixture.Ldk.ConnectPeerAsync(node.NodeIdHex, $"{ClnFixture.HostAddressFromContainers}:{node.Port}", ct);

        // Assert
        await Poll.UntilAsync(async () => node.IsConnectedTo(ldkId)
                                       && await _fixture.Ldk.IsConnectedAsync(node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(30), "both ends list each other", ct);
        await LogFeaturesAsync(node, ldkId, ct);
        AssertNegotiatedFeatures(node, ldkId);
        Assert.True(await Poll.HoldsAsync(() => node.IsConnectedTo(ldkId), TimeSpan.FromSeconds(5), ct),
                    "the connection from LDK dropped");
        Assert.True(await _fixture.Ldk.IsConnectedAsync(node.NodeIdHex, ct), "LDK dropped the connection");
    }

    /// <summary>
    /// 3: the channel we fund (1M sat, 300k pushed, v1, private) is usable on both ends with the anchors channel type;
    /// we pay LDK's invoice and LDK pays ours.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ChannelWeFunded_When_Usable_Then_PaymentsWorkBothWays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await GetSessionAsync(ct);

        // Act
        var ours = await session.GetOurChannelAsync(ct);
        var theirs = await session.GetLdkChannelAsync(ct);

        // Assert
        Console.WriteLine($"[ldk] channel: {LdkChannelSession.DescribeLdk(theirs)}");
        Assert.True(ours.IsInitiator);
        Assert.False(theirs["is_outbound"]?.GetValue<bool>() ?? true);
        Assert.False(theirs["is_announced"]?.GetValue<bool>() ?? true);
        Assert.Equal(LdkChannelSession.Capacity, ours.Capacity);
        Assert.Equal((long)LdkChannelSession.Capacity.Satoshi, theirs["channel_value_sats"]!.GetValue<long>());
        var model = Channel(session);
        Assert.Equal(ChannelVersion.V1, model.Version);
        Assert.True(model.ChannelParams.OptionAnchorOutputs, "not an anchors channel at our end");
        Assert.True(LdkChannelSession.IsLdkAnchors(theirs), "not an anchors channel at LDK's end");
        LogChannelParams(model);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(40_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(25_000), ct);
    }

    /// <summary>
    /// 5: LDK restarts (same data, same host port): we reconnect, both ends reestablish (we send
    /// <c>channel_reestablish</c>, LDK's channel is usable again) and payments work both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_UsableChannel_When_LdkRestarts_Then_ReestablishedAndPaymentsWork()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await GetSessionAsync(ct);
        var mark = session.Sent.Mark;
        var before = await session.GetOurChannelAsync(ct);

        // Act
        await _fixture.RestartLdkAsync(ct);

        // Assert
        await Poll.UntilAsync(() => session.Sent.CountSent<ChannelReestablishMessage>(session.ChannelId, mark) > 0,
                              LdkChannelSession.UsableTimeout, "we sent channel_reestablish on a new connection", ct);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await session.GetOurChannelAsync(ct);
        Assert.Equal(before.LocalBalance, after.LocalBalance);
        Assert.False(after.DataLossDetected);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(12_000), ct);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(11_000), ct);
    }

    /// <summary>
    /// 4: a channel we fund (500k sat, 100k pushed), after a payment each way, closed by us: legacy
    /// <c>shutdown</c>/<c>closing_signed</c>, the closing transaction in the mempool, our channel Closed after 6
    /// blocks, LDK no longer lists it, and our wallet grown by our balance less the closing fee.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ChannelWeFunded_When_WeClose_Then_BothCloseAndOurFundsAreOnChain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await OwnAsync(LdkChannelSession.BuildOurFundedAsync(
                                         _fixture, "nltg-close-we", LightningMoney.Satoshis(500_000),
                                         LightningMoney.Satoshis(100_000), ct));
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(20_000), ct);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(5_000), ct);
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
        Console.WriteLine($"[ldk] closed: our balance {ours.LocalBalance.Satoshi} sat, closing fee {fee} sat, "
                        + $"wallet +{(AnchorsHarness.WalletBalance(session.Node) - walletBefore).Satoshi} sat");
    }

    /// <summary>
    /// NL-562: a small (50k sat) channel we fund. LDK asks for its minimum reserve of 1,000 sat (2 % of the channel),
    /// which our old rule (1.2 x our own 1 % reserve, 600 sat) refused with "Channel reserve amount is too large"; it
    /// is accepted now and the channel carries a payment.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ASmallChannelWeFund_When_LdkAsksForItsMinimumReserve_Then_WeAcceptAndPay()
    {
        // Arrange + Act
        var ct = TestContext.Current.CancellationToken;
        var session = await OwnAsync(LdkChannelSession.BuildOurFundedAsync(
                                         _fixture, "nltg-small", LightningMoney.Satoshis(50_000), null, ct));

        // Assert
        var model = Channel(session);
        LogChannelParams(model);
        Assert.True(model.IsInitiator);
        Assert.Equal(LightningMoney.Satoshis(50_000), model.FundingOutput!.Amount);
        Assert.Equal(LightningMoney.Satoshis(1_000), model.ChannelParams.Remote.ChannelReserveAmount);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(5_000), ct);
    }

    /// <summary>
    /// 2 (and 4 from LDK's side): LDK funds a 1M sat private anchors channel to us (v1 <c>open_channel</c>; we hold
    /// the anchors reserve first, NL-379). Payments both ways, then LDK closes it (<c>close-channel</c>; legacy
    /// <c>closing_signed</c>).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_LdkFunds_When_Usable_Then_PaymentsWorkAndLdkCloses()
    {
        // Arrange + Act
        var ct = TestContext.Current.CancellationToken;
        var session = await OwnAsync(LdkChannelSession.BuildLdkFundedAsync(_fixture, "nltg-fundee",
                                                                           LdkChannelSession.Capacity, ct));

        // Assert
        var model = Channel(session);
        Assert.Equal(ChannelVersion.V1, model.Version);
        Assert.False(model.IsInitiator);
        Assert.True(model.ChannelParams.OptionAnchorOutputs, "not an anchors channel at our end");
        Assert.Equal(LightningMoney.Zero, model.LocalBalance);
        Assert.Equal(LdkChannelSession.Capacity, model.FundingOutput!.Amount);
        Assert.True(LdkChannelSession.IsLdkAnchors(await session.GetLdkChannelAsync(ct)),
                    "not an anchors channel at LDK's end");
        LogChannelParams(model);
        await session.AssertLdkPaysUsAsync(LightningMoney.Satoshis(30_000), ct);
        await session.AssertWePayLdkAsync(LightningMoney.Satoshis(10_000), ct);

        // Act: LDK closes
        var close = await _fixture.Ldk.CloseChannelAsync(session.UserChannelId, session.Node.NodeIdHex, ct);
        Console.WriteLine($"[ldk] close: {close.ToJsonString()}");

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

    private async Task<LdkChannelSession> GetSessionAsync(CancellationToken ct)
    {
        _session = await LdkChannelSession.GetAsync(_fixture, ct);
        await _session.PrepareAsync(ct);
        return _session;
    }

    private async Task<LdkChannelSession> OwnAsync(Task<LdkChannelSession> build)
    {
        var session = await build;
        _ownSessions.Add(session);
        return session;
    }

    /// <summary>
    /// The closing transaction in the mempool, 6 blocks, our channel Closed and LDK no longer listing the channel.
    /// </summary>
    /// <returns>The closing transaction's fee in satoshis.</returns>
    private async Task<ulong> AssertClosedOnBothSidesAsync(LdkChannelSession session, TxId closingTxId,
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
        await Poll.UntilAsync(async () => await session.Ldk.GetChannelAsync(session.ChannelIdHex, ct) is null,
                              s_closeTimeout, "LDK no longer lists the channel", ct);
        Console.WriteLine($"[ldk] closed on both ends; closing tx {closingHex}, fee {fee} sat");
        return fee;
    }

    private static ChannelModel Channel(LdkChannelSession session) =>
        session.Node.ChannelMemoryRepository.TryGetChannel(session.ChannelId, out var channel)
            ? channel
            : throw new InvalidOperationException($"{session.Node.Name} has no channel {session.ChannelId}");

    /// <summary>Logs what LDK asked of us and what we asked of LDK (to_self_delay, in-flight, reserve, dust).</summary>
    private static void LogChannelParams(ChannelModel channel)
    {
        var local = channel.ChannelParams.Local;
        var remote = channel.ChannelParams.Remote;
        Console.WriteLine($"[ldk] channel params: ours to_self_delay={local.ToSelfDelay} "
                        + $"max_in_flight={local.MaxHtlcValueInFlight} dust={local.DustLimitAmount}; "
                        + $"LDK's to_self_delay={remote.ToSelfDelay} max_in_flight={remote.MaxHtlcValueInFlight} "
                        + $"dust={remote.DustLimitAmount} max_accepted={remote.MaxAcceptedHtlcs}");
    }

    private async Task LogFeaturesAsync(NLightningTestNode node, CompactPubKey ldkId, CancellationToken ct)
    {
        var info = await _fixture.Ldk.GetNodeInfoAsync(ct);
        Console.WriteLine($"[ldk] LDK's features (get-node-info): {info["features"]?.ToJsonString()}");
        var peer = node.PeerManager.GetPeer(ldkId);
        if (peer is not null)
        {
            var negotiated = peer.NegotiatedFeatures;
            Console.WriteLine($"[ldk] negotiated at our end: anchors={negotiated.OptionAnchors} "
                            + $"dual_fund={negotiated.DualFund} splice={negotiated.OptionSplice} "
                            + $"quiesce={negotiated.OptionQuiesce} route_blinding={negotiated.OptionRouteBlinding} "
                            + $"onion_messages={negotiated.OptionOnionMessages} "
                            + $"simple_close={negotiated.OptionSimpleClose} basic_mpp={negotiated.BasicMpp} "
                            + $"provide_storage={negotiated.OptionProvideStorage}; peer's init features "
                            + $"{Convert.ToHexString(peer.Features.GetWireBytes() ?? []).ToLowerInvariant()}");
        }
    }

    /// <summary>
    /// The features both ends offer are negotiated: anchors, splice, quiesce, route blinding and onion messages; dual
    /// funding is not (LDK does not offer it).
    /// </summary>
    private static void AssertNegotiatedFeatures(NLightningTestNode node, CompactPubKey peerId)
    {
        var peer = node.PeerManager.GetPeer(peerId);
        Assert.NotNull(peer);
        var negotiated = peer.NegotiatedFeatures;
        Assert.NotEqual(FeatureSupport.No, negotiated.OptionAnchors);
        // LDK has no dual funding (no bit 28/29 in its init, NL-556)
        Assert.Equal(FeatureSupport.No, negotiated.DualFund);
        Assert.NotEqual(FeatureSupport.No, negotiated.OptionSplice);
        Assert.NotEqual(FeatureSupport.No, negotiated.OptionQuiesce);
        Assert.NotEqual(FeatureSupport.No, negotiated.OptionRouteBlinding);
        Assert.NotEqual(FeatureSupport.No, negotiated.OptionOnionMessages);
    }
}
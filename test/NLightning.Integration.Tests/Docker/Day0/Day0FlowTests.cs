using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Day0;

using Abcd;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Protocol.Messages;
using Fixtures;
using Gossip;
using Infrastructure.Transport.Interfaces;
using Utils;

/// <summary>
/// The day-0 proof (wave sp2 lane SP2-F, <c>docs/agents/DAY0_RUNBOOK.md</c>): the script the owner and Nick run on
/// mainnet, between two NLightning nodes A and B on regtest, with LND alice as the network that watches. (1) A opens
/// a dual-funded public channel to B and both contribute; after 6 confirmations alice has its
/// <c>channel_announcement</c> and both policies. (2) Payments A to B, B to A, and alice to B through A. (3) A splices
/// in; the splice locks, the channel is announced again under its new short channel id and alice forgets the old
/// one. (4) B splices out to a bitcoind address; the same, and the address holds the amount in a confirmed
/// transaction. (5) B crashes while it sends its <c>tx_signatures</c> of a third splice and restarts: the splice
/// completes; then B is stopped while the splice confirms and restarted: the lock completes through
/// <c>channel_reestablish</c>. (6) <c>setchannelpolicy</c> on A lowers <c>htlc_maximum_msat</c> and alice sees it. (7)
/// <c>exportchanbackup</c> + <c>verifychanbackup</c> on both nodes after every step, always on the channel's current
/// funding. (8) A closes cooperatively and alice forgets the channel.
/// </summary>
/// <remarks>
/// <para>Written against the SP2 contracts (<c>3560f3a9</c>); the splice completion lands in lanes SP2-A (reestablish
/// across a splice, step 5), SP2-B (<c>splice_locked</c>, the new short channel id, re-announcement and the retired
/// SCID map, steps 3-5), SP2-C (the splice transaction is not a close) and SP2-E (backups on the current funding with
/// the rotated key index, step 7). The integrator runs it after the merge. Public channels change the LND nodes'
/// graph for good, so it runs in the gossip collection, in its own process:
/// <c>scripts/run-gossip.sh 1 Release -namespace NLightning.Integration.Tests.Docker.Day0</c>.</para>
/// <para>Both nodes run the runbook's feature set (<see cref="Day0Harness.EnableDay0Features"/>) and flush their own
/// gossip every 5 s instead of 60 s (<c>Gossip:OwnGossipFlushInterval</c>), so alice sees each announcement sooner;
/// A also has a public channel to alice (alice gets a push), the only way alice reaches B.</para>
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public sealed class Day0FlowTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 40 * 60 * 1_000;

    private const long OpenerContributionSat = 500_000;
    private const long AccepterContributionSat = 300_000;
    private const long AliceChannelPushSat = 400_000;
    private const ulong SpliceInSat = 200_000;
    private const ulong SpliceOutSat = 100_000;
    private const ulong RestartSpliceInSat = 50_000;
    private const ulong PolicyHtlcMaximumMsat = 150_000_000;

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    public Day0FlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var node in _nodes)
            {
                Console.WriteLine($"===== {node.Name}: last log lines =====");
                foreach (var line in node.NodeLog.TakeLast(300))
                    Console.WriteLine(line);
            }

            await DockerDiagnostics.DumpContainerLogsAsync(["alice"]);
        }

        foreach (var node in _nodes)
            await node.DisposeAsync();
    }

    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_TwoNLightningNodes_When_TheyRunTheDay0Script_Then_LndSeesEveryStepAndEveryBackupIsCurrent()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        IReadOnlyList<LNDNodeConnection> observers = [alice];

        // Arrange: A and B with the runbook's features; B contributes to a peer's dual-funded open. A has a public
        // channel to alice (alice gets a push, so she can pay B through A)
        var a = await StartDay0NodeAsync("day0-a", "nltg-day0-a", 0, ct);
        var b = await StartDay0NodeAsync("day0-b", "nltg-day0-b", AccepterContributionSat, ct);
        var aliceChannel = await PublicTopology.OpenPublicChannelToAliceAsync(
                               _fixture, a, LightningMoney.Satoshis(AliceChannelPushSat), observers, ct,
                               syncGraph: false);
        Console.WriteLine($"[day0] A's channel to alice: {new ShortChannelId(aliceChannel.ShortChannelId)}");
        await a.FundWalletAsync(LightningMoney.Satoshis(1_500_000), Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        await b.FundWalletAsync(LightningMoney.Satoshis(1_000_000), Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        await Day0Harness.ConnectBothWaysAsync(a, b, ct);

        // ---- Step 1: a dual-funded public channel, both contribute, announced at 6 confirmations ----
        var opened = await Day0Harness.HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                         a, new OpenChannelClientRequest(b.Address, LightningMoney.Satoshis(OpenerContributionSat))
                         {
                             IsDualFunded = true,
                             IsPublic = true
                         }, ct);
        var channelId = opened.ChannelId;
        Console.WriteLine($"[day0] step 1: dual-funded public channel {channelId}");
        var (openA, openB) = await Day0Harness.MineUntilUsableAsync(_fixture, observers, a, b, channelId, ct);

        Assert.True(openA.IsInitiator);
        Assert.False(openB.IsInitiator);
        Assert.Equal(OpenerContributionSat + AccepterContributionSat, openA.Capacity.Satoshi);
        Assert.Equal(OpenerContributionSat * 1_000, (long)openA.LocalBalance.MilliSatoshi);
        Assert.Equal(AccepterContributionSat * 1_000, (long)openA.RemoteBalance.MilliSatoshi);
        Assert.Equal(openA.RemoteBalance, openB.LocalBalance);
        Assert.Equal(ChannelVersion.V2, Channel(a, channelId).Version);
        Assert.Equal(ChannelVersion.V2, Channel(b, channelId).Version);
        Assert.True(Channel(a, channelId).ChannelParams.AnnounceChannel);
        var scidOpen = openA.ShortChannelId!.Value.ToUInt64();
        Assert.Equal(scidOpen, openB.ShortChannelId!.Value.ToUInt64());
        var edge = await Day0Harness.WaitLndHasChannelAsync(_fixture, alice, scidOpen, a, b, ct);
        Assert.Equal(OpenerContributionSat + AccepterContributionSat, edge.Capacity);
        await BackupBothAsync(a, b, channelId, "step 1 (open)", ct);

        // ---- Step 2: payments both ways, and alice -> A -> B ----
        await Day0Harness.PayAsync(a, b, 30_000, "day0 step 2 a->b", ct);
        await Day0Harness.PayAsync(b, a, 10_000, "day0 step 2 b->a", ct);
        var forwarded = await Day0Harness.LndPaysAsync(alice, b, 20_000, "day0 step 2 alice->a->b", ct);
        AssertRoutedThroughA(forwarded, a, b);
        await BackupBothAsync(a, b, channelId, "step 2 (payments)", ct);

        // ---- Step 3: A splices in; lock, new short channel id, re-announced, the old one retired ----
        var before3 = await Day0Harness.WaitSettledAsync(a, channelId, ct);
        var spliceIn = await Day0Harness.SpliceInAsync(a, channelId, SpliceInSat, ct);
        var spliceInTxId = Day0Harness.AssertSigned(spliceIn);
        Assert.Equal((ulong)before3.Capacity.Satoshi + SpliceInSat, spliceIn.NewCapacitySat);
        var (lockedA3, lockedB3) = await Day0Harness.MineUntilSpliceLockedAsync(_fixture, observers, a, b, channelId,
                                                                                spliceInTxId, ct);
        Assert.Equal(before3.Capacity.Satoshi + (long)SpliceInSat, lockedA3.Capacity.Satoshi);
        Assert.Equal(before3.LocalBalance.MilliSatoshi + SpliceInSat * 1_000, lockedA3.LocalBalance.MilliSatoshi);
        Assert.Equal(before3.RemoteBalance, lockedA3.RemoteBalance);
        var scidSplice3 = AssertNewShortChannelId(lockedA3, lockedB3, scidOpen);
        await Day0Harness.WaitLndHasChannelAsync(_fixture, alice, scidSplice3, a, b, ct);
        await Day0Harness.WaitLndForgotChannelAsync(alice, scidOpen, ct);
        await Day0Harness.PayAsync(a, b, 25_000, "day0 step 3 a->b", ct);
        await Day0Harness.PayAsync(b, a, 5_000, "day0 step 3 b->a", ct);
        AssertRoutedThroughA(await Day0Harness.LndPaysAsync(alice, b, 15_000, "day0 step 3 alice->a->b", ct), a, b);
        await BackupBothAsync(a, b, channelId, "step 3 (splice in)", ct);

        // ---- Step 4: B splices out to a bitcoind address ----
        var before4 = await Day0Harness.WaitSettledAsync(b, channelId, ct);
        var address = await _fixture.Bitcoin.GetNewAddressAsync(ct);
        var spliceOut = await Day0Harness.SpliceOutAsync(b, channelId, SpliceOutSat, address.ToString(), ct);
        var spliceOutTxId = Day0Harness.AssertSigned(spliceOut);
        var spliceOutTx = await _fixture.Bitcoin.GetRawTransactionAsync(spliceOutTxId, true, ct);
        Assert.Single(spliceOutTx.Outputs, o => o.ScriptPubKey == address.ScriptPubKey
                                             && o.Value == Money.Satoshis((long)SpliceOutSat));
        var spliceOutFee = await Day0Harness.GetFeeAsync(_fixture, spliceOutTxId, ct);
        var (lockedA4, lockedB4) = await Day0Harness.MineUntilSpliceLockedAsync(_fixture, observers, a, b, channelId,
                                                                                spliceOutTxId, ct);

        // ...B (the initiator, alone in it) paid the amount and the whole fee; A's balance did not move
        var capacityDrop = before4.Capacity.Satoshi - lockedB4.Capacity.Satoshi;
        Assert.Equal((long)SpliceOutSat + spliceOutFee, capacityDrop);
        Assert.Equal(spliceOut.NewCapacitySat, (ulong)lockedB4.Capacity.Satoshi);
        Assert.Equal(before4.LocalBalance.MilliSatoshi - (ulong)capacityDrop * 1_000, lockedB4.LocalBalance.MilliSatoshi);
        Assert.Equal(before4.RemoteBalance, lockedB4.RemoteBalance);
        var outInfo = await _fixture.Bitcoin.GetRawTransactionInfoAsync(spliceOutTxId, ct);
        Assert.True(outInfo.Confirmations >= 1, "the splice-out transaction is not confirmed");
        var scidSplice4 = AssertNewShortChannelId(lockedA4, lockedB4, scidSplice3);
        await Day0Harness.WaitLndHasChannelAsync(_fixture, alice, scidSplice4, a, b, ct);
        await Day0Harness.WaitLndForgotChannelAsync(alice, scidSplice3, ct);
        await Day0Harness.PayAsync(a, b, 12_000, "day0 step 4 a->b", ct);
        await Day0Harness.PayAsync(b, a, 6_000, "day0 step 4 b->a", ct);
        await BackupBothAsync(a, b, channelId, "step 4 (splice out)", ct);

        // ---- Step 5: B restarts mid-splice, twice ----
        // (a) B crashes on the wire as it raises its tx_signatures of A's splice-in and restarts: after
        // channel_reestablish (next_funding) the missing tx_signatures are retransmitted and the splice is signed
        var before5 = await Day0Harness.WaitSettledAsync(a, channelId, ct);
        var tcpB = Assert.IsType<CrashableTcpService>(b.Services.GetRequiredService<ITcpService>());
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.ChannelManager.OnResponseMessageReady += (_, args) =>
        {
            // Raised under the channel lock right after the save; cut every connection before anything else
            if (args.ResponseMessage is not TxSignaturesMessage || crashed.Task.IsCompleted)
                return;

            tcpB.CrashAsync().GetAwaiter().GetResult();
            crashed.TrySetResult();
        };
        var restartSplice = Day0Harness.SpliceInAsync(a, channelId, RestartSpliceInSat, ct);
        await crashed.Task.WaitAsync(Day0Harness.StepTimeout, ct);
        Console.WriteLine("[day0] step 5 (a): B crashed as it sent its tx_signatures");
        await b.StopAsync();
        await b.StartAsync(ct);
        await Day0Harness.EnsureConnectedAsync(a, b, ct);
        var restartTxId = Day0Harness.AssertSigned(await restartSplice.WaitAsync(Day0Harness.NetworkTimeout, ct));
        await Day0Harness.WaitInMempoolAsync(_fixture, restartTxId, ct);

        // (b) B is down while the splice confirms past its depth, then starts: its catch-up and channel_reestablish
        // (my_current_funding_locked) complete the lock on both ends
        await b.StopAsync();
        await ChainSync.MineAndWaitAsync(_fixture, 6, observers, [a], ct);
        var whileDown = await a.GetChannelAsync(channelId, ct);
        Assert.Equal(ChannelState.Open, whileDown.State);
        await b.StartAsync(ct);
        await Day0Harness.EnsureConnectedAsync(a, b, ct);
        var (lockedA5, lockedB5) = await Day0Harness.MineUntilSpliceLockedAsync(_fixture, observers, a, b, channelId,
                                                                                restartTxId, ct);
        Assert.Equal(before5.Capacity.Satoshi + (long)RestartSpliceInSat, lockedA5.Capacity.Satoshi);
        Assert.Equal(before5.LocalBalance.MilliSatoshi + RestartSpliceInSat * 1_000,
                     lockedA5.LocalBalance.MilliSatoshi);
        Assert.False(lockedA5.DataLossDetected);
        Assert.False(lockedB5.DataLossDetected);
        var scidSplice5 = AssertNewShortChannelId(lockedA5, lockedB5, scidSplice4);
        await Day0Harness.WaitLndHasChannelAsync(_fixture, alice, scidSplice5, a, b, ct);
        await Day0Harness.WaitLndForgotChannelAsync(alice, scidSplice4, ct);
        await Day0Harness.PayAsync(a, b, 11_000, "day0 step 5 a->b", ct);
        await Day0Harness.PayAsync(b, a, 4_000, "day0 step 5 b->a", ct);
        AssertRoutedThroughA(await Day0Harness.LndPaysAsync(alice, b, 9_000, "day0 step 5 alice->a->b", ct), a, b);
        await BackupBothAsync(a, b, channelId, "step 5 (restart mid-splice)", ct);

        // ---- Step 6: setchannelpolicy on A lowers htlc_maximum_msat; alice sees it ----
        var policy = await Day0Harness.HandleAsync<SetChannelPolicyClientRequest, ChannelPolicyClientResponse>(
                         a, new SetChannelPolicyClientRequest(new ChannelReference(channelId))
                         {
                             HtlcMaximumMsat = PolicyHtlcMaximumMsat
                         }, ct);
        Assert.Equal(PolicyHtlcMaximumMsat, policy.Policy.HtlcMaximumMsat);
        var seen = await Poll.ForAsync(async () =>
        {
            var current = await GossipGraphProbe.TryGetChanInfoAsync(alice, scidSplice5, ct);
            var ours = current is null
                           ? null
                           : current.Node1Pub.Equals(a.NodeIdHex, StringComparison.OrdinalIgnoreCase)
                               ? current.Node1Policy
                               : current.Node2Policy;
            return ours?.MaxHtlcMsat == PolicyHtlcMaximumMsat ? ours : null;
        }, Day0Harness.NetworkTimeout, "alice has A's new htlc_maximum_msat", ct, GossipGraphProbe.PollInterval);
        Console.WriteLine($"[day0] step 6: alice sees A's policy max {seen.MaxHtlcMsat} msat, updated "
                        + seen.LastUpdate);
        var listed = await a.GetChannelAsync(channelId, ct);
        Assert.Equal(PolicyHtlcMaximumMsat, listed.HtlcMaximumMsat);
        Assert.True(listed.HasPolicyOverride);
        await BackupBothAsync(a, b, channelId, "step 6 (setchannelpolicy)", ct);

        // ---- Step 8: cooperative close ----
        var close = await Day0Harness.HandleAsync<CloseChannelClientRequest, CloseChannelClientResponse>(
                        a, new CloseChannelClientRequest(channelId) { WaitSeconds = 60 }, ct);
        Assert.NotNull(close.ClosingTxId);
        var closingTxId = Day0Harness.ToUint256(close.ClosingTxId.Value);
        Console.WriteLine($"[day0] step 8: closing transaction {closingTxId} ({close.State})");
        await Day0Harness.WaitInMempoolAsync(_fixture, closingTxId, ct);
        var closingTx = await _fixture.Bitcoin.GetRawTransactionAsync(closingTxId, true, ct);
        Assert.Single(closingTx.Inputs);
        Assert.Equal(Day0Harness.ToUint256(lockedA5.FundingTxId!.Value), closingTx.Inputs[0].PrevOut.Hash);
        await ChainSync.MineAndWaitAsync(_fixture, 6, observers, [a, b], ct);
        foreach (var node in new[] { a, b })
            await Poll.UntilAsync(() => IsClosed(node, channelId), Day0Harness.StepTimeout,
                                  $"{node.Name}: channel {channelId} Closed", ct, TimeSpan.FromMilliseconds(500));
        await Day0Harness.WaitLndForgotChannelAsync(alice, scidSplice5, ct);
    }

    /// <summary>Closed in memory, or no longer loaded (a closed channel may be dropped from memory).</summary>
    private static bool IsClosed(NLightningTestNode node, ChannelId channelId) =>
        !node.Services.GetRequiredService<IChannelMemoryRepository>().TryGetChannel(channelId, out var channel)
     || channel.State == ChannelState.Closed;

    private async Task<NLightningTestNode> StartDay0NodeAsync(string name, string alias, long acceptContributionSat,
                                                              CancellationToken ct)
    {
        var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, name, alias, ct, n =>
        {
            Day0Harness.EnableDay0Features(n);
            n.ExtraConfiguration["Node:DualFund:AcceptContributionSat"] = acceptContributionSat.ToString();
            n.ExtraConfiguration["Gossip:OwnGossipFlushInterval"] = "00:00:05";
        });
        _nodes.Add(node);
        return node;
    }

    /// <summary><c>exportchanbackup</c> + <c>verifychanbackup</c> on both nodes (runbook: after every step).</summary>
    private static async Task BackupBothAsync(NLightningTestNode a, NLightningTestNode b, ChannelId channelId,
                                              string step, CancellationToken ct)
    {
        foreach (var node in new[] { a, b })
            await Day0Harness.BackupAsync(node, await Day0Harness.WaitSettledAsync(node, channelId, ct), step, ct);
    }

    /// <summary>
    /// Both ends list the same short channel id after the lock, a new one, and A lists the previous one as retired
    /// (lane SP2-B's map, shown by <c>listchannels</c> through SP2-D).
    /// </summary>
    private static ulong AssertNewShortChannelId(ChannelInfoClientResponse a, ChannelInfoClientResponse b,
                                                 ulong previous)
    {
        Assert.NotNull(a.ShortChannelId);
        Assert.NotNull(b.ShortChannelId);
        var scid = a.ShortChannelId.Value.ToUInt64();
        Assert.Equal(scid, b.ShortChannelId.Value.ToUInt64());
        Assert.NotEqual(previous, scid);
        Assert.Contains(a.RetiredShortChannelIds, r => r.ShortChannelId.ToUInt64() == previous);
        Console.WriteLine($"[day0] new short channel id {a.ShortChannelId} (was {new ShortChannelId(previous)})");
        return scid;
    }

    private static void AssertRoutedThroughA(Lnrpc.Payment payment, NLightningTestNode a, NLightningTestNode b)
    {
        var route = payment.Htlcs.Single(h => h.Status == Lnrpc.HTLCAttempt.Types.HTLCStatus.Succeeded).Route;
        Assert.True(route.Hops.Count >= 2, $"alice's route has {route.Hops.Count} hop(s)");
        Assert.Equal(b.NodeIdHex, route.Hops[^1].PubKey, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(a.NodeIdHex, route.Hops[^2].PubKey, StringComparer.OrdinalIgnoreCase);
    }

    private static Domain.Channels.Models.ChannelModel Channel(NLightningTestNode node, ChannelId channelId) =>
        node.Services.GetRequiredService<IChannelMemoryRepository>().TryGetChannel(channelId, out var channel)
            ? channel
            : throw new InvalidOperationException($"{node.Name} has no channel {channelId}");
}
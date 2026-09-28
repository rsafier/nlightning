using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBitcoin.RPC;

namespace NLightning.Integration.Tests.Docker.Day0;

using Abcd;
using Application.Channels.Splicing;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing.Enums;
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
/// a dual-funded public channel to B and both contribute; (1 b) A bumps the unconfirmed open with <c>bumpopen</c>,
/// (1 c) B, the accepter, bumps it again (NL-530); after 6 confirmations of B's attempt alice has its
/// <c>channel_announcement</c> and both policies. (2) Payments A to B, B to A, and alice to B through A. (3) A splices
/// in; the splice locks, the channel is announced again under its new short channel id and alice forgets the old
/// one. (4) B splices out to a bitcoind address; the same, and the address holds the amount in a confirmed
/// transaction. (5) B crashes before its <c>tx_signatures</c> of a third splice reach the wire and restarts: B
/// retransmits them on <c>channel_reestablish</c> and the splice completes; then B is stopped while the splice confirms and restarted: the lock completes through
/// <c>channel_reestablish</c>. (6) <c>setchannelpolicy</c> on A lowers <c>htlc_maximum_msat</c> and alice sees it. (7)
/// <c>exportchanbackup</c> + <c>verifychanbackup</c> on both nodes after every step, always on the channel's current
/// funding. (9) A splices in at BOLT 3's floor feerate and bumps the splice with <c>bumpsplice</c> (RBF, wave SPR):
/// the bump replaces the first attempt in the mempool, both nodes list both attempts pending, a payment goes through
/// while they are, the bumped attempt confirms and locks, the first never confirms, and alice has the new short channel
/// id and forgets the old one. (8) A closes cooperatively and alice forgets the channel (the close runs last, after
/// step 9).
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
/// A also has a public channel to alice (alice gets a push), the only way alice reaches B. B is connected to alice
/// without a channel, as each day-0 node has other peers: alice learns B's direction of the channel from B's own
/// gossip (through A alone she did not, see the connect in the test).</para>
/// <para>After each splice and the close the test waits until alice forgets the spent short channel id, mining one
/// block at a time up to BOLT 7's 72-block delay (<see cref="Day0Harness.WaitLndForgotChannelAsync"/>, which prints
/// the delay LND used); the proof itself only needs the new short channel id in alice's graph. B's
/// <c>tx_signatures</c> in step 5 (a) is kept off the wire by a hook that runs before B's outbox
/// (<see cref="Day0Harness.HookSentChannelMessages"/>), so the splice can only complete through B's retransmission,
/// which the test counts.</para>
/// <para>Step 9 was added in wave spr (lane SPR-C) against the SPR contracts (<c>b72a42ea</c>); the RBF protocol lands
/// in lane SPR-A and <c>bumpsplice</c> in lane SPR-B, and the integrator runs it after the merge. B refuses an RBF of
/// an attempt "created recently": since NL-520 until one new block (<c>Splice:MinRbfBlocks</c>, default 1), so A's
/// bump in the first attempt's block gets B's <c>tx_abort</c> and the same bump after one empty block is accepted;
/// nothing else is mined between the two attempts, so only the bump can confirm.</para>
/// </remarks>
[Collection(GossipRegtestCollection.Name)]
public sealed class Day0FlowTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 60 * 60 * 1_000;

    private const long OpenerContributionSat = 500_000;
    private const long AccepterContributionSat = 300_000;
    private const long AliceChannelPushSat = 400_000;
    private const ulong SpliceInSat = 200_000;
    private const ulong SpliceOutSat = 100_000;
    private const ulong RestartSpliceInSat = 50_000;
    private const ulong PolicyHtlcMaximumMsat = 150_000_000;
    private const ulong RbfSpliceInSat = 40_000;

    /// <summary>
    /// Step 1 (b): the feerate A bumps its unconfirmed open to (the open is at the test node's estimate, 2,500 sat/kw;
    /// IT-RBF-01 asks for at least 2,604).
    /// </summary>
    private const uint OpenBumpFeeRatePerKw = 5_000;

    /// <summary>
    /// Step 1 (c): the feerate B, the accepter, bumps the open to after A's bump (IT-RBF-01 asks for at least 5,208).
    /// </summary>
    private const uint AccepterBumpFeeRatePerKw = 7_000;

    /// <summary>Step 9's first attempt: BOLT 3's floor, the feerate the runbook warns about.</summary>
    private const uint RbfLowFeeRatePerKw = 253;

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly List<NLightningTestNode> _nodes = [];

    // Step 5 (a): B's hook crashes B on its first tx_signatures once armed, and counts the ones it sends afterwards
    private TaskCompletionSource? _crashOnTxSignatures;
    private CrashableTcpService? _tcpB;
    private ChannelId? _restartSpliceChannel;
    private int _txSignaturesSentAfterCrash;

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
        var b = await StartDay0NodeAsync("day0-b", "nltg-day0-b", AccepterContributionSat, ct,
                                         n => Day0Harness.HookSentChannelMessages(n, OnBSendsChannelMessage));
        var aliceChannel = await PublicTopology.OpenPublicChannelToAliceAsync(
                               _fixture, a, LightningMoney.Satoshis(AliceChannelPushSat), observers, ct,
                               syncGraph: false);
        Console.WriteLine($"[day0] A's channel to alice: {new ShortChannelId(aliceChannel.ShortChannelId)}");
        await a.FundWalletAsync(LightningMoney.Satoshis(1_500_000), Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        await b.FundWalletAsync(LightningMoney.Satoshis(1_000_000), Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        await Day0Harness.ConnectBothWaysAsync(a, b, ct);
        // B is alice's peer too (no channel): each node's own announcements and channel_update reach alice directly.
        // Through A alone alice never got B's direction: A relays others' gossip only to a peer that sent a
        // gossip_timestamp_filter, and LND sends one only to its active sync peers
        await b.ConnectToAsync(alice, ct);

        // ---- Step 1: a dual-funded public channel, both contribute, announced at 6 confirmations ----
        var opened = await Day0Harness.HandleAsync<OpenChannelClientRequest, OpenChannelClientResponse>(
                         a, new OpenChannelClientRequest(b.Address, LightningMoney.Satoshis(OpenerContributionSat))
                         {
                             IsDualFunded = true,
                             IsPublic = true
                         }, ct);
        var channelId = opened.ChannelId;
        Console.WriteLine($"[day0] step 1: dual-funded public channel {channelId}");

        // Step 1 (b), lane dfrbf: A bumps the unconfirmed public open (bumpopen, RBF on by default since NL-528); both
        // nodes move to the replacement, which is the funding that confirms and is announced
        var firstFunding = Channel(a, channelId).FundingOutput!.TransactionId!.Value;
        var bumped = await Day0Harness.HandleAsync<BumpOpenClientRequest, BumpOpenClientResponse>(
                         a, new BumpOpenClientRequest(channelId, OpenBumpFeeRatePerKw), ct);
        Assert.NotEqual(firstFunding, bumped.FundingTxId);
        await Poll.UntilAsync(() => Task.FromResult(Channel(b, channelId).FundingOutput!.TransactionId
                                                 == bumped.FundingTxId), Day0Harness.StepTimeout,
                              "B on the bumped funding", ct);
        Console.WriteLine($"[day0] step 1 (b): bumpopen replaced {firstFunding} with {bumped.FundingTxId}");

        // Step 1 (c), NL-530: B, the accepter, bumps it again (bumpopen works in either role; B is the interactive-tx
        // initiator of this attempt, adds the funding output and pays the shared fields); A follows, and this third
        // attempt is the funding that confirms and is announced
        var bumpedByB = await Day0Harness.HandleAsync<BumpOpenClientRequest, BumpOpenClientResponse>(
                            b, new BumpOpenClientRequest(channelId, AccepterBumpFeeRatePerKw), ct);
        Assert.NotEqual(bumped.FundingTxId, bumpedByB.FundingTxId);
        await Poll.UntilAsync(() => Task.FromResult(Channel(a, channelId).FundingOutput!.TransactionId
                                                 == bumpedByB.FundingTxId), Day0Harness.StepTimeout,
                              "A on B's bumped funding", ct);
        Console.WriteLine($"[day0] step 1 (c): B's bumpopen replaced {bumped.FundingTxId} with "
                        + $"{bumpedByB.FundingTxId}");
        var (openA, openB) = await Day0Harness.MineUntilUsableAsync(_fixture, observers, a, b, channelId, ct);
        Assert.Equal(bumpedByB.FundingTxId, Channel(a, channelId).FundingOutput!.TransactionId);
        Assert.Equal(bumpedByB.FundingTxId, Channel(b, channelId).FundingOutput!.TransactionId);

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
        await Day0Harness.WaitLndForgotChannelAsync(_fixture, alice, scidOpen, a, b, ct);
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
        await Day0Harness.WaitLndForgotChannelAsync(_fixture, alice, scidSplice3, a, b, ct);
        await Day0Harness.PayAsync(a, b, 12_000, "day0 step 4 a->b", ct);
        await Day0Harness.PayAsync(b, a, 6_000, "day0 step 4 b->a", ct);
        await BackupBothAsync(a, b, channelId, "step 4 (splice out)", ct);

        // ---- Step 5: B restarts mid-splice, twice ----
        // (a) B crashes on the wire as it raises its tx_signatures of A's splice-in (B contributes no input, so it
        // sends first and A cannot send its own before it has B's): B's hook runs before the peer manager's outbox
        // takes the message, so it never reaches A. After the restart B must retransmit it on channel_reestablish
        // (next_funding) and A, which gets it only that way, completes the splice and broadcasts it
        var before5 = await Day0Harness.WaitSettledAsync(a, channelId, ct);
        _tcpB = Assert.IsType<CrashableTcpService>(b.Services.GetRequiredService<ITcpService>());
        _restartSpliceChannel = channelId;
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _crashOnTxSignatures = crashed;
        var restartSplice = Day0Harness.SpliceInAsync(a, channelId, RestartSpliceInSat, ct);
        await crashed.Task.WaitAsync(Day0Harness.StepTimeout, ct);
        Console.WriteLine("[day0] step 5 (a): B crashed as it raised its tx_signatures, before they reached the wire");
        Assert.Equal(0, Volatile.Read(ref _txSignaturesSentAfterCrash));
        await b.StopAsync();
        await b.StartAsync(ct);
        await Day0Harness.EnsureConnectedAsync(a, b, ct);
        // A's splicein returns when its negotiation stops at the disconnection (CommitmentSigned, kept for the
        // reconnection) or, when B is back first, once it is signed; either way it names the splice, which A then
        // completes on B's retransmitted tx_signatures (checked by the mempool and the retransmission count below)
        var restartResponse = await restartSplice.WaitAsync(Day0Harness.NetworkTimeout, ct);
        Assert.True(restartResponse.State is SpliceNegotiationState.Signed or SpliceNegotiationState.CommitmentSigned,
                    $"the splice is {restartResponse.State}: {restartResponse.FailureReason}");
        Assert.NotNull(restartResponse.SpliceTxId);
        var restartTxId = Day0Harness.ToUint256(restartResponse.SpliceTxId.Value);
        await Day0Harness.WaitInMempoolAsync(_fixture, restartTxId, ct);
        Assert.True(Volatile.Read(ref _txSignaturesSentAfterCrash) >= 1,
                    "B never sent its tx_signatures again after the restart, yet the splice completed");
        Console.WriteLine($"[day0] step 5 (a): B retransmitted its tx_signatures {_txSignaturesSentAfterCrash} "
                        + $"time(s); the splice {restartTxId} is in the mempool");

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
        await Day0Harness.WaitLndForgotChannelAsync(_fixture, alice, scidSplice4, a, b, ct);
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

        // ---- Step 9: A splices in at the floor feerate and bumps it (bumpsplice, RBF); the bump locks ----
        var before9 = await Day0Harness.WaitSettledAsync(a, channelId, ct);
        Assert.NotNull(before9.FundingTxId);
        Assert.NotNull(before9.FundingOutputIndex);
        var funding9 = new OutPoint(Day0Harness.ToUint256(before9.FundingTxId.Value), before9.FundingOutputIndex.Value);
        var lowSplice = await Day0Harness.HandleAsync<SpliceInClientRequest, SpliceClientResponse>(
                            a, new SpliceInClientRequest(channelId, RbfSpliceInSat)
                            {
                                FeeRatePerKw = RbfLowFeeRatePerKw
                            }, ct);
        var lowTxId = Day0Harness.AssertSigned(lowSplice);
        Console.WriteLine($"[day0] step 9: first attempt {lowTxId} at {RbfLowFeeRatePerKw} sat/kw");
        await Day0Harness.WaitInMempoolAsync(_fixture, lowTxId, ct);
        var lowTx = await _fixture.Bitcoin.GetRawTransactionAsync(lowTxId, true, ct);
        var lowFee = await Day0Harness.GetFeeAsync(_fixture, lowTxId, ct);
        await WaitPendingAttemptsAsync(a, b, channelId, [lowTxId], ct);

        // ...B judges the RBF (NL-520): in the block the first attempt was created in, it is "created recently", so
        // B answers A's bump with tx_abort and the first attempt stays the only one...
        var spliceOptions = b.Services.GetRequiredService<IOptions<SpliceOptions>>().Value;
        Assert.Null(spliceOptions.MinRbfInterval);
        Assert.Equal(1u, spliceOptions.MinRbfBlocks);
        var early = await Day0Harness.HandleAsync<BumpSpliceClientRequest, SpliceClientResponse>(
                        a, new BumpSpliceClientRequest(channelId, Day0Harness.SpliceFeeRatePerKw), ct);
        Console.WriteLine($"[day0] step 9: bumpsplice in the same block: {early.State}, reason {early.FailureReason}");
        Assert.NotEqual(SpliceNegotiationState.Signed, early.State);
        await WaitPendingAttemptsAsync(a, b, channelId, [lowTxId], ct);
        Assert.Contains(lowTxId, await _fixture.Bitcoin.GetRawMempoolAsync(ct));

        // ...one empty block later (nothing confirms the first attempt) the same bump is accepted
        var emptyBlockAddress = await _fixture.Bitcoin.GetNewAddressAsync(ct);
        await _fixture.Bitcoin.SendCommandAsync("generateblock", ct, emptyBlockAddress.ToString(),
                                                Array.Empty<string>());
        await ChainSync.WaitAllAtTipAsync(_fixture, [a, b], ct);
        var bump = await Day0Harness.HandleAsync<BumpSpliceClientRequest, SpliceClientResponse>(
                       a, new BumpSpliceClientRequest(channelId, Day0Harness.SpliceFeeRatePerKw), ct);
        Console.WriteLine($"[day0] step 9: bumpsplice at {Day0Harness.SpliceFeeRatePerKw} sat/kw: {bump.State}, txid "
                        + $"{Day0Harness.Display(bump.SpliceTxId)}, capacity {bump.NewCapacitySat}, reason "
                        + bump.FailureReason);
        var bumpTxId = Day0Harness.AssertSigned(bump);
        Assert.NotEqual(lowTxId, bumpTxId);
        Assert.Equal((ulong)before9.Capacity.Satoshi + RbfSpliceInSat, bump.NewCapacitySat);

        // ...the bump replaced the first attempt: both spend the current funding output, the bump pays more
        await Poll.UntilAsync(async () =>
        {
            var mempool = await _fixture.Bitcoin.GetRawMempoolAsync(ct);
            return mempool.Contains(bumpTxId) && !mempool.Contains(lowTxId);
        }, Day0Harness.StepTimeout, $"{bumpTxId} replaced {lowTxId} in the mempool", ct);
        var bumpTx = await _fixture.Bitcoin.GetRawTransactionAsync(bumpTxId, true, ct);
        Assert.Contains(lowTx.Inputs, i => i.PrevOut == funding9);
        Assert.Contains(bumpTx.Inputs, i => i.PrevOut == funding9);
        var bumpFee = await Day0Harness.GetFeeAsync(_fixture, bumpTxId, ct);
        Assert.True(bumpFee > lowFee, $"the bump pays {bumpFee} sat, not more than the first attempt's {lowFee}");
        await WaitPendingAttemptsAsync(a, b, channelId, [lowTxId, bumpTxId], ct);

        // ...a payment while both attempts are pending (every commitment update signs for three fundings)
        await Day0Harness.PayAsync(a, b, 8_000, "day0 step 9 a->b (rbf pending)", ct);

        // ...the bumped attempt confirms and locks; the first never confirms
        var (lockedA9, lockedB9) = await Day0Harness.MineUntilSpliceLockedAsync(_fixture, observers, a, b, channelId,
                                                                                bumpTxId, ct);
        Assert.Equal(before9.Capacity.Satoshi + (long)RbfSpliceInSat, lockedA9.Capacity.Satoshi);
        Assert.Equal(before9.LocalBalance.MilliSatoshi + RbfSpliceInSat * 1_000 - 8_000_000,
                     lockedA9.LocalBalance.MilliSatoshi);
        foreach (var locked in new[] { lockedA9, lockedB9 })
            Assert.DoesNotContain(locked.Fundings, f => f.Status == ChannelFundingStatus.Pending);
        await AssertNeverConfirmedAsync(lowTxId, ct);
        var scidSplice9 = AssertNewShortChannelId(lockedA9, lockedB9, scidSplice5);
        await Day0Harness.WaitLndHasChannelAsync(_fixture, alice, scidSplice9, a, b, ct);
        await Day0Harness.WaitLndForgotChannelAsync(_fixture, alice, scidSplice5, a, b, ct);
        await Day0Harness.PayAsync(a, b, 7_000, "day0 step 9 a->b", ct);
        await Day0Harness.PayAsync(b, a, 3_000, "day0 step 9 b->a", ct);
        AssertRoutedThroughA(await Day0Harness.LndPaysAsync(alice, b, 6_000, "day0 step 9 alice->a->b", ct), a, b);
        await BackupBothAsync(a, b, channelId, "step 9 (splice rbf)", ct);

        // ---- Step 8: cooperative close ----
        var close = await Day0Harness.HandleAsync<CloseChannelClientRequest, CloseChannelClientResponse>(
                        a, new CloseChannelClientRequest(channelId) { WaitSeconds = 60 }, ct);
        Assert.NotNull(close.ClosingTxId);
        var closingTxId = Day0Harness.ToUint256(close.ClosingTxId.Value);
        Console.WriteLine($"[day0] step 8: closing transaction {closingTxId} ({close.State})");
        await Day0Harness.WaitInMempoolAsync(_fixture, closingTxId, ct);
        var closingTx = await _fixture.Bitcoin.GetRawTransactionAsync(closingTxId, true, ct);
        Assert.Single(closingTx.Inputs);
        Assert.Equal(Day0Harness.ToUint256(lockedA9.FundingTxId!.Value), closingTx.Inputs[0].PrevOut.Hash);
        await ChainSync.MineAndWaitAsync(_fixture, 6, observers, [a, b], ct);
        foreach (var node in new[] { a, b })
            await Poll.UntilAsync(() => IsClosed(node, channelId), Day0Harness.StepTimeout,
                                  $"{node.Name}: channel {channelId} Closed", ct, TimeSpan.FromMilliseconds(500));
        await Day0Harness.WaitLndForgotChannelAsync(_fixture, alice, scidSplice9, a, b, ct);
    }

    /// <summary>
    /// Both nodes list exactly <paramref name="attempts"/> as the channel's pending fundings (<c>listchannels</c>
    /// fundings): the first a <c>Splice</c>, the others its <c>SpliceRbf</c> attempts.
    /// </summary>
    private static async Task WaitPendingAttemptsAsync(NLightningTestNode a, NLightningTestNode b, ChannelId channelId,
                                                       IReadOnlyList<uint256> attempts, CancellationToken ct)
    {
        var expected = attempts.ToHashSet();
        foreach (var node in new[] { a, b })
        {
            var channel = await Poll.ForAsync(async () =>
            {
                var c = await node.GetChannelAsync(channelId, ct);
                var pending = c.Fundings.Where(f => f.Status == ChannelFundingStatus.Pending)
                               .Select(f => Day0Harness.ToUint256(f.FundingTxId)).ToHashSet();
                return pending.SetEquals(expected) ? c : null;
            }, Day0Harness.StepTimeout, $"{node.Name}: pending fundings {string.Join(", ", expected)}", ct,
                                              TimeSpan.FromMilliseconds(500));
            foreach (var funding in channel.Fundings.Where(f => f.Status == ChannelFundingStatus.Pending))
                Assert.Equal(Day0Harness.ToUint256(funding.FundingTxId) == attempts[0]
                                 ? ChannelFundingKind.Splice
                                 : ChannelFundingKind.SpliceRbf, funding.Kind);
        }
    }

    /// <summary>bitcoind knows <paramref name="txId"/> neither in a block nor in its mempool.</summary>
    private async Task AssertNeverConfirmedAsync(uint256 txId, CancellationToken ct)
    {
        Assert.DoesNotContain(txId, await _fixture.Bitcoin.GetRawMempoolAsync(ct));
        try
        {
            var info = await _fixture.Bitcoin.GetRawTransactionInfoAsync(txId, ct);
            Assert.Fail($"bitcoind knows the replaced attempt {txId} ({info.Confirmations} confirmations)");
        }
        catch (RPCException)
        {
            Console.WriteLine($"[day0] step 9: the first attempt {txId} never confirmed");
        }
    }

    /// <summary>Closed in memory, or no longer loaded (a closed channel may be dropped from memory).</summary>
    private static bool IsClosed(NLightningTestNode node, ChannelId channelId) =>
        !node.Services.GetRequiredService<IChannelMemoryRepository>().TryGetChannel(channelId, out var channel)
     || channel.State == ChannelState.Closed;

    private async Task<NLightningTestNode> StartDay0NodeAsync(string name, string alias, long acceptContributionSat,
                                                              CancellationToken ct,
                                                              Action<NLightningTestNode>? configure = null)
    {
        var node = await GossipTestNodes.StartGossipNodeAsync(_fixture, name, alias, ct, n =>
        {
            Day0Harness.EnableDay0Features(n);
            n.ExtraConfiguration["Node:DualFund:AcceptContributionSat"] = acceptContributionSat.ToString();
            n.ExtraConfiguration["Gossip:OwnGossipFlushInterval"] = "00:00:05";
            configure?.Invoke(n);
        });
        _nodes.Add(node);
        return node;
    }

    /// <summary>
    /// B's hook (<see cref="Day0Harness.HookSentChannelMessages"/>), run under the channel's lock before B's outbox
    /// takes the message: once step 5 (a) armed it, the first <c>tx_signatures</c> of the channel crashes B on the
    /// wire (so it is never sent) and every later one is counted as a retransmission.
    /// </summary>
    private void OnBSendsChannelMessage(ChannelResponseMessageEventArgs args)
    {
        var crash = _crashOnTxSignatures;
        if (crash is null || args.ResponseMessage is not TxSignaturesMessage
         || args.ResponseMessage.Payload.ChannelId != _restartSpliceChannel)
            return;

        if (crash.Task.IsCompleted)
        {
            Interlocked.Increment(ref _txSignaturesSentAfterCrash);
            return;
        }

        _tcpB!.CrashAsync().GetAwaiter().GetResult();
        crash.TrySetResult();
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
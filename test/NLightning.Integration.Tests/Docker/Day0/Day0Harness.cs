using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Day0;

using Abcd;
using Application.Channels.Managers;
using Daemon.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Fixtures;
using Gossip;
using Utils;

/// <summary>
/// The steps the day-0 proofs share (<c>docs/agents/DAY0_RUNBOOK.md</c>, wave sp2 lane SP2-F): the day-0 feature set,
/// the daemon's client handlers for the calls an operator makes (<c>splicein</c>, <c>spliceout</c>,
/// <c>exportchanbackup</c>, <c>verifychanbackup</c>, <c>createinvoice</c>, <c>payinvoice</c>), mining until a splice
/// is locked on both ends, and what LND alice sees of our channel.
/// </summary>
public static class Day0Harness
{
    /// <summary>The feerate of every splice the proofs start (the test node's 10 sat/vB estimate).</summary>
    public const uint SpliceFeeRatePerKw = 2_500;

    /// <summary>The most blocks mined one at a time until both ends are usable or locked a splice.</summary>
    public const int MaxBlocks = 12;

    /// <summary>BOLT 7's delay before a node forgets a channel whose funding output is spent.</summary>
    public const int SpentChannelPruneDelayBlocks = 72;

    public static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan s_blockEvery = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The runbook's day-0 features: since splicing plan D13 (wave d13) they are the defaults, <c>option_quiesce</c>,
    /// <c>option_splice</c> and <c>option_dual_fund</c> Optional without <c>AllowExperimentalFeatures</c>, which this
    /// pins off so the day-0 proofs show the defaults suffice. Applied on every start through
    /// <see cref="NLightningTestNode.ConfigureServices"/> (after the test node's own feature options).
    /// </summary>
    public static void EnableDay0Features(NLightningTestNode node) =>
        node.ConfigureServices = services => services.PostConfigure<NodeOptions>(o =>
        {
            o.Features.AllowExperimentalFeatures = false;
            if (o.Features.GetValidationErrors() is { Count: > 0 } errors)
                throw new InvalidOperationException(string.Join("; ", errors));
            if (o.Features.OptionQuiesce == FeatureSupport.No || o.Features.OptionSplice == FeatureSupport.No
             || o.Features.DualFund == FeatureSupport.No)
                throw new InvalidOperationException("D13: splice, quiesce and dual_fund are expected on by default");
        });

    /// <summary>
    /// The feature set of a build from before wave sp1 (no quiescence, splicing or dual funding), for the upgrade test.
    /// With <paramref name="beforeAnchorsAndPeerStorage"/> also that of a build from before waves O7b and RF1 (no
    /// <c>option_anchors</c> by default, no <c>option_provide_storage</c>), as the live Mutinynet node's build: its
    /// channels are <c>option_static_remotekey</c> channels.
    /// </summary>
    public static void UsePreSp1Features(NLightningTestNode node, bool beforeAnchorsAndPeerStorage = false) =>
        node.ConfigureServices = services => services.PostConfigure<NodeOptions>(o =>
        {
            o.Features.OptionQuiesce = FeatureSupport.No;
            o.Features.OptionSplice = FeatureSupport.No;
            o.Features.DualFund = FeatureSupport.No;
            if (!beforeAnchorsAndPeerStorage)
                return;

            o.Features.OptionAnchors = FeatureSupport.No;
            o.Features.OptionProvideStorage = FeatureSupport.No;
        });

    /// <summary>
    /// Calls <paramref name="hook"/> for every channel message <paramref name="node"/> sends, <b>before</b> the peer
    /// manager puts it into the peer's outbox, on every start of the node (kept across restarts; chained after the
    /// node's current <see cref="NLightningTestNode.ConfigureServices"/>, so set the feature options first).
    /// </summary>
    /// <remarks>
    /// <c>PeerManager</c> subscribes to <see cref="IChannelManager.OnResponseMessageReady"/> in its constructor and
    /// its outbox sends on its own task, so a handler added after the node started runs after the enqueue and races
    /// the send. This one is subscribed when the channel manager singleton is built, before any other subscriber, so
    /// a hook that resets the node's connections (<see cref="CrashableTcpService.CrashAsync"/>) keeps the message off
    /// the wire. The hook runs under the channel's lock: it must not wait on the channel.
    /// </remarks>
    public static void HookSentChannelMessages(NLightningTestNode node, Action<ChannelResponseMessageEventArgs> hook)
    {
        var previous = node.ConfigureServices;
        node.ConfigureServices = services =>
        {
            previous?.Invoke(services);
            var descriptor = services.Last(d => d.ServiceType == typeof(ChannelManager));
            var factory = descriptor.ImplementationFactory
                       ?? throw new InvalidOperationException("ChannelManager is expected to be built by a factory");
            services.Remove(descriptor);
            services.AddSingleton(sp =>
            {
                var channelManager = (ChannelManager)factory(sp);
                channelManager.OnResponseMessageReady += (_, args) => hook(args);
                return channelManager;
            });
        };
    }

    /// <summary>The daemon's client handler for <typeparamref name="TRequest"/>, as <c>nltg</c> reaches it.</summary>
    public static async Task<TResponse> HandleAsync<TRequest, TResponse>(NLightningTestNode node, TRequest request,
                                                                          CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>()
                          .HandleAsync(request, ct);
    }

    /// <summary><c>splicein &lt;channel&gt; &lt;sats&gt; --feerate 2500</c>.</summary>
    public static async Task<SpliceClientResponse> SpliceInAsync(NLightningTestNode node, ChannelId channelId,
                                                                 ulong amountSat, CancellationToken ct)
    {
        var response = await HandleAsync<SpliceInClientRequest, SpliceClientResponse>(
                           node, new SpliceInClientRequest(channelId, amountSat) { FeeRatePerKw = SpliceFeeRatePerKw },
                           ct);
        Console.WriteLine($"[{node.Name}] splicein {amountSat}: {response.State}, txid {Display(response.SpliceTxId)}, "
                        + $"capacity {response.NewCapacitySat}, reason {response.FailureReason}");
        return response;
    }

    /// <summary><c>spliceout &lt;channel&gt; &lt;sats&gt; --address &lt;address&gt; --feerate 2500</c>.</summary>
    public static async Task<SpliceClientResponse> SpliceOutAsync(NLightningTestNode node, ChannelId channelId,
                                                                  ulong amountSat, string address,
                                                                  CancellationToken ct)
    {
        var response = await HandleAsync<SpliceOutClientRequest, SpliceClientResponse>(
                           node, new SpliceOutClientRequest(channelId, amountSat)
                           {
                               Address = address,
                               FeeRatePerKw = SpliceFeeRatePerKw
                           }, ct);
        Console.WriteLine($"[{node.Name}] spliceout {amountSat} to {address}: {response.State}, txid "
                        + $"{Display(response.SpliceTxId)}, capacity {response.NewCapacitySat}, reason "
                        + response.FailureReason);
        return response;
    }

    /// <summary>The splice transaction of a signed splice (fails the test otherwise).</summary>
    public static uint256 AssertSigned(SpliceClientResponse response)
    {
        Assert.True(response.State == SpliceNegotiationState.Signed,
                    $"the splice is {response.State}: {response.FailureReason}");
        Assert.Null(response.FailureReason);
        Assert.NotNull(response.SpliceTxId);
        return ToUint256(response.SpliceTxId.Value);
    }

    /// <summary>
    /// Mines one block at a time (every LND of the fixture and both nodes at the tip after each) until the channel is
    /// usable on both ends, has its short channel id on both and carries no HTLC.
    /// </summary>
    public static async Task<(ChannelInfoClientResponse A, ChannelInfoClientResponse B)> MineUntilUsableAsync(
        LightningRegtestNetworkFixture fixture, IReadOnlyList<LNDNodeConnection> lndNodes, NLightningTestNode a,
        NLightningTestNode b, ChannelId channelId, CancellationToken ct)
    {
        for (var block = 0; ; block++)
        {
            var ours = await a.GetChannelAsync(channelId, ct);
            var theirs = await b.GetChannelAsync(channelId, ct);
            Console.WriteLine($"[day0] +{block} block(s): {a.Name} {ours.Describe()}; {b.Name} {theirs.Describe()}");
            if (IsSettled(ours) && IsSettled(theirs) && ours.ShortChannelId is not null
             && theirs.ShortChannelId is not null)
                return (ours, theirs);

            Assert.True(block < MaxBlocks, $"channel {channelId} not usable on both ends after {MaxBlocks} blocks");
            await ChainSync.MineAndWaitAsync(fixture, 1, lndNodes, [a, b], ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>
    /// Mines one block at a time until both ends run the channel on <paramref name="spliceTxId"/> (the splice is
    /// locked: <c>splice_locked</c> both ways), are usable and carry no HTLC.
    /// </summary>
    public static async Task<(ChannelInfoClientResponse A, ChannelInfoClientResponse B)> MineUntilSpliceLockedAsync(
        LightningRegtestNetworkFixture fixture, IReadOnlyList<LNDNodeConnection> lndNodes, NLightningTestNode a,
        NLightningTestNode b, ChannelId channelId, uint256 spliceTxId, CancellationToken ct)
    {
        for (var block = 1; ; block++)
        {
            await ChainSync.MineAndWaitAsync(fixture, 1, lndNodes, [a, b], ct);
            ChannelInfoClientResponse? ours = null, theirs = null;
            try
            {
                // The lock is one save per end after the depth; give both a moment at this height
                await Poll.UntilAsync(async () =>
                {
                    ours = await a.GetChannelAsync(channelId, ct);
                    theirs = await b.GetChannelAsync(channelId, ct);
                    return RunsOn(ours, spliceTxId) && RunsOn(theirs, spliceTxId) && IsSettled(ours)
                        && IsSettled(theirs);
                }, TimeSpan.FromSeconds(3), "splice locked on both ends", ct, TimeSpan.FromMilliseconds(500));
                Console.WriteLine($"[day0] splice {spliceTxId} locked after {block} block(s): {a.Name} "
                                + $"{ours!.Describe()}; {b.Name} {theirs!.Describe()}");
                return (ours!, theirs!);
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"[day0] +{block} block(s): {a.Name} {ours?.Describe()} on "
                                + $"{Display(ours?.FundingTxId)}; {b.Name} {theirs?.Describe()} on "
                                + Display(theirs?.FundingTxId));
                Assert.True(block < MaxBlocks,
                            $"the splice {spliceTxId} was not locked by both ends in {MaxBlocks} blocks");
            }
        }
    }

    /// <summary>
    /// Our end of <paramref name="channelId"/> once it is usable with no HTLC in flight.
    /// </summary>
    public static Task<ChannelInfoClientResponse> WaitSettledAsync(NLightningTestNode node, ChannelId channelId,
                                                                   CancellationToken ct) =>
        Poll.ForAsync(async () =>
        {
            var channel = await node.GetChannelAsync(channelId, ct);
            return IsSettled(channel) ? channel : null;
        }, StepTimeout, $"{node.Name}: channel {channelId} usable with no HTLC in flight", ct,
                      TimeSpan.FromMilliseconds(500));

    /// <summary>
    /// Waits until <paramref name="a"/> and <paramref name="b"/> are connected (after a restart the peer manager's
    /// reconnect backoff usually does it); connects <paramref name="a"/> to <paramref name="b"/> otherwise.
    /// </summary>
    public static async Task EnsureConnectedAsync(NLightningTestNode a, NLightningTestNode b, CancellationToken ct)
    {
        try
        {
            await Poll.UntilAsync(() => a.IsConnectedTo(b.NodeId) && b.IsConnectedTo(a.NodeId),
                                  TimeSpan.FromSeconds(20), $"{a.Name} and {b.Name} reconnected", ct);
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"[day0] {a.Name} and {b.Name} did not reconnect on their own: connecting");
            await a.ConnectToAsync(b, ct);
        }
    }

    /// <summary>
    /// Connects <paramref name="a"/> and <paramref name="b"/> so each keeps the other in its <c>Peers</c> table:
    /// <paramref name="a"/> dials <paramref name="b"/>, hangs up, and <paramref name="b"/> dials <paramref name="a"/>.
    /// </summary>
    /// <remarks>
    /// <c>PeerManager</c> saves the peer of an inbound connection only when it does not come from <c>127.0.0.1</c>,
    /// and a restarted node loads its channels peer by peer from that table: the accepter of a channel opened over one
    /// loopback connection forgets the channel at its restart and answers the opener's <c>channel_reestablish</c>
    /// with <c>error</c> "unknown channel" (found by this lane's upgrade proof; reported to the ledger). Live nodes
    /// connect over other addresses, so the proofs make both ends dial once instead.
    /// </remarks>
    public static async Task ConnectBothWaysAsync(NLightningTestNode a, NLightningTestNode b, CancellationToken ct)
    {
        await a.ConnectToAsync(b, ct);
        a.PeerManager.DisconnectPeer(b.NodeId);
        await Poll.UntilAsync(() => !a.IsConnectedTo(b.NodeId) && !b.IsConnectedTo(a.NodeId), StepTimeout,
                              $"{a.Name} and {b.Name} disconnected", ct);
        await b.ConnectToAsync(a, ct);
    }

    /// <summary>
    /// <paramref name="payee"/>'s invoice for <paramref name="amountSat"/> paid by <paramref name="payer"/>
    /// (<c>createinvoice</c>, <c>payinvoice</c>); the payment succeeds and the invoice is settled.
    /// </summary>
    public static async Task PayAsync(NLightningTestNode payer, NLightningTestNode payee, long amountSat,
                                      string description, CancellationToken ct)
    {
        var invoice = await payee.CreateInvoiceAsync(LightningMoney.Satoshis(amountSat), description, ct);
        var payment = await payer.PayInvoiceAsync(invoice.Bolt11!, ct);
        Console.WriteLine($"[day0] {payer.Name} pays {payee.Name} {amountSat} sat ({description}): {payment.Status}, "
                        + $"fee {payment.Fee.MilliSatoshi} msat");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        await Poll.UntilAsync(async () => (await payee.GetInvoiceAsync(invoice.PaymentHash, ct))?.Status
                                        == InvoiceStatus.Settled, StepTimeout,
                              $"{payee.Name}'s invoice ({description}) settled", ct);
    }

    /// <summary>
    /// A payment from <paramref name="lnd"/> to <paramref name="payee"/>'s invoice over any route LND finds (retried
    /// while LND has no route yet, NL-319); succeeds and settles.
    /// </summary>
    public static async Task<Payment> LndPaysAsync(LNDNodeConnection lnd, NLightningTestNode payee, long amountSat,
                                                   string description, CancellationToken ct)
    {
        var invoice = await payee.CreateInvoiceAsync(LightningMoney.Satoshis(amountSat), description, ct);
        await LndTestHelpers.ResetMissionControlAsync(lnd, ct);
        var payment = await Poll.ForAsync(async () =>
        {
            var result = await LndTestHelpers.SendPaymentV2Async(lnd, LndTestHelpers.PinnedPayment(invoice.Bolt11!, []),
                                                                 ct);
            Console.WriteLine($"[day0] {lnd.LocalAlias} pays {payee.Name} {amountSat} sat ({description}): "
                            + $"{result.Status} {result.FailureReason}");
            return result.Status == Payment.Types.PaymentStatus.Succeeded
                || result.FailureReason != PaymentFailureReason.FailureReasonNoRoute
                       ? result
                       : null;
        }, NetworkTimeout, $"{lnd.LocalAlias}'s payment to {payee.Name} final (not no_route)", ct,
                                          TimeSpan.FromSeconds(5));
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, payment.Status);
        await Poll.UntilAsync(async () => (await payee.GetInvoiceAsync(invoice.PaymentHash, ct))?.Status
                                        == InvoiceStatus.Settled, StepTimeout,
                              $"{payee.Name}'s invoice ({description}) settled", ct);
        return payment;
    }

    /// <summary>
    /// Waits until <paramref name="lnd"/>'s graph has <paramref name="scid"/> between <paramref name="a"/> and
    /// <paramref name="b"/> with both policies, mining a block every 15 s without it (announcements go out at 6
    /// confirmations); returns the edge.
    /// </summary>
    public static async Task<ChannelEdge> WaitLndHasChannelAsync(LightningRegtestNetworkFixture fixture,
                                                                 LNDNodeConnection lnd, ulong scid,
                                                                 NLightningTestNode a, NLightningTestNode b,
                                                                 CancellationToken ct)
    {
        ChannelEdge? edge = null;
        await GossipGraphProbe.MineUntilAsync(async () =>
        {
            edge = await GossipGraphProbe.TryGetChanInfoAsync(lnd, scid, ct);
            return edge is { Node1Policy: not null, Node2Policy: not null };
        }, () => ChainSync.MineAndWaitAsync(fixture, 1, fixture.LndNodes, [a, b], ct), s_blockEvery, 6,
                                              NetworkTimeout,
                                              $"{lnd.LocalAlias} has {new ShortChannelId(scid)} with both policies",
                                              ct);
        var ends = new[] { edge!.Node1Pub.ToLowerInvariant(), edge.Node2Pub.ToLowerInvariant() };
        Assert.Contains(a.NodeIdHex, ends);
        Assert.Contains(b.NodeIdHex, ends);
        return edge;
    }

    /// <summary>
    /// Waits until <paramref name="lnd"/>'s graph no longer has <paramref name="scid"/>, whose funding output is
    /// spent in a confirmed block, mining one block at a time; returns (and prints) the blocks mined until LND forgot
    /// the edge, i.e. the pruning delay the fixture's LND uses.
    /// </summary>
    /// <remarks>
    /// BOLT 7: "once its funding output has been spent OR reorganized out: SHOULD forget a channel after a 72-block
    /// delay", and splice-aware nodes wait on purpose so the new announcement arrives first (our own
    /// <c>GraphPruner</c> does, splicing plan D12). An LND that prunes at the spending block forgets it at once; one
    /// that follows the delay forgets it after at most <see cref="SpentChannelPruneDelayBlocks"/> + 1 blocks.
    /// </remarks>
    public static async Task<int> WaitLndForgotChannelAsync(LightningRegtestNetworkFixture fixture,
                                                            LNDNodeConnection lnd, ulong scid, NLightningTestNode a,
                                                            NLightningTestNode b, CancellationToken ct)
    {
        var description = $"{lnd.LocalAlias} forgot {new ShortChannelId(scid)}";
        for (var mined = 0; ; mined++)
        {
            try
            {
                // Without a new block give LND longer: it may still be handling the spending block
                await Poll.UntilAsync(async () => await GossipGraphProbe.TryGetChanInfoAsync(lnd, scid, ct) is null,
                                      mined == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(3), description,
                                      ct, GossipGraphProbe.PollInterval);
                Console.WriteLine($"[day0] {description} after {mined} more block(s)");
                return mined;
            }
            catch (TimeoutException)
            {
                Assert.True(mined <= SpentChannelPruneDelayBlocks,
                            $"{lnd.LocalAlias} still has {new ShortChannelId(scid)} {mined} blocks later");
                await ChainSync.MineAndWaitAsync(fixture, 1, fixture.LndNodes, [a, b], ct);
            }
        }
    }

    /// <summary>
    /// <c>exportchanbackup</c> then <c>verifychanbackup</c> of the export (runbook: after every step): the backup is
    /// valid for the node, holds <paramref name="channel"/> on its current funding outpoint, capacity and short
    /// channel id, and our key index re-derives the keys it recorded (after a splice with a rotated funding key: lane
    /// SP2-E's <c>LocalFundingKeyIndex</c>).
    /// </summary>
    public static async Task<byte[]> BackupAsync(NLightningTestNode node, ChannelInfoClientResponse channel,
                                                 string step, CancellationToken ct)
    {
        var export = await HandleAsync<ExportChanBackupClientRequest, ExportChanBackupClientResponse>(
                         node, new ExportChanBackupClientRequest(), ct);
        Assert.Contains(channel.ChannelId, export.ChannelIds);
        var verified = await HandleAsync<VerifyChanBackupClientRequest, VerifyChanBackupClientResponse>(
                           node, new VerifyChanBackupClientRequest { Backup = export.Backup }, ct);
        var entry = verified.Channels.SingleOrDefault(c => c.ChannelId == channel.ChannelId);
        Console.WriteLine($"[day0] {node.Name} backup after {step}: {export.Backup.Length} bytes, "
                        + $"{export.ChannelIds.Count} channel(s), valid {verified.IsValid} ({verified.Error}); "
                        + (entry is null
                               ? "channel missing"
                               : $"funding {Display(entry.FundingTxId)}:{entry.FundingOutputIndex}, "
                               + $"capacity {entry.CapacitySat}, scid {entry.ShortChannelId}, "
                               + $"keys match {entry.KeysMatch}, state {entry.LocalState}"));
        Assert.True(verified.IsValid, $"{node.Name}'s backup after {step} is not valid: {verified.Error}");
        Assert.NotNull(entry);
        Assert.Equal(channel.FundingTxId, entry.FundingTxId);
        Assert.Equal(channel.FundingOutputIndex, entry.FundingOutputIndex);
        Assert.Equal((ulong)channel.Capacity.Satoshi, entry.CapacitySat);
        if (channel.ShortChannelId is { } scid)
        {
            Assert.NotNull(entry.ShortChannelId);
            Assert.Equal(scid.ToUInt64(), entry.ShortChannelId.Value.ToUInt64());
        }

        Assert.True(entry.KeysMatch, $"{node.Name}'s backup after {step}: the key index does not re-derive the keys");
        return export.Backup;
    }

    /// <summary>The fee of <paramref name="txId"/> (its inputs' values minus its outputs').</summary>
    public static async Task<long> GetFeeAsync(LightningRegtestNetworkFixture fixture, uint256 txId,
                                               CancellationToken ct)
    {
        var tx = await fixture.Bitcoin.GetRawTransactionAsync(txId, true, ct);
        var spent = Money.Zero;
        foreach (var input in tx.Inputs)
            spent += (await fixture.Bitcoin.GetRawTransactionAsync(input.PrevOut.Hash, true, ct))
                    .Outputs[input.PrevOut.N].Value;

        return (spent - tx.TotalOut).Satoshi;
    }

    /// <summary>Waits until <paramref name="txId"/> is in bitcoind's mempool.</summary>
    public static Task WaitInMempoolAsync(LightningRegtestNetworkFixture fixture, uint256 txId, CancellationToken ct) =>
        Poll.UntilAsync(async () => (await fixture.Bitcoin.GetRawMempoolAsync(ct)).Contains(txId), StepTimeout,
                        $"{txId} in the mempool", ct, TimeSpan.FromMilliseconds(500));

    /// <summary>Our stored (internal byte order) txid as NBitcoin's <see cref="uint256"/>.</summary>
    public static uint256 ToUint256(TxId txId) => new((byte[])txId);

    /// <summary>A txid in display order, or <c>-</c>.</summary>
    public static string Display(TxId? txId) => txId is { } id ? ToUint256(id).ToString() : "-";

    private static bool RunsOn(ChannelInfoClientResponse? channel, uint256 fundingTxId) =>
        channel?.FundingTxId is { } funding && ToUint256(funding) == fundingTxId;

    private static bool IsSettled(ChannelInfoClientResponse channel) =>
        channel.IsUsable() && channel is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 };
}
using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Onchain;

using Abcd;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using Fixtures;
using Infrastructure.Transport.Interfaces;
using Utils;

/// <summary>
/// BOLT 5 plan Proof O4 (remote commitment resolution) against LND david, which force-closes:
/// <list type="bullet">
///   <item>(a) an idle channel with a push to david: our <c>to_remote</c> is swept to our wallet in the next blocks
///   (B5-RMT-02, D5).</item>
///   <item>(b) david pays our invoice and our fulfill is persisted but never reaches david (the wire dies right after
///   the save and we stay down while david force-closes): we claim the HTLC on chain with the preimage before its
///   <c>cltv_expiry</c> and the invoice is settled (B5-RMT-RO-01).</item>
///   <item>(c) we pay a david hold invoice that is never settled: after <c>cltv_expiry</c> our timeout claim confirms
///   and, once reasonably deep, the payment fails (B5-RMT-LO-02).</item>
///   <item>(d) david force-closes with the commitment we signed while its <c>revoke_and_ack</c> is unacked on our side
///   (our database is put back to the moment our <c>commitment_signed</c> was saved): we classify
///   <c>RemoteNextCommitment</c> and resolve it with that commitment's point (B5-RMT-01).</item>
///   <item>(e) alice pays david through us; alice force-closes while david holds the HTLC, then david settles: the
///   upstream fulfill can no longer be sent, so we claim alice's HTLC on chain with the preimage the downstream
///   fulfill revealed, before its <c>cltv_expiry</c> (B5-RMT-RO-01 for a forward).</item>
/// </list>
/// </summary>
/// <remarks>
/// Needs the BOLT 5 on-chain watcher and resolution executor (plan O2-T5) wired into the node: it classifies the
/// funding spend and drives the <c>IOutputResolver</c> <c>RemoteCommitResolver</c> (O4, ABCD W5-C). The assertions read what the resolver persists
/// (<c>ChannelCloses</c>, <c>OutputResolutions</c>, <c>BroadcastTransactions</c>) and what bitcoind, our wallet and
/// our invoices/payments show. Run with <c>scripts/run-onchain.sh</c> (own process, own fixture).
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
public class OnchainO4Tests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);
    private const ulong HoldInvoiceCltvExpiry = 24;

    /// <summary>D9 <c>Onchain:ReasonableDepth</c> default: an HTLC is failed upstream when its claim is this deep.</summary>
    private const int ReasonableDepth = 6;

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public OnchainO4Tests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "onchain-o4");
        await Node.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Proof O4 (a): david force-closes an idle channel; our <c>to_remote</c> reaches our wallet.</summary>
    [Fact]
    public async Task Given_IdleChannelWithPush_When_DavidForceCloses_Then_OurToRemoteSweptToOurWallet()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var david = GetDavid();
        var channel = await OpenUsableChannelAsync(david, s_push, ct);

        // Act: david force-closes; the commitment confirms
        var commitmentTxId = await ForceCloseAsync(david, channel, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [Node], ct);

        // Assert: we classified david's current commitment and saved one to_remote row
        var close = await WaitForCloseAsync(channel.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
        Assert.Equal(commitmentTxId, new uint256((byte[])close.CommitmentTransactionId));
        var toRemote = await WaitForOutputAsync(channel.ChannelId,
                                                o => o is
                                                {
                                                    Descriptor: OutputDescriptorKind.PaymentToRemote,
                                                    ResolvingTransactionId: not null
                                                }, "our to_remote sweep saved", ct);
        var commitment = (await _fixture.Bitcoin.GetRawTransactionInfoAsync(commitmentTxId, ct)).Transaction;
        var toRemoteValue = commitment.Outputs[(int)toRemote.OutputIndex].Value;
        Console.WriteLine($"to_remote {toRemoteValue} at vout {toRemote.OutputIndex}");

        // The sweep confirms in the next block and pays our wallet
        var sweep = await MineUntilConfirmedAsync(toRemote.ResolvingTransactionId!.Value, [david], ct);
        Assert.Single(sweep.Inputs);
        Assert.Equal(commitmentTxId, sweep.Inputs[0].PrevOut.Hash);
        var fee = toRemoteValue - sweep.Outputs[0].Value;
        Assert.True(fee > Money.Zero && fee < toRemoteValue / 2, $"sweep fee {fee}");
        await Poll.UntilAsync(() => Node.Services.GetRequiredService<IUtxoMemoryRepository>()
                                        .TryGetUtxo(new Domain.Bitcoin.ValueObjects.TxId(sweep.GetHash().ToBytes()),
                                                    0, out _),
                              s_timeout, "the sweep output credited to our wallet", ct);
        await WaitForOutputStateAsync(channel.ChannelId, toRemote.OutputIndex, OutputResolutionState.Resolved, ct);
    }

    /// <summary>
    /// Proof O4 (b): our fulfill of david's HTLC is saved but lost with the connection; david force-closes while we
    /// are down; we claim the HTLC with the preimage before its expiry.
    /// </summary>
    [Fact]
    public async Task Given_OurFulfillNeverReachedDavid_When_DavidForceCloses_Then_WeClaimWithThePreimageBeforeExpiry()
    {
        // Arrange: david (with the push) pays our invoice; the wire dies right after our fulfill is saved
        var ct = TestContext.Current.CancellationToken;
        var david = GetDavid();
        var channel = await OpenUsableChannelAsync(david, s_push, ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(david, channel.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        var tcpService = Assert.IsType<CrashableTcpService>(Node.Services.GetRequiredService<ITcpService>());
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Node.ChannelManager.OnResponseMessageReady += (_, args) =>
        {
            // Raised under the channel lock right after the fulfill's save: cut every connection before it goes out
            if (args.ResponseMessage is not UpdateFulfillHtlcMessage || crashed.Task.IsCompleted)
                return;

            tcpService.CrashAsync().GetAwaiter().GetResult();
            crashed.TrySetResult();
        };
        var invoice = await Node.CreateInvoiceAsync(LightningMoney.Satoshis(50_000), "o4 (b) claim with preimage", ct);
        // LND lists the channel active before its router has the private channel's edge in its graph; until then
        // pathfinding sees no local balance (insufficient_balance), so the payment is retried until the edge is there
        await PayUntilSentAsync(david, invoice.Bolt11, lndChannel.ChanId, crashed.Task, ct);
        var htlc = Assert.Single(GetHtlcs(channel.ChannelId), h => h.Direction == HtlcDirection.Incoming);
        Assert.NotNull(htlc.Removal);
        Assert.True(htlc.Removal.IsFulfill);
        Console.WriteLine($"Fulfill of HTLC {htlc.Id} saved and lost; cltv_expiry {htlc.CltvExpiry}");
        await Node.StopAsync();

        // Act: david force-closes while we are down (its commitment still holds the HTLC); we come back after 1 block
        var commitmentTxId = await ForceCloseAsync(david, channel, ct);
        await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [], ct);
        await Node.StartAsync(ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [david], [Node], ct);

        // Assert: the HTLC is claimed with <sig> <preimage> before cltv_expiry
        var close = await WaitForCloseAsync(channel.ChannelId, ct);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
        Assert.Equal(commitmentTxId, new uint256((byte[])close.CommitmentTransactionId));
        var row = await WaitForOutputAsync(channel.ChannelId,
                                           o => o is
                                           {
                                               Descriptor: OutputDescriptorKind.RemoteOfferedHtlc,
                                               ResolvingTransactionId: not null
                                           }, "our preimage claim saved", ct);
        Assert.Equal(htlc.Id, row.HtlcId);
        var claim = await MineUntilConfirmedAsync(row.ResolvingTransactionId!.Value, [david], ct);
        var confirmedAt = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
        Assert.True(confirmedAt < htlc.CltvExpiry, $"claimed at {confirmedAt}, expiry {htlc.CltvExpiry}");
        Assert.Equal(0U, claim.LockTime.Value);
        Assert.Equal((byte[])htlc.Removal.PaymentPreimage!.Value, claim.Inputs[0].WitScript.Pushes.ElementAt(1));
        await WaitForOutputStateAsync(channel.ChannelId, row.OutputIndex, OutputResolutionState.Resolved, ct);

        var ours = await Node.GetInvoiceAsync(invoice.PaymentHash, ct);
        Assert.NotNull(ours);
        Assert.Equal(InvoiceStatus.Settled, ours.Status);
    }

    /// <summary>
    /// Proof O4 (c): we pay a david hold invoice that is never settled; david force-closes; after the expiry our timeout
    /// claim confirms and the payment fails once it is reasonably deep.
    /// </summary>
    [Fact]
    public async Task Given_OurHtlcHeldByDavid_When_DavidForceCloses_Then_TimeoutClaimAfterExpiryAndPaymentFailed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var david = GetDavid();
        var channel = await OpenUsableChannelAsync(david, null, ct);
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [], ct,
                                                                   "o4 (c) timeout claim", HoldInvoiceCltvExpiry);
        try
        {
            var inFlight = await Node.PayInvoiceAsync(holdInvoice.PaymentRequest, ct, timeoutSeconds: 2);
            Assert.Equal(PaymentStatus.InFlight, inFlight.Status);
            await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                          s_timeout, ct);
            var htlc = await WaitForHtlcInBothCommitmentsAsync(channel.ChannelId, HtlcDirection.Outgoing, ct);
            Console.WriteLine($"Our HTLC {htlc.Id}: cltv_expiry {htlc.CltvExpiry}");

            // Act: david force-closes
            await ForceCloseAsync(david, channel, ct);
            await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [Node], ct);
            var close = await WaitForCloseAsync(channel.ChannelId, ct);
            Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);

            // Assert: nothing is claimed before cltv_expiry
            var row = await WaitForOutputAsync(channel.ChannelId,
                                               o => o.Descriptor == OutputDescriptorKind.RemoteReceivedHtlc,
                                               "our HTLC output row", ct);
            Assert.Equal(htlc.Id, row.HtlcId);
            var vout = row.OutputIndex;
            var tip = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
            if (htlc.CltvExpiry - 1 > tip)
                await ChainSync.MineAndWaitAsync(_fixture, (int)(htlc.CltvExpiry - 1 - tip), [david], [Node], ct);
            Assert.Null((await GetOutputAsync(channel.ChannelId, vout))?.ResolvingTransactionId);

            // At cltv_expiry our claim goes out (nLockTime = cltv_expiry) and confirms in the next block
            await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [Node], ct);
            var claimed = await WaitForOutputAsync(channel.ChannelId,
                                                   o => o.OutputIndex == vout && o.ResolvingTransactionId is not null,
                                                   "our timeout claim saved", ct);
            var claim = await MineUntilConfirmedAsync(claimed.ResolvingTransactionId!.Value, [david], ct);
            Assert.Equal(htlc.CltvExpiry, claim.LockTime.Value);
            Assert.Empty(claim.Inputs[0].WitScript.Pushes.ElementAt(1));

            // The payment fails once the claim is reasonably deep, not before
            var payment = await Node.GetPaymentAsync(new Domain.Crypto.ValueObjects.Hash(paymentHash), ct);
            Assert.Equal(PaymentStatus.InFlight, payment?.Status);
            await ChainSync.MineAndWaitAsync(_fixture, ReasonableDepth - 1, [david], [Node], ct);
            await Poll.UntilAsync(async () =>
                                      (await Node.GetPaymentAsync(new Domain.Crypto.ValueObjects.Hash(paymentHash), ct))
                                    ?.Status == PaymentStatus.Failed,
                                  s_timeout, "our payment failed after the on-chain timeout", ct);
        }
        finally
        {
            await CancelHoldInvoiceQuietlyAsync(david, paymentHash);
        }
    }

    /// <summary>
    /// Proof O4 (d): david holds our HTLC; our database goes back to the moment our <c>commitment_signed</c> for it was
    /// saved (david's <c>revoke_and_ack</c> unacked), and david force-closes with the commitment we signed: we classify
    /// it as the next remote commitment and resolve it.
    /// </summary>
    [Fact]
    public async Task Given_OurCommitmentSignedUnacked_When_DavidForceClosesWithIt_Then_RemoteNextCommitResolved()
    {
        // Arrange: snapshot our SQLite files right after the commitment_signed that adds our HTLC is saved
        var ct = TestContext.Current.CancellationToken;
        var david = GetDavid();
        var channel = await OpenUsableChannelAsync(david, null, ct);
        var databasePath = Node.DatabaseFilePath ?? throw new InvalidOperationException("A SQLite node is needed");
        var backupDirectory = Directory.CreateTempSubdirectory("nltg-o4d-");
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Node.ChannelManager.OnResponseMessageReady += (_, args) =>
        {
            // Raised under the channel lock right after the save: the database holds our unacked commitment_signed
            if (args.ResponseMessage is not CommitmentSignedMessage || saved.Task.IsCompleted)
                return;

            CopyDatabase(databasePath, backupDirectory.FullName);
            saved.TrySetResult();
        };
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [], ct,
                                                                   "o4 (d) remote next commitment",
                                                                   HoldInvoiceCltvExpiry);
        try
        {
            await Node.PayInvoiceAsync(holdInvoice.PaymentRequest, ct, timeoutSeconds: 2);
            await saved.Task.WaitAsync(s_timeout, ct);
            await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                          s_timeout, ct);
            var htlc = await WaitForHtlcInBothCommitmentsAsync(channel.ChannelId, HtlcDirection.Outgoing, ct);

            // Act: we stop and come back from the snapshot, but only after david force-closed (its latest commitment
            // is the one our commitment_signed signed) and it confirmed
            await Node.StopAsync();
            RestoreDatabase(backupDirectory.FullName, databasePath);
            var commitmentTxId = await ForceCloseAsync(david, channel, ct);
            await ChainSync.MineAndWaitAsync(_fixture, 1, [david], [], ct);
            await Node.StartAsync(ct);
            await ChainSync.WaitAllAtTipAsync(_fixture, [david], [Node], ct);

            // Assert: classified as the next remote commitment; to_remote swept, our HTLC claimed after its expiry
            var close = await WaitForCloseAsync(channel.ChannelId, ct);
            Assert.Equal(ChannelCloseKind.RemoteNextCommitment, close.Kind);
            Assert.Equal(commitmentTxId, new uint256((byte[])close.CommitmentTransactionId));
            var toRemote = await WaitForOutputAsync(channel.ChannelId,
                                                    o => o is
                                                    {
                                                        Descriptor: OutputDescriptorKind.PaymentToRemote,
                                                        ResolvingTransactionId: not null
                                                    }, "our to_remote sweep saved", ct);
            await MineUntilConfirmedAsync(toRemote.ResolvingTransactionId!.Value, [david], ct);

            var row = await WaitForOutputAsync(channel.ChannelId,
                                               o => o.Descriptor == OutputDescriptorKind.RemoteReceivedHtlc,
                                               "our HTLC output row in the next commitment", ct);
            Assert.Equal(htlc.Id, row.HtlcId);
            var vout = row.OutputIndex;
            var tip = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
            if (htlc.CltvExpiry > tip)
                await ChainSync.MineAndWaitAsync(_fixture, (int)(htlc.CltvExpiry - tip), [david], [Node], ct);
            var claimed = await WaitForOutputAsync(channel.ChannelId,
                                                   o => o.OutputIndex == vout && o.ResolvingTransactionId is not null,
                                                   "our timeout claim on the next commitment saved", ct);
            var claim = await MineUntilConfirmedAsync(claimed.ResolvingTransactionId!.Value, [david], ct);
            Assert.Equal(htlc.CltvExpiry, claim.LockTime.Value);
            await WaitForOutputStateAsync(channel.ChannelId, vout, OutputResolutionState.Resolved, ct);
        }
        finally
        {
            await CancelHoldInvoiceQuietlyAsync(david, paymentHash);
            backupDirectory.Delete(true);
        }
    }

    /// <summary>
    /// Proof O4 (e): alice -> us -> david; alice force-closes while david holds the HTLC, then david settles. Our
    /// upstream fulfill is refused (the channel is on chain), so the downstream preimage is the only one left: we claim
    /// alice's HTLC with it before its expiry.
    /// </summary>
    [Fact]
    public async Task Given_ForwardFulfilledAfterUpstreamForceClose_When_Resolved_Then_UpstreamHtlcClaimedWithPreimage()
    {
        // Arrange: alice -> us -> david, both channels funded by us (alice gets a push so she can pay through us)
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var david = GetDavid();
        var upstream = await OpenUsableChannelAsync(alice, s_push, ct);
        var downstream = await OpenUsableChannelAsync(david, null, ct);
        var upstreamLnd = await LndTestHelpers.GetChannelByPointAsync(alice, upstream.ChannelPoint(), ct);
        Assert.NotNull(upstreamLnd);
        var downstreamScid = (await Node.GetChannelAsync(downstream.ChannelId, ct)).ShortChannelId;
        Assert.NotNull(downstreamScid);
        var routing = Node.Services.GetRequiredService<IOptions<NodeOptions>>().Value.Routing;
        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var hint = LndTestHelpers.RouteHint(LndTestHelpers.HopHint(Node.NodeIdHex, downstreamScid.Value.ToUInt64(),
                                                                   routing.FeeBaseMsat,
                                                                   routing.FeeProportionalMillionths,
                                                                   routing.CltvExpiryDelta));
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(david, paymentHash, 50_000_000, [hint], ct,
                                                                   "o4 (e) claim after downstream fulfill",
                                                                   HoldInvoiceCltvExpiry);
        try
        {
            await LndTestHelpers.ResetMissionControlAsync(alice, ct);
            _ = LndTestHelpers.SendPaymentV2Async(
                alice, LndTestHelpers.PinnedPayment(holdInvoice.PaymentRequest, [upstreamLnd.ChanId]), ct,
                TimeSpan.FromMinutes(5));
            await LndTestHelpers.WaitForInvoiceStateAsync(david, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                          s_timeout, ct);
            var incoming = await WaitForHtlcInBothCommitmentsAsync(upstream.ChannelId, HtlcDirection.Incoming, ct);
            await WaitForHtlcInBothCommitmentsAsync(downstream.ChannelId, HtlcDirection.Outgoing, ct);
            Console.WriteLine($"Incoming HTLC {incoming.Id}: cltv_expiry {incoming.CltvExpiry}");

            // Act: alice force-closes; her commitment (with her HTLC to us) confirms; nothing can be claimed yet
            var commitmentTxId = await ForceCloseAsync(alice, upstream, ct);
            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice, david], [Node], ct);
            var close = await WaitForCloseAsync(upstream.ChannelId, ct);
            Assert.Equal(ChannelCloseKind.RemoteCommitment, close.Kind);
            Assert.Equal(commitmentTxId, new uint256((byte[])close.CommitmentTransactionId));
            var row = await WaitForOutputAsync(upstream.ChannelId,
                                               o => o.Descriptor == OutputDescriptorKind.RemoteOfferedHtlc,
                                               "alice's HTLC output row", ct);
            Assert.Equal(incoming.Id, row.HtlcId);
            Assert.Null(row.ResolvingTransactionId);

            // Then david settles: the downstream fulfill reaches us, the upstream fulfill cannot go out
            await LndTestHelpers.SettleInvoiceAsync(david, preimage, ct);
            await Poll.UntilAsync(async () =>
            {
                using var scope = Node.Services.CreateScope();
                var circuit = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ForwardCircuitDbRepository
                                         .GetByIncomingAsync(upstream.ChannelId, incoming.Id);
                return circuit?.Status == ForwardCircuitStatus.Fulfilled;
            }, s_timeout, "the forward fulfilled downstream", ct);
            await ChainSync.MineAndWaitAsync(_fixture, 1, [alice, david], [Node], ct);

            // Assert: claimed with <sig> <preimage> before cltv_expiry
            var claimed = await WaitForOutputAsync(upstream.ChannelId,
                                                   o => o.OutputIndex == row.OutputIndex
                                                     && o.ResolvingTransactionId is not null,
                                                   "our preimage claim of alice's HTLC saved", ct);
            var claim = await MineUntilConfirmedAsync(claimed.ResolvingTransactionId!.Value, [alice, david], ct);
            var confirmedAt = (uint)await _fixture.Bitcoin.GetBlockCountAsync(ct);
            Assert.True(confirmedAt < incoming.CltvExpiry, $"claimed at {confirmedAt}, expiry {incoming.CltvExpiry}");
            Assert.Equal(0U, claim.LockTime.Value);
            Assert.Equal(preimage, claim.Inputs[0].WitScript.Pushes.ElementAt(1));
            await WaitForOutputStateAsync(upstream.ChannelId, row.OutputIndex, OutputResolutionState.Resolved, ct);
        }
        finally
        {
            await CancelHoldInvoiceQuietlyAsync(david, paymentHash);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var line in _node?.NodeLog.TakeLast(300) ?? [])
                Console.WriteLine(line);
            await DockerDiagnostics.DumpContainerLogsAsync(["alice", "david"]);
        }

        if (_node is not null)
            await _node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private LNDNodeConnection GetDavid() => _fixture.GetLndNode("david");

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(LNDNodeConnection peer,
        LightningMoney? push, CancellationToken ct)
    {
        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var peerAddress = await Node.ConnectToAsync(peer, ct);
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

    /// <summary>
    /// Starts a pinned LND payment and returns once <paramref name="sent"/> completes. A payment that LND fails at
    /// once for want of a route (its router adds the private channel's edge a moment after the channel turns active)
    /// is started again; any other end of the payment before <paramref name="sent"/> fails the test.
    /// </summary>
    private static async Task PayUntilSentAsync(LNDNodeConnection lnd, string bolt11, ulong chanId, Task sent,
                                                CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_timeout);
        while (true)
        {
            await LndTestHelpers.ResetMissionControlAsync(lnd, ct);
            var payment = LndTestHelpers.SendPaymentV2Async(lnd, LndTestHelpers.PinnedPayment(bolt11, [chanId]), ct,
                                                            TimeSpan.FromMinutes(5));
            if (await Task.WhenAny(sent, payment).WaitAsync(deadline.Token) == sent)
                return;

            var result = await payment;
            if (result.FailureReason is not (PaymentFailureReason.FailureReasonInsufficientBalance
                                             or PaymentFailureReason.FailureReasonNoRoute))
                Assert.Fail($"{lnd.LocalAlias}'s payment ended before it reached us: {result.Status} "
                          + $"{result.FailureReason}");

            Console.WriteLine($"{lnd.LocalAlias}'s payment failed with {result.FailureReason}; retrying");
            await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token);
        }
    }

    /// <summary>LND <c>CloseChannel { force = true }</c>; returns the commitment's txid once it is broadcast.</summary>
    private static async Task<uint256> ForceCloseAsync(LNDNodeConnection lnd,
                                                       OpenChannelClientSubscriptionResponse channel,
                                                       CancellationToken ct)
    {
        var parts = channel.ChannelPoint().Split(':');
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_timeout);
        using var closeCall = lnd.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint { FundingTxidStr = parts[0], OutputIndex = uint.Parse(parts[1]) },
            Force = true
        }, cancellationToken: closeTimeout.Token);
        PendingUpdate? pending = null;
        while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
            pending = closeCall.ResponseStream.Current.ClosePending;
        Assert.NotNull(pending);

        // LND's txid bytes are in internal order
        var txId = new uint256(pending.Txid.ToByteArray());
        Console.WriteLine($"{lnd.LocalAlias} force-closed {channel.ChannelPoint()} with {txId}");
        return txId;
    }

    /// <summary>Mines one block at a time until <paramref name="txId"/> (internal order) is confirmed.</summary>
    private async Task<NBitcoin.Transaction> MineUntilConfirmedAsync(Domain.Bitcoin.ValueObjects.TxId txId,
                                                            IEnumerable<LNDNodeConnection> lndNodes,
                                                            CancellationToken ct)
    {
        var displayTxId = new uint256((byte[])txId);
        var lnd = lndNodes.ToList();
        return await Poll.ForAsync(async () =>
        {
            try
            {
                var info = await _fixture.Bitcoin.GetRawTransactionInfoAsync(displayTxId, ct);
                if (info.Confirmations >= 1)
                    return info.Transaction;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Not broadcast yet
            }

            await ChainSync.MineAndWaitAsync(_fixture, 1, lnd, [Node], ct);
            return null;
        }, s_timeout, $"transaction {displayTxId} confirmed", ct);
    }

    private async Task<ChannelCloseModel> WaitForCloseAsync(ChannelId channelId, CancellationToken ct) =>
        await Poll.ForAsync(async () =>
        {
            using var scope = Node.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                              .GetCloseAsync(channelId);
        }, s_timeout, $"the funding spend of {channelId} recorded", ct);

    private async Task<OutputResolutionModel> WaitForOutputAsync(ChannelId channelId,
                                                                 Func<OutputResolutionModel, bool> predicate,
                                                                 string description, CancellationToken ct) =>
        await Poll.ForAsync(async () =>
        {
            using var scope = Node.Services.CreateScope();
            var outputs = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                                     .GetOutputsByChannelIdAsync(channelId);
            return outputs.FirstOrDefault(predicate);
        }, s_timeout, description, ct);

    private async Task WaitForOutputStateAsync(ChannelId channelId, uint vout, OutputResolutionState state,
                                               CancellationToken ct) =>
        await WaitForOutputAsync(channelId, o => o.OutputIndex == vout && o.State >= state,
                                 $"output {vout} {state}", ct);

    private async Task<OutputResolutionModel?> GetOutputAsync(ChannelId channelId, uint vout)
    {
        using var scope = Node.Services.CreateScope();
        var outputs = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                                 .GetOutputsByChannelIdAsync(channelId);
        return outputs.FirstOrDefault(o => o.OutputIndex == vout);
    }

    private IReadOnlyList<HtlcRecord> GetHtlcs(ChannelId channelId) =>
        Node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel) && channel.Commitments is { } c
            ? c.Htlcs.Values.ToList()
            : [];

    private Task<HtlcRecord> WaitForHtlcInBothCommitmentsAsync(ChannelId channelId, HtlcDirection direction,
                                                               CancellationToken ct) =>
        Poll.ForAsync(() => GetHtlcs(channelId).FirstOrDefault(h => h.Direction == direction
                                                                  && h.IsInCommit(CommitmentSide.Local)
                                                                  && h.IsInCommit(CommitmentSide.Remote)),
                      s_timeout, $"{direction} HTLC in both commitments of {channelId}", ct);

    /// <summary>Copies the SQLite database and its WAL files (taken under the channel lock, right after a save).</summary>
    private static void CopyDatabase(string databasePath, string directory)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var source = databasePath + suffix;
            if (File.Exists(source))
                File.Copy(source, Path.Combine(directory, Path.GetFileName(source)), true);
        }
    }

    /// <summary>Puts the copied database back (the node is stopped); a WAL file that was not copied is removed.</summary>
    private static void RestoreDatabase(string directory, string databasePath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var target = databasePath + suffix;
            var copy = Path.Combine(directory, Path.GetFileName(target));
            if (File.Exists(copy))
                File.Copy(copy, target, true);
            else if (File.Exists(target))
                File.Delete(target);
        }
    }

    private static async Task CancelHoldInvoiceQuietlyAsync(LNDNodeConnection node, byte[] paymentHash)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await LndTestHelpers.CancelInvoiceAsync(node, paymentHash, timeoutCts.Token);
        }
        catch (Exception e)
        {
            Console.WriteLine($"Could not cancel the hold invoice: {e.Message}");
        }
    }
}
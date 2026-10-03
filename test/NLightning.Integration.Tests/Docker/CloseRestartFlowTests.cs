using Microsoft.Extensions.DependencyInjection;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Daemon.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Constants;
using Fixtures;
using TestCollections;
using Utils;
using static CooperativeCloseFlowTests;
using static Interop.Cln.ClnSpliceReestablishTests;

/// <summary>
/// A cooperative close against LND interrupted by a restart of our node (NL-286, BOLT2 plan Proof N10 stretch). Our
/// node funds a channel to alice (with a push and a payment each way, as <see cref="CooperativeCloseFlowTests"/>), then:
/// <list type="bullet">
///   <item>ShuttingDown with an HTLC in flight: we pay alice's hold invoice and close while she holds it. Both
///   <c>shutdown</c>s are exchanged and the channel stays ShuttingDown: a new payment of ours is refused, as is
///   alice's to us. Our node restarts (a crash on the wire), re-sends <c>shutdown</c> after <c>channel_reestablish</c>
///   (B2-RE-28) and keeps the HTLC; alice settles, our payment succeeds and only then is the fee negotiated and the
///   close completed.</item>
///   <item>Closing: the close is agreed and broadcast; our node restarts while the closing transaction is in the
///   mempool (we re-send <c>shutdown</c>; LND 0.21 answers <c>channel_reestablish</c> without a <c>shutdown</c> of
///   its own, so no negotiation restarts, and neither side errors nor force-closes), then stops while it confirms 6
///   deep, and after the next start the channel is Closed.</item>
/// </list>
/// Each ends with LND listing a <c>COOPERATIVE_CLOSE</c> with our closing txid and alice's balance, our channel Closed
/// and our wallet credited. Our traffic is recorded by an unarmed <see cref="SpliceLinkCutter"/> (it records across
/// restarts).
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public sealed class CloseRestartFlowTests : IAsyncLifetime
{
    private const long HeldPaymentSat = 50_000;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(90);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly SpliceLinkCutter _wire = new();
    private NLightningTestNode? _node;
    private byte[]? _heldPaymentHash;

    public CloseRestartFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        Console.WriteLine($"[close] wire: {_wire.Describe()}");
        if (DockerDiagnostics.CurrentTestFailed)
            await _fixture.DumpLndLogsAsync(["alice"]);

        if (_heldPaymentHash is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await LndTestHelpers.CancelInvoiceAsync(_fixture.GetLndNode("alice"), _heldPaymentHash, cts.Token);
            }
            catch (Exception e)
            {
                // Settled already, or alice is gone
                Console.WriteLine($"[close] hold invoice not cancelled: {e.Message}");
            }
        }

        if (_node is not null)
            await _node.DisposeAsync();
    }

    [Fact]
    public async Task Given_AnHtlcInFlight_When_WeCloseAndRestartWhileShuttingDown_Then_TheCloseCompletesAfterItSettles()
    {
        // Arrange: a settled channel, then our payment to alice's hold invoice, held
        var ct = TestContext.Current.CancellationToken;
        var node = await StartNodeAsync("close-shut", ct);
        var alice = _fixture.GetLndNode("alice");
        var (channelId, channelPoint) = await OpenChannelAndWaitUntilActiveAsync(node, alice, ct);
        await MakePaymentsAsync(node, alice, channelId, channelPoint, ct);
        var walletBefore = WalletBalance(node);
        var (preimage, paymentHash) = LndTestHelpers.NewPreimage();
        var holdInvoice = await LndTestHelpers.AddHoldInvoiceAsync(alice, paymentHash, HeldPaymentSat * 1_000, [], ct,
                                                                   "nl-286 held across the close");
        _heldPaymentHash = paymentHash;
        var inFlight = await node.PayInvoiceAsync(holdInvoice.PaymentRequest, ct, timeoutSeconds: 2);
        Assert.Equal(PaymentStatus.InFlight, inFlight.Status);
        await LndTestHelpers.WaitForInvoiceStateAsync(alice, paymentHash, Invoice.Types.InvoiceState.Accepted,
                                                      s_timeout, ct);
        await WaitHtlcsAsync(node, alice, channelId, channelPoint, 1, ct);

        // Act 1: we close while the HTLC is held
        var closing = await CloseAsync(node, channelId, 0, ct);

        // Assert 1: shutdown both ways, the channel stays ShuttingDown (no closing_signed while an HTLC is pending)
        Assert.Equal(ChannelState.ShuttingDown, closing.State);
        await Poll.UntilAsync(() => node.ChannelMemoryRepository.TryGetChannel(channelId, out var c)
                                 && c.RemoteShutdownScript is not null,
                              s_timeout, "alice's shutdown", ct);
        Assert.NotNull(_wire.FirstOrDefault(false, MessageTypes.Shutdown));
        Assert.NotNull(_wire.FirstOrDefault(true, MessageTypes.Shutdown));

        // New HTLCs are refused both ways while shutting down (BOLT 2: no update_add_htlc after shutdown)
        var refused = await LndTestHelpers.AddInvoiceAsync(alice, 1_000_000, [], ct, "nl-286 after shutdown");
        var ourRefused = await node.PayInvoiceAsync(refused.PaymentRequest, ct, timeoutSeconds: 30);
        Console.WriteLine($"[close] our payment after shutdown: {ourRefused.Status} {ourRefused.FailureReason}");
        Assert.Equal(PaymentStatus.Failed, ourRefused.Status);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channelPoint, ct);
        var ourInvoice = await node.CreateInvoiceAsync(LightningMoney.Satoshis(1_000), "nl-286 alice after shutdown",
                                                       ct);
        var aliceRefused = await LndTestHelpers.SendPaymentV2Async(
                               alice, LndTestHelpers.PinnedPayment(ourInvoice.Bolt11!, [lndChannel!.ChanId],
                                                                   timeoutSeconds: 20), ct);
        Console.WriteLine($"[close] alice's payment after shutdown: {aliceRefused.Status} {aliceRefused.FailureReason}");
        Assert.Equal(Payment.Types.PaymentStatus.Failed, aliceRefused.Status);
        var during = await node.GetChannelAsync(channelId, ct);
        Assert.Equal(ChannelState.ShuttingDown, during.State);
        Assert.Equal(1, during.OfferedHtlcCount);
        Assert.Equal(0, during.ReceivedHtlcCount);
        Assert.Null(_wire.FirstOrDefault(false, MessageTypes.ClosingSigned));

        // Act 2: a restart (crash on the wire) while ShuttingDown with the HTLC held
        var from = _wire.CurrentSequence;
        await node.CrashAsync();
        await node.StartAsync(ct);
        await WaitReconnectedAsync(node, alice, ct);

        // Assert 2: channel_reestablish, then our shutdown again; still ShuttingDown with the HTLC
        await Poll.UntilAsync(() => _wire.FirstOrDefault(false, MessageTypes.Shutdown, from) is not null
                                 && _wire.FirstOrDefault(true, MessageTypes.ChannelReestablish, from) is not null,
                              s_timeout, "reestablish and our shutdown again", ct);
        Assert.NotNull(_wire.FirstOrDefault(false, MessageTypes.ChannelReestablish, from));
        Assert.True(_wire.FirstOrDefault(false, MessageTypes.ChannelReestablish, from)!.Sequence
                  < _wire.FirstOrDefault(false, MessageTypes.Shutdown, from)!.Sequence,
                    "shutdown went out before channel_reestablish");
        var afterRestart = await node.GetChannelAsync(channelId, ct);
        Assert.Equal(ChannelState.ShuttingDown, afterRestart.State);
        Assert.Equal(1, afterRestart.OfferedHtlcCount);

        // Act 3: alice settles the held HTLC
        await LndTestHelpers.SettleInvoiceAsync(alice, preimage, ct);
        _heldPaymentHash = null;

        // Assert 3: our payment succeeds, then the funder (us) proposes and the close completes
        await Poll.UntilAsync(async () => (await node.GetPaymentAsync(paymentHash, ct))?.Status
                                       == PaymentStatus.Succeeded,
                              s_timeout, "our held payment succeeded", ct);
        var closingTx = await Poll.ForAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channelId, ct);
            return ours.State == ChannelState.Closing ? GetClosingTransaction(node, channelId) : null;
        }, s_timeout, "our channel is Closing", ct);
        Assert.NotNull(_wire.FirstOrDefault(false, MessageTypes.ClosingSigned, from));
        AssertNoErrorOrWarning(from);
        await AssertClosedAsync(node, alice, channelId, channelPoint, closingTx, walletBefore, Initiator.Remote, ct,
                                AliceShareSat + HeldPaymentSat);
    }

    [Fact]
    public async Task Given_AnAgreedClose_When_WeRestartBeforeAndWhileItConfirms_Then_LndAndWeEndClosed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var node = await StartNodeAsync("close-closing", ct);
        var alice = _fixture.GetLndNode("alice");
        var (channelId, channelPoint) = await OpenChannelAndWaitUntilActiveAsync(node, alice, ct);
        await MakePaymentsAsync(node, alice, channelId, channelPoint, ct);
        var walletBefore = WalletBalance(node);
        var closed = await CloseAsync(node, channelId, (uint)s_timeout.TotalSeconds, ct);
        Assert.Equal(ChannelState.Closing, closed.State);
        var closingTx = GetClosingTransaction(node, channelId);
        Assert.NotNull(closingTx);
        await Poll.UntilAsync(async () =>
        {
            var pending = await alice.LightningClient.PendingChannelsAsync(new PendingChannelsRequest(),
                                                                           cancellationToken: ct);
            return pending.WaitingCloseChannels.Any(c => c.Channel.ChannelPoint == channelPoint);
        }, s_timeout, "alice waits for the closing transaction", ct);

        // Act 1: a restart with the closing transaction in the mempool
        var from = _wire.CurrentSequence;
        await node.CrashAsync();
        await node.StartAsync(ct);
        await WaitReconnectedAsync(node, alice, ct);

        // Assert 1: reestablish both ways and our shutdown again (B2-RE-28). LND 0.21 keeps a channel whose mutual
        // close it broadcast out of the negotiation: it re-sends channel_reestablish but no shutdown (seen in this
        // proof), so there is nothing for us to answer; had it restarted the negotiation, we, the funder, would
        // propose the agreed fee again (NL-725). Either way nobody errors and LND does not force-close
        await Poll.UntilAsync(() => _wire.FirstOrDefault(true, MessageTypes.ChannelReestablish, from) is not null
                                 && _wire.FirstOrDefault(false, MessageTypes.Shutdown, from) is not null,
                              s_timeout, "reestablish and our shutdown again", ct);
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        var lndShutdown = _wire.FirstOrDefault(true, MessageTypes.Shutdown, from);
        Console.WriteLine($"[close] LND after our restart in Closing: shutdown {(lndShutdown is null ? "not " : "")}"
                        + "re-sent");
        if (lndShutdown is not null)
            await Poll.UntilAsync(() => _wire.FirstOrDefault(false, MessageTypes.ClosingSigned, lndShutdown.Sequence)
                                        is not null,
                                  s_timeout, "our agreed closing_signed after LND's shutdown", ct);
        AssertNoErrorOrWarning(from);
        Assert.True(node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel));
        Assert.Equal(ChannelState.Closing, channel.State);
        Assert.Equal(closingTx.TxId, channel.ClosingTransaction!.TxId);
        var pendingAfter = await alice.LightningClient.PendingChannelsAsync(new PendingChannelsRequest(),
                                                                            cancellationToken: ct);
        Assert.Contains(pendingAfter.WaitingCloseChannels, c => c.Channel.ChannelPoint == channelPoint);
        Assert.DoesNotContain(pendingAfter.PendingForceClosingChannels, c => c.Channel.ChannelPoint == channelPoint);

        // Act 2: down while the closing transaction confirms 6 deep, then started again
        await node.StopAsync();
        await node.MineBlocksAsync(6, ct);
        await node.StartAsync(ct);

        // Assert 2
        await AssertClosedAsync(node, alice, channelId, channelPoint, closingTx, walletBefore, Initiator.Remote, ct,
                                mine: false);
    }

    private async Task<NLightningTestNode> StartNodeAsync(string name, CancellationToken ct)
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, name);
        _node.ConfigureServices = _wire.Install;
        await _node.StartAsync(ct);
        return _node;
    }

    private static async Task<CloseChannelClientResponse> CloseAsync(NLightningTestNode node, ChannelId channelId,
                                                                     uint waitSeconds, CancellationToken ct)
    {
        using var scope = node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<CloseChannelClientRequest,
                                CloseChannelClientResponse>>();
        return await handler.HandleAsync(new CloseChannelClientRequest(channelId) { WaitSeconds = waitSeconds }, ct);
    }

    /// <summary>Both sides list <paramref name="count"/> HTLCs on the channel (ours offered, alice's pending).</summary>
    private static async Task WaitHtlcsAsync(NLightningTestNode node, LndNodeConnection alice, ChannelId channelId,
                                             string channelPoint, int count, CancellationToken ct) =>
        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channelId, ct);
            var theirs = await LndTestHelpers.GetChannelByPointAsync(alice, channelPoint, ct);
            return ours.OfferedHtlcCount == count && theirs?.PendingHtlcs.Count == count;
        }, s_timeout, $"{count} HTLC(s) on both sides", ct);

    /// <summary>Our restarted node dials alice (a stored peer with a channel) and both list the connection.</summary>
    private static async Task WaitReconnectedAsync(NLightningTestNode node, LndNodeConnection alice,
                                                   CancellationToken ct) =>
        await Poll.UntilAsync(async () => node.IsConnectedTo(new CompactPubKey(alice.LocalNodePubKeyBytes))
                                       && await LndTestHelpers.IsConnectedToAsync(alice, node.NodeIdHex, ct),
                              s_timeout, "our node and alice connected again", ct);

    private void AssertNoErrorOrWarning(long from)
    {
        var bad = _wire.Snapshot()
                       .Where(m => m.Message.Sequence >= from
                                && m.Message.Type is (ushort)MessageTypes.Error or (ushort)MessageTypes.Warning)
                       .Select(m => $"{(m.Message.Inbound ? "received" : "sent")} {(MessageTypes)m.Message.Type}: "
                                  + System.Text.Encoding.UTF8.GetString(
                                        m.Message.Wire.AsSpan(Math.Min(2 + 32 + 2, m.Message.Wire.Length))))
                       .ToList();
        Assert.True(bad.Count == 0, $"error/warning after the restart: {string.Join("; ", bad)}");
    }
}
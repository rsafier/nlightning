using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Daemon.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Node.ValueObjects;
using Domain.Protocol.Constants;
using Fixtures;
using Utils;
using static ClnSpliceReestablishTests;

/// <summary>
/// A cooperative close interrupted by a lost link or a restart of our node, against Core Lightning (NL-286: the
/// restart while ShuttingDown/Negotiating/Closing that the legacy close was proven without). Our node funds a private
/// channel to CLN and closes it; <see cref="SpliceLinkCutter"/> drops one of CLN's close messages and cuts the link,
/// so the close is caught in a known state, then our node comes back (a reconnection or a restart on the same key and
/// database) and BOLT 2's retransmission rules finish the close:
/// <list type="bullet">
///   <item>ShuttingDown: CLN's <c>shutdown</c> reply lost. On reconnection both sides send <c>shutdown</c> again
///   (B2-RE-28) and we, the funder, open the negotiation.</item>
///   <item>Negotiating: CLN's <c>closing_signed</c> answer lost (CLN considers the close agreed and broadcasts it). On
///   reconnection the negotiation restarts from <c>shutdown</c> and ends on the same transaction.</item>
///   <item>Closing: the close agreed, our node restarts twice: once while the closing transaction is unconfirmed (we
///   re-send <c>shutdown</c> and the agreed <c>closing_signed</c>, NL-287, and CLN answers without an error), once
///   while it confirms (the chain monitor catches up and our channel becomes Closed).</item>
/// </list>
/// Each ends with one closing transaction on both sides, confirmed, our channel Closed and our wallet credited with our
/// output of it.
/// </summary>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnCloseRestartTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 8 * 60 * 1_000;
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_reconnectTimeout = TimeSpan.FromSeconds(90);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(500_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(100_000);

    private readonly ClnFixture _fixture;
    private ClnChannelSession? _session;
    private SpliceLinkCutter? _cutter;

    public ClnCloseRestartTests(ClnFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_cutter is not null)
            Console.WriteLine($"[close] wire: {_cutter.Describe()}");

        if (_session is not null)
        {
            if (DockerDiagnostics.CurrentTestFailed)
            {
                Console.WriteLine($"[close] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
                Console.WriteLine("[close] CLN close log:\n"
                                + await _fixture.Cln.GetLogLinesAsync("clos", CancellationToken.None, 80));
                await DockerDiagnostics.DumpContainerLogsAsync([ClnFixture.ClnContainerName], 300);
            }

            _cutter?.Heal();
            await _session.DisposeAsync();
        }
    }

    /// <summary>
    /// ShuttingDown: our <c>shutdown</c> reached CLN, its reply did not. After a reconnection (or a restart of our
    /// node) both sides re-send <c>shutdown</c>, we propose, and the close completes.
    /// </summary>
    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ClnShutdownLost_When_WeComeBack_Then_BothResendShutdownAndTheCloseCompletes(bool restart)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, cutter) = await BuildAsync($"nltg-cr-shut-{(restart ? "restart" : "reconnect")}", ct);
        var walletBefore = WalletBalance(session.Node);
        var cut = cutter.ArmInboundCut(MessageTypes.Shutdown, cutter.CurrentSequence);

        // Act 1: our shutdown goes out, CLN's reply is dropped and the link cut
        var closing = await CloseAsync(session, waitSeconds: 0, ct);
        Assert.Equal(ChannelState.ShuttingDown, closing.State);
        await cut.WaitAsync(s_closeTimeout, ct);

        // Assert 1: we sent shutdown, never saw CLN's, and stay ShuttingDown; CLN received ours and replied
        var channel = OurChannel(session);
        Assert.Equal(ChannelState.ShuttingDown, channel.State);
        Assert.NotNull(channel.LocalShutdownScript);
        Assert.Null(channel.RemoteShutdownScript);
        Assert.NotNull(cutter.FirstOrDefault(false, MessageTypes.Shutdown));
        // CLN starts closingd (and waits for our closing_signed) as soon as both shutdowns are out
        await Poll.UntilAsync(async () => (await session.GetClnChannelAsync(ct))["state"]?.GetValue<string>()
                                       is "CHANNELD_SHUTTING_DOWN" or "CLOSINGD_SIGEXCHANGE", s_closeTimeout,
                              "CLN shutting down", ct);

        // Act 2
        var from = cutter.CurrentSequence;
        await ComeBackAsync(session, cutter, restart, ct);

        // Assert 2: reestablish, shutdown both ways, then the negotiation we open as the funder
        var closingTx = await WaitClosingAsync(session, ct);
        AssertResent(cutter, from, MessageTypes.ChannelReestablish, MessageTypes.Shutdown, MessageTypes.ClosingSigned);
        AssertReceived(cutter, from, MessageTypes.ChannelReestablish, MessageTypes.Shutdown,
                       MessageTypes.ClosingSigned);
        AssertNoErrorOrWarning(cutter, from);
        await AssertClosedOnBothSidesAsync(session, closingTx, walletBefore, ct);
    }

    /// <summary>
    /// Negotiating: our <c>closing_signed</c> reached CLN, its answer did not (CLN signed our fee and broadcast the
    /// closing transaction). After a restart the negotiation starts again from <c>shutdown</c> and both sides end on
    /// the transaction CLN already broadcast.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ClnClosingSignedLost_When_WeRestart_Then_TheNegotiationRestartsOnTheSameTransaction()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, cutter) = await BuildAsync("nltg-cr-negotiating", ct);
        var walletBefore = WalletBalance(session.Node);
        var cut = cutter.ArmInboundCut(MessageTypes.ClosingSigned, cutter.CurrentSequence);

        // Act 1
        var closing = await CloseAsync(session, waitSeconds: 0, ct);
        Assert.Equal(ChannelState.ShuttingDown, closing.State);
        await cut.WaitAsync(s_closeTimeout, ct);

        // Assert 1: we proposed and are still negotiating; CLN answered (the dropped message) and moved on
        var channel = OurChannel(session);
        Assert.Equal(ChannelState.Negotiating, channel.State);
        Assert.Null(channel.ClosingTransaction);
        Assert.NotNull(cutter.FirstOrDefault(false, MessageTypes.ClosingSigned));
        var clnState = await Poll.ForAsync(async () =>
        {
            var state = (await session.GetClnChannelAsync(ct))["state"]?.GetValue<string>();
            return state is "CLOSINGD_SIGEXCHANGE" or "CLOSINGD_COMPLETE" ? state : null;
        }, s_closeTimeout, "CLN in closingd", ct);
        Console.WriteLine($"[close] CLN after its lost closing_signed: {clnState}");

        // Act 2
        var from = cutter.CurrentSequence;
        await ComeBackAsync(session, cutter, restart: true, ct);

        // Assert 2: shutdown again both ways, our closing_signed again, CLN's answer, Closing
        var closingTx = await WaitClosingAsync(session, ct);
        AssertResent(cutter, from, MessageTypes.ChannelReestablish, MessageTypes.Shutdown, MessageTypes.ClosingSigned);
        AssertReceived(cutter, from, MessageTypes.Shutdown, MessageTypes.ClosingSigned);
        AssertNoErrorOrWarning(cutter, from);
        await AssertClosedOnBothSidesAsync(session, closingTx, walletBefore, ct);
    }

    /// <summary>
    /// Closing: the close is agreed and broadcast. Our node restarts with the transaction unconfirmed (we re-send
    /// <c>shutdown</c> and the agreed <c>closing_signed</c>, CLN answers without an error and nothing changes), then
    /// stops, the transaction confirms 6 deep while it is down, and after the next start our channel is Closed.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AnAgreedClose_When_WeRestartBeforeAndWhileItConfirms_Then_ItEndsClosed()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var (session, cutter) = await BuildAsync("nltg-cr-closing", ct);
        var walletBefore = WalletBalance(session.Node);
        var closed = await CloseAsync(session, waitSeconds: (uint)s_closeTimeout.TotalSeconds, ct);
        Assert.Equal(ChannelState.Closing, closed.State);
        var closingTx = await WaitClosingAsync(session, ct);
        await WaitInMempoolAsync(closingTx.TxId, ct);
        // The fee of the stored transaction (our echo of CLN's fee may still be on its way: read it from the tx)
        var agreedFee = (ulong)(s_capacity.Satoshi - Transaction.Load(closingTx.RawTxBytes, Network.RegTest)
                                                                .Outputs.Sum(o => o.Value.Satoshi));

        // Act 1: a restart with the closing transaction in the mempool
        var from = cutter.CurrentSequence;
        await ComeBackAsync(session, cutter, restart: true, ct);

        // Assert 1: shutdown and the agreed closing_signed again; still Closing on the same transaction
        await Poll.UntilAsync(() => cutter.Snapshot().Any(m => m.Message.Sequence >= from && !m.Message.Inbound
                                                            && m.Message.Type == (ushort)MessageTypes.ClosingSigned),
                              s_closeTimeout, "our closing_signed re-sent", ct);
        AssertResent(cutter, from, MessageTypes.ChannelReestablish, MessageTypes.Shutdown, MessageTypes.ClosingSigned);
        Assert.All(cutter.Snapshot().Where(m => m.Message.Sequence >= from
                                             && m.Message.Type == (ushort)MessageTypes.ClosingSigned),
                   m => Assert.Equal(agreedFee, ClosingSignedFeeSat(m.Message)));
        // CLN's answer, if any, comes after a moment: give it the chance to complain
        await Task.Delay(TimeSpan.FromSeconds(3), ct);
        AssertNoErrorOrWarning(cutter, from);
        var channel = OurChannel(session);
        Assert.Equal(ChannelState.Closing, channel.State);
        Assert.Equal(closingTx.TxId, channel.ClosingTransaction!.TxId);
        Console.WriteLine($"[close] CLN after our restart in Closing: {await session.DescribeAsync(ct)}");

        // Act 2: down while the closing transaction confirms 6 deep, then started again
        await session.StopNodeAsync();
        await _fixture.MineAsync(6, ct);
        await _fixture.WaitAllAtTipAsync([], ct);
        await session.StartNodeAsync(ct);

        // Assert 2
        await AssertClosedOnBothSidesAsync(session, closingTx, walletBefore, ct, mine: false);
    }

    #region Helpers

    private async Task<(ClnChannelSession Session, SpliceLinkCutter Cutter)> BuildAsync(string nodeName,
                                                                                       CancellationToken ct)
    {
        var cutter = new SpliceLinkCutter();
        _cutter = cutter;
        _session = await ClnChannelSession.BuildOurFundedAsync(_fixture, nodeName, s_capacity, s_push, ct,
                                                               node => node.ConfigureServices = cutter.Install);
        return (_session, cutter);
    }

    private static async Task<CloseChannelClientResponse> CloseAsync(ClnChannelSession session, uint waitSeconds,
                                                                     CancellationToken ct)
    {
        using var scope = session.Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<CloseChannelClientRequest,
                                CloseChannelClientResponse>>();
        return await handler.HandleAsync(new CloseChannelClientRequest(session.ChannelId)
        {
            WaitSeconds = waitSeconds
        }, ct);
    }

    /// <summary>
    /// Brings our node back after a cut: a restart on the same key and database (the node dials CLN at start), or a
    /// reconnection of the running node. Either way the link is healed first.
    /// </summary>
    private async Task ComeBackAsync(ClnChannelSession session, SpliceLinkCutter cutter, bool restart,
                                            CancellationToken ct)
    {
        if (restart)
        {
            await session.StopNodeAsync();
            cutter.Heal();
            await session.StartNodeAsync(ct);
        }
        else
        {
            cutter.Heal();
        }

        await Poll.UntilAsync(async () =>
        {
            if (!session.Node.IsConnectedTo(session.ClnPubKey))
            {
                try
                {
                    await session.Node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.ClnAddress))
                                 .WaitAsync(TimeSpan.FromSeconds(10), ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Console.WriteLine($"[close] reconnect attempt failed: {e.Message}");
                    return false;
                }
            }

            return await session.Cln.IsConnectedAsync(session.Node.NodeIdHex, ct);
        }, s_reconnectTimeout, "our node and CLN connected again", ct, TimeSpan.FromSeconds(1));
        Console.WriteLine($"[close] back ({(restart ? "restart" : "reconnection")}): {await session.DescribeAsync(ct)}");
    }

    private static ChannelModel OurChannel(ClnChannelSession session)
    {
        var memory = session.Node.Services.GetRequiredService<IChannelMemoryRepository>();
        Assert.True(memory.TryGetChannel(session.ChannelId, out var channel), "our channel is not loaded");
        return channel;
    }

    private static async Task<Domain.Bitcoin.ValueObjects.SignedTransaction> WaitClosingAsync(
        ClnChannelSession session, CancellationToken ct) =>
        await Poll.ForAsync(() =>
        {
            var memory = session.Node.Services.GetRequiredService<IChannelMemoryRepository>();
            return Task.FromResult(memory.TryGetChannel(session.ChannelId, out var channel)
                                && channel.State == ChannelState.Closing
                                       ? channel.ClosingTransaction
                                       : null);
        }, s_closeTimeout, "our channel is Closing", ct);

    private async Task WaitInMempoolAsync(Domain.Bitcoin.ValueObjects.TxId txId, CancellationToken ct)
    {
        var hex = new uint256((byte[])txId).ToString();
        await Poll.UntilAsync(async () => (await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct))
                                 .Any(t => t.ToString() == hex),
                              s_closeTimeout, $"the closing transaction {hex} in bitcoind's mempool", ct);
    }

    /// <summary>Every <paramref name="types"/> was sent by our node at or after <paramref name="from"/>.</summary>
    private static void AssertResent(SpliceLinkCutter cutter, long from, params MessageTypes[] types)
    {
        foreach (var type in types)
            Assert.True(cutter.FirstOrDefault(false, type, from) is not null,
                        $"we sent no {type} after the cut: {cutter.Describe()}");
    }

    /// <summary>Every <paramref name="types"/> was received (and kept) by our node at or after <paramref name="from"/>.
    /// </summary>
    private static void AssertReceived(SpliceLinkCutter cutter, long from, params MessageTypes[] types)
    {
        foreach (var type in types)
            Assert.True(cutter.Snapshot().Any(m => m.Message.Sequence >= from && m.Message.Inbound && !m.Dropped
                                                && m.Message.Type == (ushort)type),
                        $"CLN sent no {type} after the cut: {cutter.Describe()}");
    }

    private static void AssertNoErrorOrWarning(SpliceLinkCutter cutter, long from)
    {
        var bad = cutter.Snapshot()
                        .Where(m => m.Message.Sequence >= from
                                 && m.Message.Type is (ushort)MessageTypes.Error or (ushort)MessageTypes.Warning)
                        .Select(m => $"{(m.Message.Inbound ? "received" : "sent")} {(MessageTypes)m.Message.Type} "
                                   + System.Text.Encoding.UTF8.GetString(m.Message.Wire.AsSpan(Math.Min(36, m.Message.Wire.Length))))
                        .ToList();
        Assert.True(bad.Count == 0, $"error/warning after the cut: {string.Join("; ", bad)}");
    }

    /// <summary><c>closing_signed</c>: type (2), channel_id (32), then the u64 <c>fee_satoshis</c>.</summary>
    private static ulong ClosingSignedFeeSat(ClnSpliceTests.SpliceWireMessage message) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(message.Wire.AsSpan(2 + 32, 8));

    /// <summary>
    /// The closing transaction is the one in bitcoind's mempool (CLN broadcast the same one); after 6 blocks (mined
    /// here unless <paramref name="mine"/> is false) CLN sees the funding spent by it, our channel is Closed and our
    /// wallet holds our output of it.
    /// </summary>
    private async Task AssertClosedOnBothSidesAsync(ClnChannelSession session,
                                                    Domain.Bitcoin.ValueObjects.SignedTransaction closingTx,
                                                    LightningMoney walletBefore, CancellationToken ct,
                                                    bool mine = true)
    {
        var channel = OurChannel(session);
        var tx = Transaction.Load(closingTx.RawTxBytes, Network.RegTest);
        var ourOutput = tx.Outputs.Where(o => o.ScriptPubKey.ToBytes()
                                               .SequenceEqual((byte[])channel.LocalShutdownScript!.Value))
                          .Sum(o => o.Value.Satoshi);
        var fee = (long)s_capacity.Satoshi - tx.Outputs.Sum(o => o.Value.Satoshi);
        Console.WriteLine($"[close] closing transaction {tx.GetHash()}: fee {fee} sat, ours {ourOutput} sat");
        Assert.InRange(fee, 1, 20_000);
        Assert.True(ourOutput > 0, "the closing transaction pays us nothing");

        if (mine)
        {
            await WaitInMempoolAsync(closingTx.TxId, ct);
            await _fixture.MineAndWaitAsync(6, [session.Node], ct);
        }
        else
        {
            await _fixture.WaitAllAtTipAsync([session.Node], ct);
        }

        // The funding output is spent by our closing transaction
        var funding = channel.FundingOutput!;
        var fundingTxId = new uint256((byte[])funding.TransactionId!.Value);
        var spent = await _fixture.Bitcoin.Rpc.GetTxOutAsync(fundingTxId, (int)funding.Index!.Value);
        Assert.Null(spent);
        var confirmed = await _fixture.Bitcoin.Rpc.GetRawTransactionInfoAsync(tx.GetHash());
        Assert.True(confirmed.Confirmations >= 6, $"the closing transaction has {confirmed.Confirmations} confirmations");

        await Poll.UntilAsync(async () =>
        {
            var ours = (await session.Node.ListChannelsAsync(ct)).Channels
                                                                   .FirstOrDefault(c => c.ChannelId
                                                                                     == session.ChannelId);
            return ours is null || ours.State == ChannelState.Closed;
        }, s_closeTimeout, "our channel is Closed", ct);
        var theirs = await Poll.ForAsync(async () =>
        {
            var clnChannel = await session.Cln.GetPeerChannelAsync(session.Node.NodeIdHex, session.ChannelIdHex,
                                                                   ct);
            var state = clnChannel?["state"]?.GetValue<string>();
            return state is "ONCHAIN" or "CLOSED" ? clnChannel : null;
        }, s_closeTimeout, "CLN sees the mutual close on chain", ct);
        Console.WriteLine($"[close] CLN after the close: {ClnChannelSession.DescribeCln(theirs)}");

        await Poll.UntilAsync(() => Task.FromResult(WalletBalance(session.Node) - walletBefore
                                                 == LightningMoney.Satoshis(ourOutput)),
                              s_closeTimeout, $"our wallet received our closing output of {ourOutput} sat", ct);
    }

    private static LightningMoney WalletBalance(NLightningTestNode node)
    {
        var utxos = node.Services.GetRequiredService<IUtxoMemoryRepository>();
        var height = node.BlockchainMonitor.LastProcessedBlockHeight;
        return utxos.GetConfirmedBalance(height) + utxos.GetUnconfirmedBalance(height);
    }

    #endregion
}
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Abcd;
using Daemon.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Messages;
using Fixtures;
using Utils;

/// <summary>
/// Cooperative close against Core Lightning (NL-286, the W3 review's fee_range interop evidence): the legacy
/// <c>shutdown</c>/<c>closing_signed</c> negotiation with CLN, which (unlike LND 0.20) sends and honours
/// <c>fee_range</c>, so our fee_range receive path (B2-CLS-R03..R06) runs against another implementation. Cases: we
/// close a channel we funded (with and without our <c>fee_range</c>), CLN closes a channel we funded (we propose as
/// the funder), and CLN closes a channel it funded (we answer its <c>fee_range</c> as the non-funder). Each ends with
/// the same closing transaction on both sides, confirmed, and our channel <c>Closed</c>.
/// </summary>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnCloseTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 6 * 60 * 1_000;
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(90);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(500_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(100_000);

    private readonly ClnFixture _fixture;
    private readonly ConcurrentQueue<ClosingSignedMessage> _ourClosingSigned = new();
    private ClnChannelSession? _session;

    public ClnCloseTests(ClnFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        Console.WriteLine("[cln] CLN unusual/broken log lines so far:\n"
                        + await _fixture.Cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 100, "unusual"));
        if (_session is not null)
        {
            if (DockerDiagnostics.CurrentTestFailed)
            {
                Console.WriteLine($"[cln] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");
                Console.WriteLine("[cln] CLN close log:\n"
                                + await _fixture.Cln.GetLogLinesAsync("closing", CancellationToken.None, 60));
                await DockerDiagnostics.DumpContainerLogsAsync([ClnFixture.ClnContainerName], 300);
            }

            await _session.DisposeAsync();
        }
    }

    /// <summary>
    /// We close a channel we funded (100k sat pushed to CLN): as the funder we open the negotiation, with our
    /// <c>fee_range</c> or without it (legacy "strictly between"), and CLN agrees.
    /// </summary>
    [Theory(Timeout = TestTimeoutMs)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_ChannelWeFunded_When_WeClose_Then_ClnAgreesAndBothClose(bool withFeeRange)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await OpenOurFundedAsync($"nltg-close-we-{(withFeeRange ? "range" : "legacy")}", ct);

        // Act
        CloseChannelClientResponse closed;
        using (var scope = session.Node.Services.CreateScope())
        {
            var handler = scope.ServiceProvider
                               .GetRequiredService<IClientCommandHandler<CloseChannelClientRequest,
                                    CloseChannelClientResponse>>();
            closed = await handler.HandleAsync(new CloseChannelClientRequest(session.ChannelId)
            {
                NoFeeRange = !withFeeRange,
                WaitSeconds = (uint)s_closeTimeout.TotalSeconds
            }, ct);
        }

        // Assert
        Assert.Equal(ChannelState.Closing, closed.State);
        Assert.NotNull(closed.ClosingTxId);
        Assert.NotEmpty(_ourClosingSigned);
        Assert.All(_ourClosingSigned, m => Assert.Equal(withFeeRange, m.FeeRangeTlv is not null));
        await AssertClosedOnBothSidesAsync(session, closed.ClosingTxId.Value, ct);
    }

    /// <summary>
    /// CLN closes a channel we funded: it sends <c>shutdown</c>, we reply and, as the funder, propose the fee with our
    /// <c>fee_range</c>; CLN answers inside it.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ChannelWeFunded_When_ClnCloses_Then_WeProposeAndBothClose()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await OpenOurFundedAsync("nltg-close-cln", ct);

        // Act: CLN's close returns once the mutual close is signed and broadcast
        var result = await session.Cln.CallAsync("close", ct, ("id", session.ChannelIdHex));
        Console.WriteLine($"[cln] close: {result.ToJsonString()}");

        // Assert
        Assert.Equal("mutual", result["type"]!.GetValue<string>());
        Assert.Contains(_ourClosingSigned, m => m.FeeRangeTlv is not null);
        var clnTxId = result["txids"]![0]!.GetValue<string>();
        var ours = await WaitClosingTxAsync(session, ct);
        Assert.Equal(clnTxId, DisplayTxId(ours));
        await AssertClosedOnBothSidesAsync(session, ours, ct);
    }

    /// <summary>
    /// CLN closes a channel it funded (after paying us, so both sides have an output): CLN, as the funder, sends
    /// <c>closing_signed</c> with its <c>fee_range</c>, and we answer as the non-funder (B2-CLS-R03/R06) until both
    /// sign the same transaction.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ChannelClnFunded_When_ClnCloses_Then_WeAnswerItsFeeRangeAndBothClose()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _session = await ClnChannelSession.BuildClnFundedAsync(_fixture, "nltg-close-fundee", s_capacity, "opening",
                                                               ct);
        AttachRecorder(_session);
        await ClnPaysUsAsync(_session, LightningMoney.Satoshis(40_000), ct);

        // Act
        var result = await _session.Cln.CallAsync("close", ct, ("id", _session.ChannelIdHex));
        Console.WriteLine($"[cln] close: {result.ToJsonString()}");

        // Assert: CLN's closing_signed carried a fee_range (logged by our coordinator), we answered, same tx
        Assert.Equal("mutual", result["type"]!.GetValue<string>());
        Assert.True(_session.Node.CountLogLines("sat (range [") > 0, "we saw no fee_range from CLN");
        Assert.NotEmpty(_ourClosingSigned);
        var ours = await WaitClosingTxAsync(_session, ct);
        Assert.Equal(result["txids"]![0]!.GetValue<string>(), DisplayTxId(ours));
        await AssertClosedOnBothSidesAsync(_session, ours, ct);
    }

    private async Task<ClnChannelSession> OpenOurFundedAsync(string nodeName, CancellationToken ct)
    {
        _session = await ClnChannelSession.BuildOurFundedAsync(_fixture, nodeName, s_capacity, s_push, ct);
        AttachRecorder(_session);
        return _session;
    }

    private void AttachRecorder(ClnChannelSession session)
    {
        session.Node.ChannelManager.OnResponseMessageReady += (_, args) =>
        {
            if (args.ResponseMessage is ClosingSignedMessage closingSigned
             && closingSigned.Payload.ChannelId == session.ChannelId)
                _ourClosingSigned.Enqueue(closingSigned);
        };
    }

    private static async Task<TxId> WaitClosingTxAsync(ClnChannelSession session, CancellationToken ct)
    {
        var closingTx = await Poll.ForAsync(() =>
        {
            var memory = session.Node.Services.GetRequiredService<IChannelMemoryRepository>();
            return Task.FromResult(memory.TryGetChannel(session.ChannelId, out var channel)
                                && channel.State == ChannelState.Closing
                                       ? channel.ClosingTransaction
                                       : null);
        }, s_closeTimeout, "our channel is Closing", ct);
        return closingTx.TxId;
    }

    /// <summary>
    /// Mines the closing transaction 6 deep: CLN leaves CHANNELD and sees the funding spent by it (ONCHAIN), and our
    /// channel becomes Closed.
    /// </summary>
    private async Task AssertClosedOnBothSidesAsync(ClnChannelSession session, TxId closingTxId,
                                                    CancellationToken ct)
    {
        var closingHex = DisplayTxId(closingTxId);
        await Poll.UntilAsync(async () =>
        {
            var mempool = await _fixture.Bitcoin.Rpc.GetRawMempoolAsync(ct);
            return mempool.Any(t => t.ToString() == closingHex);
        }, s_closeTimeout, "the closing transaction in bitcoind's mempool", ct);

        await _fixture.MineAndWaitAsync(6, [session.Node], ct);

        await Poll.UntilAsync(async () =>
        {
            var ours = (await session.Node.ListChannelsAsync(ct)).Channels
                                                                   .FirstOrDefault(c => c.ChannelId
                                                                                     == session.ChannelId);
            return ours is null || ours.State == ChannelState.Closed;
        }, s_closeTimeout, "our channel is Closed", ct);
        var theirs = await Poll.ForAsync(async () =>
        {
            var channel = await session.Cln.GetPeerChannelAsync(session.Node.NodeIdHex, session.ChannelIdHex, ct);
            var state = channel?["state"]?.GetValue<string>();
            return state is "ONCHAIN" or "CLOSED" ? channel : null;
        }, s_closeTimeout, "CLN sees the mutual close on chain", ct);
        Console.WriteLine($"[cln] after the close: {ClnChannelSession.DescribeCln(theirs)}; closing tx {closingHex}");
    }

    private static async Task ClnPaysUsAsync(ClnChannelSession session, LightningMoney amount, CancellationToken ct)
    {
        var invoice = await session.Node.CreateInvoiceAsync(amount, $"cln pays before close {Guid.NewGuid():N}", ct);
        var result = await session.Cln.CallAsync("pay", ct, ("bolt11", invoice.Bolt11), ("retry_for", 30));
        Assert.Equal("complete", result["status"]!.GetValue<string>());
        var preimage = Convert.FromHexString(result["payment_preimage"]!.GetValue<string>());
        Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(preimage));
        await Poll.UntilAsync(async () =>
        {
            var ours = await session.Node.GetInvoiceAsync(invoice.PaymentHash, ct);
            return ours?.Status == InvoiceStatus.Settled;
        }, s_closeTimeout, "our invoice settled", ct);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
    }

    /// <summary>A txid as bitcoind and CLN print it (reversed bytes).</summary>
    private static string DisplayTxId(TxId txId) =>
        Convert.ToHexString(((byte[])txId).Reverse().ToArray()).ToLowerInvariant();
}
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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

    // Our coordinator's line for each closing_signed CLN sends (ChannelCloseCoordinator.ReceiveClosingSignedAsync)
    private static readonly Regex s_peerClosingSigned =
        new(@"closing_signed for channel (?<id>[0-9a-f]{64}): (?<fee>\d+) sat \(range (?:\[(?<min>\d+), (?<max>\d+)\] sat|none)\) -> (?<decision>\w+) (?<decisionFee>\d+) sat \[(?<requirement>[^\]]*)\]",
            RegexOptions.Compiled);

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
        var theirs = Assert.Single(PeerClosingSigned(session));
        Assert.NotNull(theirs.Range); // CLN sends fee_range whether or not we did
        if (withFeeRange)
        {
            // B2-CLS-R03: CLN answers inside the range we sent, and we close at its fee
            var ourRange = _ourClosingSigned.First().FeeRangeTlv!;
            Assert.InRange(theirs.FeeSat, (ulong)ourRange.MinFeeAmount.Satoshi, (ulong)ourRange.MaxFeeAmount.Satoshi);
            Assert.Equal("B2-CLS-R03", theirs.RequirementId);
        }
        else
        {
            // B2-CLS-R05: we sent no range, CLN did; as the funder we take its fee, which lies in its range
            Assert.InRange(theirs.FeeSat, theirs.Range.Value.Min, theirs.Range.Value.Max);
            Assert.Equal("B2-CLS-R05", theirs.RequirementId);
        }

        await AssertAgreedAndEchoedAsync(theirs, ct);
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
        var theirs = Assert.Single(PeerClosingSigned(session));
        var ourRange = _ourClosingSigned.First().FeeRangeTlv!;
        Assert.InRange(theirs.FeeSat, (ulong)ourRange.MinFeeAmount.Satoshi, (ulong)ourRange.MaxFeeAmount.Satoshi);
        Assert.Equal("B2-CLS-R03", theirs.RequirementId);
        await AssertAgreedAndEchoedAsync(theirs, ct);
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
        // B2-CLS-R06: CLN, as the funder, sends [fee, fee]; we take its fee and send it back
        var theirs = Assert.Single(PeerClosingSigned(_session));
        Assert.NotNull(theirs.Range);
        Assert.Equal(theirs.FeeSat, theirs.Range.Value.Min);
        Assert.Equal(theirs.FeeSat, theirs.Range.Value.Max);
        Assert.Equal("B2-CLS-R06", theirs.RequirementId);
        await AssertAgreedAndEchoedAsync(theirs, ct);
        var ours = await WaitClosingTxAsync(_session, ct);
        Assert.Equal(result["txids"]![0]!.GetValue<string>(), DisplayTxId(ours));
        await AssertClosedOnBothSidesAsync(_session, ours, ct);
    }

    /// <summary>What CLN sent for the session's channel, from our coordinator's log lines, in order.</summary>
    private static List<ClnClosingSigned> PeerClosingSigned(ClnChannelSession session)
    {
        var channelIdHex = session.ChannelId.ToString();
        return session.Node.NodeLog
                      .Select(line => s_peerClosingSigned.Match(line))
                      .Where(m => m.Success && m.Groups["id"].Value == channelIdHex)
                      .Select(m => new ClnClosingSigned(
                                  ulong.Parse(m.Groups["fee"].Value),
                                  m.Groups["min"].Success
                                      ? (ulong.Parse(m.Groups["min"].Value), ulong.Parse(m.Groups["max"].Value))
                                      : null,
                                  m.Groups["decision"].Value, ulong.Parse(m.Groups["decisionFee"].Value),
                                  m.Groups["requirement"].Value))
                      .ToList();
    }

    /// <summary>
    /// We agreed on CLN's fee and our last <c>closing_signed</c> carried it back (the echo is raised after the closing
    /// transaction is stored, so it may follow the close's return).
    /// </summary>
    private async Task AssertAgreedAndEchoedAsync(ClnClosingSigned theirs, CancellationToken ct)
    {
        Assert.Equal("Agree", theirs.Decision);
        Assert.Equal(theirs.FeeSat, theirs.DecisionFeeSat);
        await Poll.UntilAsync(() => Task.FromResult(_ourClosingSigned.LastOrDefault() is { } last
                                                 && (ulong)last.Payload.FeeAmount.Satoshi == theirs.FeeSat),
                              s_closeTimeout, $"our closing_signed echoing CLN's {theirs.FeeSat} sat", ct);
    }

    private sealed record ClnClosingSigned(ulong FeeSat, (ulong Min, ulong Max)? Range, string Decision,
                                           ulong DecisionFeeSat, string RequirementId);

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
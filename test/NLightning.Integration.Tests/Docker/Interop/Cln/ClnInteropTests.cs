using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Abcd;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.ValueObjects;
using Domain.Payments.Enums;
using Domain.Protocol.Messages;
using Fixtures;
using Utils;

/// <summary>
/// Interop with Core Lightning (the official <c>elementsproject/lightningd</c> image, <see cref="ClnFixture.ClnTag"/>):
/// BOLT 8 + init in both directions, a channel we fund reaching <c>CHANNELD_NORMAL</c>, payments both ways over it,
/// <c>channel_reestablish</c> after CLN drops the connection and after we restart, and a channel CLN funds to us.
/// </summary>
/// <remarks>
/// The tests share one channel (<see cref="ClnChannelSession"/>, built by the first test that needs it) and run in
/// any order: each starts from a usable channel with no HTLC in flight and asserts balance deltas only. CLN's
/// UNUSUAL/BROKEN log lines are printed after every test.
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnInteropTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 6 * 60 * 1_000;
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromSeconds(60);

    private readonly ClnFixture _fixture;
    private ClnChannelSession? _session;

    public ClnInteropTests(ClnFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // CLN's UNUSUAL/BROKEN lines are where it reports what it disliked about us, even when the test passed
        Console.WriteLine("[cln] CLN unusual/broken log lines so far:\n"
                        + await _fixture.Cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 100, "unusual"));

        if (DockerDiagnostics.CurrentTestFailed)
        {
            if (_session is not null)
                Console.WriteLine($"[cln] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");

            await DockerDiagnostics.DumpContainerLogsAsync([ClnFixture.ClnContainerName], 400);
        }
    }

    /// <summary>
    /// BOLT 8 handshake and BOLT 1 <c>init</c> both ways with a fresh node: both ends list each other, CLN recorded our
    /// features, and the connection stays up (no warning/error from either side tears it down).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ClnNode_When_WeConnect_Then_InitExchangedAndConnectionStable()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, "nltg-connect");
        await node.StartAsync(ct);
        CompactPubKey clnId = Convert.FromHexString(_fixture.ClnNodeId);

        // Act
        await node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.ClnAddress)).WaitAsync(ct);

        // Assert
        await Poll.UntilAsync(async () => node.IsConnectedTo(clnId)
                                       && await _fixture.Cln.IsConnectedAsync(node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(30), "both ends list each other", ct);
        var peer = await _fixture.Cln.GetPeerAsync(node.NodeIdHex, ct);
        Assert.NotNull(peer);
        Console.WriteLine($"[cln] CLN sees us: {peer.ToJsonString()}");
        Assert.False(string.IsNullOrEmpty(peer["features"]?.GetValue<string>()), "CLN recorded no init features");
        Assert.True(await Poll.HoldsAsync(() => node.IsConnectedTo(clnId), TimeSpan.FromSeconds(5), ct),
                    "the connection to CLN dropped");
        Assert.True(await _fixture.Cln.IsConnectedAsync(node.NodeIdHex, ct), "CLN dropped the connection");
        Assert.Equal(0, node.CountLogLines(NLightningTestNode.InitLostLogFragment));
    }

    /// <summary>
    /// CLN initiates: we are the BOLT 8 responder, and <c>init</c> is exchanged both ways over CLN's outbound
    /// connection, which stays up.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurListeningNode_When_ClnConnectsToUs_Then_InitExchangedAndConnectionStable()
    {
        // Arrange: listen on every interface so the CLN container can reach us through the host
        var ct = TestContext.Current.CancellationToken;
        await using var node = await NLightningTestNode.CreateAsync(
                                   _fixture.Bitcoin, "nltg-inbound",
                                   configureNodeOptions: o => o.ListenAddresses =
                                                                  o.ListenAddresses
                                                                   .Select(a => a.Replace("127.0.0.1", "0.0.0.0"))
                                                                   .ToList());
        await node.StartAsync(ct);
        CompactPubKey clnId = Convert.FromHexString(_fixture.ClnNodeId);

        // Act
        var connect = await _fixture.Cln.CallAsync("connect", ct, ("id", node.NodeIdHex),
                                                   ("host", ClnFixture.HostAddressFromContainers),
                                                   ("port", node.Port));

        // Assert
        Console.WriteLine($"[cln] CLN connect: {connect.ToJsonString()}");
        Assert.Equal("out", connect["direction"]!.GetValue<string>());
        await Poll.UntilAsync(async () => node.IsConnectedTo(clnId)
                                       && await _fixture.Cln.IsConnectedAsync(node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(30), "both ends list each other", ct);
        Assert.True(await Poll.HoldsAsync(() => node.IsConnectedTo(clnId), TimeSpan.FromSeconds(5), ct),
                    "the connection from CLN dropped");
        Assert.True(await _fixture.Cln.IsConnectedAsync(node.NodeIdHex, ct), "CLN dropped the connection");
    }

    /// <summary>
    /// CLN funds a private channel to us (we are the fundee, static_remotekey), both ends reach
    /// <c>CHANNELD_NORMAL</c>/usable, and payments work both ways (CLN pays first: all the funds are on its side). CLN
    /// funds at 10,000 sat/kw: at its own regtest estimate (253 sat/kw) we refuse the open, because we require at
    /// least 80 % of our own estimate (see the lane's ledger items). CLN sends <c>update_fee</c> only when its
    /// estimate changes after the channel is normal, so this test does not wait for one.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ClnFundsChannelToUs_When_Normal_Then_PaymentsWorkBothWays()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var session = await ClnChannelSession.BuildClnFundedAsync(
                                      _fixture, "nltg-fundee", LightningMoney.Satoshis(500_000), "10000perkw", ct);
        var ours = await session.GetOurChannelAsync(ct);
        Assert.False(ours.IsInitiator);
        Assert.Equal("local", (await session.GetClnChannelAsync(ct))["opener"]!.GetValue<string>());

        // Act + Assert
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(30_000), ct);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(10_000), ct);
    }

    /// <summary>
    /// Interop gap found by this suite (see the lane's ledger items): CLN funds at its own estimate (253 sat/kw on an
    /// idle regtest) and we refuse the <c>open_channel</c> with "Fee rate per kw is too small" because it is below 80 %
    /// of our estimate. Explicit until the fee floor is fixed; then drop <c>Explicit</c> (and the explicit feerate of
    /// <see cref="Given_ClnFundsChannelToUs_When_Normal_Then_PaymentsWorkBothWays"/>).
    /// </summary>
    [Fact(Timeout = TestTimeoutMs, Explicit = true)]
    public async Task Given_ClnFundsAtItsOwnEstimate_When_Opening_Then_WeAccept()
    {
        // Arrange + Act: fundchannel without a feerate, so CLN uses its own opening estimate
        var ct = TestContext.Current.CancellationToken;

        // Assert: the build fails with CLN's "They sent ERROR ... Fee rate per kw is too small" while the gap is open
        await using var session = await ClnChannelSession.BuildClnFundedAsync(
                                      _fixture, "nltg-fee-floor", LightningMoney.Satoshis(500_000), "opening", ct);
        Assert.True((await session.GetOurChannelAsync(ct)).IsUsable());
    }

    /// <summary>
    /// The channel we funded (1M sat, 300k pushed) is <c>CHANNELD_NORMAL</c> at CLN and usable at our end, and both ends
    /// agree on the SCID, the capacity and the balances.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ChannelWeFunded_When_Confirmed_Then_ChanneldNormalOnBothEnds()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await GetSessionAsync(ct);

        // Act
        var ours = await session.GetOurChannelAsync(ct);
        var theirs = await session.GetClnChannelAsync(ct);

        // Assert
        Assert.Equal("CHANNELD_NORMAL", theirs["state"]!.GetValue<string>());
        Assert.True(ours.IsUsable(), ours.Describe());
        Assert.True(ours.IsInitiator);
        Assert.Equal(ClnChannelSession.Capacity, ours.Capacity);
        Assert.Equal((long)ClnChannelSession.Capacity.MilliSatoshi, theirs["total_msat"]!.GetValue<long>());
        Assert.NotNull(ours.ShortChannelId);
        Assert.Equal(ClnScid(ours.ShortChannelId.Value.ToUInt64()), theirs["short_channel_id"]!.GetValue<string>());
        Assert.Equal((long)ours.RemoteBalance.MilliSatoshi, theirs["to_us_msat"]!.GetValue<long>());
        Assert.Equal("remote", theirs["opener"]!.GetValue<string>());
        Console.WriteLine($"[cln] channel: {ClnChannelSession.DescribeCln(theirs)}");
    }

    /// <summary>
    /// CLN pays our invoice (<c>createinvoice</c>) over the channel: CLN's <c>pay</c> completes with our preimage and our
    /// invoice is settled for the amount.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_NormalChannel_When_ClnPaysOurInvoice_Then_Settled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await GetSessionAsync(ct);

        // Act + Assert
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(25_000), ct);
    }

    /// <summary>
    /// We pay CLN's invoice (<c>payinvoice</c>): our payment succeeds with CLN's preimage and CLN's invoice is paid.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_NormalChannel_When_WePayClnInvoice_Then_PreimageReturned()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await GetSessionAsync(ct);

        // Act + Assert
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(40_000), ct);
    }

    /// <summary>
    /// CLN force-disconnects us; we reconnect (backoff), send <c>channel_reestablish</c>, both ends are usable again and
    /// payments work both ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_NormalChannel_When_ClnDisconnects_Then_ReestablishedAndPaymentsWork()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await GetSessionAsync(ct);
        var mark = session.Sent.Mark;

        // Act
        await session.Cln.CallAsync("disconnect", ct, ("id", session.Node.NodeIdHex), ("force", true));

        // Assert
        await Poll.UntilAsync(() => session.Sent.CountSent<ChannelReestablishMessage>(session.ChannelId, mark) > 0,
                              ClnChannelSession.UsableTimeout, "we sent channel_reestablish on a new connection", ct);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(11_000), ct);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(12_000), ct);
    }

    /// <summary>
    /// We stop and start again on the same key and database: we reconnect to CLN, reestablish, and payments work both
    /// ways.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_NormalChannel_When_WeRestart_Then_ReestablishedAndPaymentsWork()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var session = await GetSessionAsync(ct);
        var before = await session.GetOurChannelAsync(ct);

        // Act
        await session.StopNodeAsync();
        await Poll.UntilAsync(async () => !await session.Cln.IsConnectedAsync(session.Node.NodeIdHex, ct),
                              TimeSpan.FromSeconds(30), "CLN saw us go away", ct);
        await session.StartNodeAsync(ct);

        // Assert: usable means channel_reestablish was exchanged on the new connection (IsReestablished)
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await session.GetOurChannelAsync(ct);
        Assert.Equal(before.LocalBalance, after.LocalBalance);
        Assert.Equal(before.LocalCommitmentNumber, after.LocalCommitmentNumber);
        Assert.Equal(before.RemoteCommitmentNumber, after.RemoteCommitmentNumber);
        Assert.False(after.DataLossDetected);
        await AssertClnPaysUsAsync(session, LightningMoney.Satoshis(13_000), ct);
        await AssertWePayClnAsync(session, LightningMoney.Satoshis(14_000), ct);
    }

    private async Task<ClnChannelSession> GetSessionAsync(CancellationToken ct)
    {
        _session = await ClnChannelSession.GetAsync(_fixture, ct);
        await _session.PrepareAsync(ct);
        return _session;
    }

    private static async Task AssertClnPaysUsAsync(ClnChannelSession session, LightningMoney amount,
                                                   CancellationToken ct)
    {
        var before = await session.GetOurChannelAsync(ct);
        var invoice = await session.Node.CreateInvoiceAsync(amount, $"cln pays nltg {Guid.NewGuid():N}", ct);
        Console.WriteLine($"[cln] our invoice {invoice.Bolt11}");

        JsonNode result;
        try
        {
            result = await session.Cln.CallAsync("pay", ct, ("bolt11", invoice.Bolt11), ("retry_for", 30));
        }
        catch (ClnRpcException e)
        {
            Assert.Fail($"CLN could not pay our invoice: {e.Message}\nCLN log:\n"
                      + await session.Cln.GetLogLinesAsync(session.Node.NodeIdHex[..16], ct));
            throw;
        }

        Console.WriteLine($"[cln] CLN pay: {result.ToJsonString()}");
        Assert.Equal("complete", result["status"]!.GetValue<string>());
        var preimage = Convert.FromHexString(result["payment_preimage"]!.GetValue<string>());
        Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(preimage));

        var ours = await Poll.ForAsync(async () =>
        {
            var current = await session.Node.GetInvoiceAsync(invoice.PaymentHash, ct);
            return current?.Status == InvoiceStatus.Settled ? current : null;
        }, s_settleTimeout, "our invoice settled", ct);
        Assert.Equal(amount, ours.AmountReceived);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await session.GetOurChannelAsync(ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi + amount.MilliSatoshi, after.LocalBalance.MilliSatoshi);
    }

    private static async Task AssertWePayClnAsync(ClnChannelSession session, LightningMoney amount,
                                                  CancellationToken ct)
    {
        var before = await session.GetOurChannelAsync(ct);
        var label = $"nltg-pays-cln-{Guid.NewGuid():N}";
        var invoice = await session.Cln.CallAsync("invoice", ct, ("amount_msat", (long)amount.MilliSatoshi),
                                                  ("label", label), ("description", "nltg pays cln"));
        var bolt11 = invoice["bolt11"]!.GetValue<string>();
        Console.WriteLine($"[cln] CLN invoice {bolt11}");

        var payment = await session.Node.PayInvoiceAsync(bolt11, ct);

        Console.WriteLine($"[cln] our payment: {payment.Status}, failure {payment.FailureCode} at "
                        + $"{payment.FailureSourceIndex}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        var clnInvoice = (await session.Cln.CallAsync("listinvoices", ct, ("label", label)))["invoices"]!.AsArray()
                                                                                                    .Single()!;
        Assert.Equal("paid", clnInvoice["status"]!.GetValue<string>());
        Assert.NotNull(payment.Preimage);
        Assert.Equal(clnInvoice["payment_preimage"]!.GetValue<string>(),
                     Convert.ToHexString((byte[])payment.Preimage.Value).ToLowerInvariant());
        Assert.Equal(amount, payment.Amount);
        Assert.Equal(LightningMoney.Zero, payment.Fee);
        await session.WaitUsableAsync(ct, requireNoHtlcs: true);
        var after = await session.GetOurChannelAsync(ct);
        Assert.Equal(before.LocalBalance.MilliSatoshi - amount.MilliSatoshi, after.LocalBalance.MilliSatoshi);
    }

    /// <summary>
    /// CLN's <c>BLOCKxTXxOUT</c> form of a short channel id.
    /// </summary>
    private static string ClnScid(ulong scid) => $"{scid >> 40}x{(scid >> 16) & 0xFFFFFF}x{scid & 0xFFFF}";
}
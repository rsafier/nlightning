using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;

namespace NLightning.Integration.Tests.Docker.Taproot;

using Abcd;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Channels.Enums;
using Domain.Channels.Reestablish;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Utils;

/// <summary>
/// Taproot plan T6 (wave t02, lane LND): simple taproot channels (<c>option_simple_taproot</c>, bits 80/81) against LND
/// 0.21.4 run with <c>--protocol.simple-taproot-chans</c> (<see cref="LndTaprootNetworkFixture"/>). Each direction of
/// the v1 open is one flow: LND opens a private taproot channel to us (<c>CommitmentType.SIMPLE_TAPROOT_FINAL</c>), and
/// we open one to LND (<c>openchannel --channel-type taproot</c>, v1 since LND has no dual funding); each carries
/// payments both ways and ends with a cooperative close (LND's RBF close, <c>closing_complete</c>/<c>closing_sig</c>
/// with MuSig2 partial signatures, the flag forces it) whose key-path spend confirms. The LND-funded flow also restarts
/// our node with an HTLC in flight (LND holds it), then LND, and pays again after each <c>channel_reestablish</c>
/// (type-22 nonce maps).
/// </summary>
[Collection(LndTaprootRegtestCollection.Name)]
public class LndTaprootFlowTests : IAsyncLifetime
{
    private const long CapacitySat = 1_000_000;
    private const long LndPushSat = 300_000;
    private const long OurPushSat = 200_000;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan s_mineInterval = TimeSpan.FromSeconds(1);

    private readonly LndTaprootNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public LndTaprootFlowTests(LndTaprootNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    private LndNodeConnection Tara => _fixture.GetLndNode(LndTaprootNetworkFixture.Alias);

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "taproot", configureNodeOptions: o =>
        {
            o.Features.AllowExperimentalFeatures = true;
            o.Features.OptionSimpleTaproot = FeatureSupport.Optional;
        });
        await Node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_LndOpensAPrivateTaprootChannel_When_PaymentsRestartsAndLndCloses_Then_AllWorkAndTheCloseConfirms()
    {
        // Arrange: our anchors reserve (a taproot channel has anchors, NL-379), then connect
        var ct = TestContext.Current.CancellationToken;
        var tara = Tara;
        await Node.FundWalletAsync(LightningMoney.Satoshis(200_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [tara], [Node], ct);
        await Node.ConnectToAsync(tara, ct);
        await LogFeaturesAsync(tara, ct);

        // Act 1: LND opens a private SIMPLE_TAPROOT_FINAL channel to us
        var channelPoint = await LndTestHelpers.OpenPrivateTaprootChannelAsync(tara, (byte[])Node.NodeId, CapacitySat,
                                                                               LndPushSat, 5, ct);
        Console.WriteLine($"tara opened {channelPoint} to us");
        var ourEnd = await Poll.ForAsync(async () =>
        {
            var channels = await Node.ListChannelsAsync(ct);
            return channels.Channels.Count == 1 ? channels.Channels[0] : null;
        }, s_timeout, "our end of tara's channel", ct);
        var channelId = ourEnd.ChannelId;
        await MineUntilUsableAsync(tara, channelId, channelPoint, ct);

        // Assert 1: a private taproot channel on both sides, LND the funder, the funding output P2TR
        var lndChannel = await AssertTaprootChannelAsync(tara, channelId, channelPoint, lndIsInitiator: true, ct);
        await AssertBalancesAgreeAsync(tara, channelId, channelPoint, weAreFunder: false, ct);

        // Act 2: payments both ways
        await LndPaysUsAsync(tara, channelId, lndChannel.ChanId, LightningMoney.Satoshis(50_000), ct);
        await WePayLndAsync(tara, LightningMoney.Satoshis(20_000), ct);
        await AssertBalancesAgreeAsync(tara, channelId, channelPoint, weAreFunder: false, ct);

        // Act 3: our HTLC held by LND across a restart of our node; LND settles while we are down
        var preimage = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(preimage);
        var hold = await LndTestHelpers.AddHoldInvoiceAsync(tara, hash, 15_000_000, [], ct, "taproot held");
        var inFlight = await Node.PayInvoiceAsync(hold.PaymentRequest, ct, timeoutSeconds: 5);
        Console.WriteLine($"Our held payment: {inFlight.Status}");
        await LndTestHelpers.WaitForInvoiceStateAsync(tara, hash, Invoice.Types.InvoiceState.Accepted, s_timeout, ct);
        Assert.Equal(1, (await Node.GetChannelAsync(channelId, ct)).OfferedHtlcCount);
        await Node.StopAsync();
        await LndTestHelpers.SettleInvoiceAsync(tara, preimage, ct);
        await Node.StartAsync(ct);
        await WaitUntilReestablishedAsync(tara, channelId, channelPoint, ct);

        // Assert 3: the fulfill reached us after channel_reestablish; the channel carries payments again
        var settled = await Poll.ForAsync(async () =>
        {
            var payment = await Node.GetPaymentAsync(new Hash(hash), ct);
            return payment?.Status == PaymentStatus.Succeeded ? payment : null;
        }, s_timeout, "our held payment succeeded after the restart", ct);
        Assert.Equal(preimage, (byte[])settled.Preimage!.Value);
        await LndTestHelpers.WaitForInvoiceStateAsync(tara, hash, Invoice.Types.InvoiceState.Settled, s_timeout, ct);
        await AssertBalancesAgreeAsync(tara, channelId, channelPoint, weAreFunder: false, ct);
        await LndPaysUsAsync(tara, channelId, lndChannel.ChanId, LightningMoney.Satoshis(7_000), ct);

        // Act 4: LND restarts (same data, new pod)
        await _fixture.RestartLndAsync(LndTaprootNetworkFixture.Alias).WaitAsync(s_timeout, ct);
        tara = Tara;
        await WaitUntilReestablishedAsync(tara, channelId, channelPoint, ct);

        // Assert 4: payments both ways after LND's channel_reestablish
        await WePayLndAsync(tara, LightningMoney.Satoshis(9_000), ct);
        await LndPaysUsAsync(tara, channelId, lndChannel.ChanId, LightningMoney.Satoshis(3_000), ct);
        await AssertBalancesAgreeAsync(tara, channelId, channelPoint, weAreFunder: false, ct);
        await AssertNoForceCloseAsync(tara, channelPoint, ct);

        // Act 5: LND closes cooperatively at 5 sat/vB (its RBF close: closing_complete/closing_sig)
        var shares = await GetSharesAsync(channelId, weAreFunder: false, ct);
        var walletBefore = WalletBalance();
        await LndCloseAsync(tara, channelPoint, 5, ct);

        // Assert 5
        await AssertCooperativelyClosedAsync(tara, channelId, channelPoint, shares, walletBefore, ct);
    }

    [Fact]
    public async Task Given_WeOpenAPrivateTaprootChannel_When_PaymentsAndWeClose_Then_AllWorkAndTheCloseConfirms()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var tara = Tara;
        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [tara], [Node], ct);
        var taraAddress = await Node.ConnectToAsync(tara, ct);

        // Act 1: openchannel --channel-type taproot (v1: LND has no option_dual_fund)
        var opened = await Node.OpenChannelAsync(new OpenChannelClientRequest(taraAddress,
                                                                              LightningMoney.Satoshis(CapacitySat))
        {
            PushAmount = LightningMoney.Satoshis(OurPushSat),
            FeeRatePerKw = LightningMoney.Satoshis(2_500),
            IsSimpleTaproot = true
        }, ct);
        var channelId = opened.ChannelId;
        var channelPoint = opened.ChannelPoint();
        Console.WriteLine($"We opened {channelPoint} to tara");
        await MineUntilUsableAsync(tara, channelId, channelPoint, ct);

        // Assert 1
        var lndChannel = await AssertTaprootChannelAsync(tara, channelId, channelPoint, lndIsInitiator: false, ct);
        await AssertBalancesAgreeAsync(tara, channelId, channelPoint, weAreFunder: true, ct);

        // Act 2: payments both ways
        await WePayLndAsync(tara, LightningMoney.Satoshis(30_000), ct);
        await LndPaysUsAsync(tara, channelId, lndChannel.ChanId, LightningMoney.Satoshis(10_000), ct);
        await AssertBalancesAgreeAsync(tara, channelId, channelPoint, weAreFunder: true, ct);

        // Act 3: our closechannel
        var shares = await GetSharesAsync(channelId, weAreFunder: true, ct);
        var walletBefore = WalletBalance();
        CloseChannelClientResponse closed;
        using (var scope = Node.Services.CreateScope())
        {
            var handler = scope.ServiceProvider
                               .GetRequiredService<IClientCommandHandler<CloseChannelClientRequest,
                                    CloseChannelClientResponse>>();
            closed = await handler.HandleAsync(new CloseChannelClientRequest(channelId)
            {
                WaitSeconds = (uint)s_timeout.TotalSeconds
            }, ct);
        }

        // Assert 3
        Console.WriteLine($"closechannel: {closed.State}, closing tx {closed.ClosingTxId}");
        Assert.Equal(ChannelState.Closing, closed.State);
        await AssertCooperativelyClosedAsync(tara, channelId, channelPoint, shares, walletBefore, ct);
    }

    /// <summary>
    /// NL-978: the taproot analogue of <c>ReestablishFlowTests</c> (c), then an LND restart with an HTLC in flight.
    /// LND pays our invoice; we persist our <c>commitment_signed</c> (MuSig2 partial signature with a JIT nonce) and the
    /// connection dies before LND's <c>revoke_and_ack</c>; after our restart the <c>channel_reestablish</c> exchange
    /// (type-22 nonces) completes the dance with a re-signed retransmission and LND's payment succeeds. Then our HTLC is
    /// held by LND across an LND restart and settled after LND's <c>channel_reestablish</c>.
    /// </summary>
    [Fact]
    public async Task Given_CrashAfterOurCommitmentSignedAndAnLndRestartWithAnHtlc_When_Reestablished_Then_PaymentsComplete()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var tara = Tara;
        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [tara], [Node], ct);
        var taraAddress = await Node.ConnectToAsync(tara, ct);
        var opened = await Node.OpenChannelAsync(new OpenChannelClientRequest(taraAddress,
                                                                              LightningMoney.Satoshis(CapacitySat))
        {
            PushAmount = LightningMoney.Satoshis(OurPushSat),
            FeeRatePerKw = LightningMoney.Satoshis(2_500),
            IsSimpleTaproot = true
        }, ct);
        var channelId = opened.ChannelId;
        var channelPoint = opened.ChannelPoint();
        await MineUntilUsableAsync(tara, channelId, channelPoint, ct);
        var lndChannel = await AssertTaprootChannelAsync(tara, channelId, channelPoint, lndIsInitiator: false, ct);

        var tcpService = Assert.IsType<CrashableTcpService>(
            Node.Services.GetRequiredService<Infrastructure.Transport.Interfaces.ITcpService>());
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Node.ChannelManager.OnResponseMessageReady += (_, args) =>
        {
            // Raised under the channel lock right after the save; cut every connection before anything else
            if (args.ResponseMessage is not Domain.Protocol.Messages.CommitmentSignedMessage
             || crashed.Task.IsCompleted)
                return;

            tcpService.CrashAsync().GetAwaiter().GetResult();
            crashed.TrySetResult();
        };
        var invoice = await Node.CreateInvoiceAsync(LightningMoney.Satoshis(25_000), "taproot crash after cs", ct);

        // Act 1: LND's payment stays in flight across our crash and restart
        Task<Payment> payment;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(s_timeout);
            while (true)
            {
                await LndTestHelpers.ResetMissionControlAsync(tara, ct);
                payment = LndTestHelpers.SendPaymentV2Async(
                    tara, LndTestHelpers.PinnedPayment(invoice.Bolt11!, [lndChannel.ChanId], timeoutSeconds: 300), ct,
                    TimeSpan.FromMinutes(6));
                if (await Task.WhenAny(crashed.Task, payment).WaitAsync(deadline.Token) == crashed.Task)
                    break;

                var failed = await payment;
                Assert.True(failed.FailureReason is PaymentFailureReason.FailureReasonInsufficientBalance
                                                 or PaymentFailureReason.FailureReasonNoRoute,
                            $"LND's payment ended before it reached us: {failed.Status} {failed.FailureReason}");
                await Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token);
            }
        }

        Console.WriteLine("Crashed after persisting our commitment_signed");
        await Node.StopAsync();
        await Node.StartAsync(ct);
        await WaitUntilReestablishedAsync(tara, channelId, channelPoint, ct);
        var result = await payment.WaitAsync(s_timeout, ct);

        // Assert 1: the dance completed and the payment settled on both sides, nothing force-closed
        Console.WriteLine($"LND's payment after our restart: {result.Status} {result.FailureReason}");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, result.Status);
        Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(Convert.FromHexString(result.PaymentPreimage)));
        await AssertBalancesAgreeAsync(tara, channelId, channelPoint, weAreFunder: true, ct);
        await AssertNoForceCloseAsync(tara, channelPoint, ct);

        // Act 2: our HTLC held by LND across an LND restart; LND settles after its channel_reestablish
        var preimage = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(preimage);
        var hold = await LndTestHelpers.AddHoldInvoiceAsync(tara, hash, 12_000_000, [], ct, "taproot held lnd restart");
        var inFlight = await Node.PayInvoiceAsync(hold.PaymentRequest, ct, timeoutSeconds: 5);
        Assert.Equal(PaymentStatus.InFlight, inFlight.Status);
        await LndTestHelpers.WaitForInvoiceStateAsync(tara, hash, Invoice.Types.InvoiceState.Accepted, s_timeout, ct);
        await _fixture.RestartLndAsync(LndTaprootNetworkFixture.Alias).WaitAsync(s_timeout, ct);
        tara = Tara;
        await WaitUntilReestablishedAsync(tara, channelId, channelPoint, ct);
        Assert.Equal(1, (await Node.GetChannelAsync(channelId, ct)).OfferedHtlcCount);
        await LndTestHelpers.SettleInvoiceAsync(tara, preimage, ct);

        // Assert 2
        var settled = await Poll.ForAsync(async () =>
        {
            var paid = await Node.GetPaymentAsync(new Hash(hash), ct);
            return paid?.Status == PaymentStatus.Succeeded ? paid : null;
        }, s_timeout, "our held payment succeeded after LND's restart", ct);
        Assert.Equal(preimage, (byte[])settled.Preimage!.Value);
        await AssertBalancesAgreeAsync(tara, channelId, channelPoint, weAreFunder: true, ct);
        await WePayLndAsync(tara, LightningMoney.Satoshis(4_000), ct);
        await LndPaysUsAsync(tara, channelId, lndChannel.ChanId, LightningMoney.Satoshis(2_000), ct);
        await AssertNoForceCloseAsync(tara, channelPoint, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (TestDiagnostics.CurrentTestFailed)
        {
            foreach (var line in _node?.NodeLog.TakeLast(400) ?? [])
                Console.WriteLine(line);
            await _fixture.DumpLndLogsAsync([LndTaprootNetworkFixture.Alias], 500);
        }

        if (_node is not null)
            await _node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Writes the feature bits LND advertises (its init) and the ones it saw in our init, for the interop record.
    /// </summary>
    private async Task LogFeaturesAsync(LndNodeConnection lnd, CancellationToken ct)
    {
        var info = await lnd.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: ct);
        Console.WriteLine($"{lnd.LocalAlias} {info.Version} advertises bits "
                        + string.Join(",", info.Features.Keys.Order()));
        var peers = await lnd.LightningClient.ListPeersAsync(new ListPeersRequest(), cancellationToken: ct);
        var us = peers.Peers.FirstOrDefault(p => p.PubKey == Node.NodeIdHex);
        if (us is not null)
            Console.WriteLine($"{lnd.LocalAlias} saw our init with bits " + string.Join(",", us.Features.Keys.Order()));
        Assert.True(info.Features.ContainsKey(81), "LND does not advertise option_simple_taproot (81)");
    }

    private async Task MineUntilUsableAsync(LndNodeConnection lnd, ChannelId channelId, string channelPoint,
                                            CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var ours = (await Node.ListChannelsAsync(ct)).Channels.FirstOrDefault(c => c.ChannelId == channelId);
            var theirs = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
            // LND's router also needs the edge before it sends over the channel (NL-768)
            if (ours is not null && ours.IsUsable() && theirs is { Active: true }
             && await LndTestHelpers.HasOwnChannelEdgeAsync(lnd, theirs.ChanId, ct))
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [Node], ct);
            return false;
        }, s_timeout, $"channel {channelPoint} usable on both ends", ct, s_mineInterval);
        await ChainSync.WaitAllAtTipAsync(_fixture, [lnd], [Node], ct);
    }

    /// <summary>
    /// LND lists the channel as <c>SIMPLE_TAPROOT_FINAL</c> and private, we hold it as a simple taproot channel, and its
    /// funding output on chain is a segwit v1 (P2TR) output.
    /// </summary>
    private async Task<Channel> AssertTaprootChannelAsync(LndNodeConnection lnd, ChannelId channelId,
                                                          string channelPoint, bool lndIsInitiator,
                                                          CancellationToken ct)
    {
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
        Assert.NotNull(lndChannel);
        Console.WriteLine($"LND channel {lndChannel.ChanId}: type {lndChannel.CommitmentType}, private "
                        + $"{lndChannel.Private}, initiator {lndChannel.Initiator}, "
                        + $"fee_per_kw {lndChannel.FeePerKw}, commit fee {lndChannel.CommitFee}");
        Assert.Equal(CommitmentType.SimpleTaprootFinal, lndChannel.CommitmentType);
        Assert.True(lndChannel.Private);
        Assert.Equal(lndIsInitiator, lndChannel.Initiator);

        Assert.True(Node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel));
        Assert.True(channel.ChannelParams.OptionSimpleTaproot);
        Assert.False(channel.ChannelParams.AnnounceChannel);

        var parts = channelPoint.Split(':');
        var fundingTx = await _fixture.Bitcoin.GetRawTransactionAsync(uint256.Parse(parts[0]), true, ct);
        var fundingOutput = fundingTx.Outputs[int.Parse(parts[1])];
        Assert.Equal(CapacitySat, fundingOutput.Value.Satoshi);
        Assert.True(fundingOutput.ScriptPubKey.IsScriptType(ScriptType.Taproot),
                    $"funding output {fundingOutput.ScriptPubKey} is not P2TR");
        return lndChannel;
    }

    /// <summary>
    /// Once nothing is pending: the non-funder's balance is its whole <c>to_local</c> on LND's side; the funder's also
    /// pays the commitment fee and the two anchors.
    /// </summary>
    private async Task AssertBalancesAgreeAsync(LndNodeConnection lnd, ChannelId channelId, string channelPoint,
                                                bool weAreFunder, CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            var ours = await Node.GetChannelAsync(channelId, ct);
            var theirs = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
            return ours.OfferedHtlcCount + ours.ReceivedHtlcCount == 0 && theirs is { PendingHtlcs.Count: 0 };
        }, s_timeout, "no HTLC pending on either side", ct);

        var ourChannel = await Node.GetChannelAsync(channelId, ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
        Assert.NotNull(lndChannel);
        Console.WriteLine($"Ours: local {ourChannel.LocalBalance.MilliSatoshi} remote "
                        + $"{ourChannel.RemoteBalance.MilliSatoshi} msat; LND: local {lndChannel.LocalBalance} remote "
                        + $"{lndChannel.RemoteBalance} commit fee {lndChannel.CommitFee} fee_per_kw "
                        + $"{lndChannel.FeePerKw}");
        if (weAreFunder)
        {
            Assert.Equal(ourChannel.RemoteBalance.Satoshi, lndChannel.LocalBalance);
            Assert.Equal(ourChannel.LocalBalance.Satoshi,
                         lndChannel.RemoteBalance + lndChannel.CommitFee + LndTestHelpers.FunderAnchorsSat(lndChannel));
        }
        else
        {
            Assert.Equal(ourChannel.LocalBalance.Satoshi, lndChannel.RemoteBalance);
            Assert.Equal(ourChannel.RemoteBalance.Satoshi,
                         lndChannel.LocalBalance + lndChannel.CommitFee + LndTestHelpers.FunderAnchorsSat(lndChannel));
        }

        Assert.True(lndChannel.Active, "LND no longer lists the channel as active");
        Assert.True(ourChannel.IsUsable(), ourChannel.Describe());
    }

    /// <summary>LND pays an invoice of ours over the channel (retried while its router lacks the edge, NL-319).</summary>
    private async Task LndPaysUsAsync(LndNodeConnection lnd, ChannelId channelId, ulong chanId, LightningMoney amount,
                                      CancellationToken ct)
    {
        // Our balance is gross (it counts our offered HTLCs): read it once an earlier payment's dance has ended
        var before = await Poll.ForAsync(async () =>
        {
            var channel = await Node.GetChannelAsync(channelId, ct);
            return channel.OfferedHtlcCount + channel.ReceivedHtlcCount == 0 ? channel : null;
        }, s_timeout, "no HTLC pending on our side", ct);
        var invoice = await Node.CreateInvoiceAsync(amount, $"taproot lnd pays {amount.Satoshi} sat", ct);
        var retryUntil = DateTime.UtcNow + s_timeout;
        while (true)
        {
            await LndTestHelpers.ResetMissionControlAsync(lnd, ct);
            var payment = await LndTestHelpers.SendPaymentV2Async(
                              lnd, LndTestHelpers.PinnedPayment(invoice.Bolt11!, [chanId]), ct);
            Console.WriteLine($"LND's payment of {amount.Satoshi} sat: {payment.Status} {payment.FailureReason}");
            if (payment.Status == Payment.Types.PaymentStatus.Succeeded)
            {
                Assert.Equal((byte[])invoice.PaymentHash, SHA256.HashData(Convert.FromHexString(payment.PaymentPreimage)));
                break;
            }

            Assert.True(payment.FailureReason is PaymentFailureReason.FailureReasonInsufficientBalance
                                              or PaymentFailureReason.FailureReasonNoRoute
                     && DateTime.UtcNow < retryUntil,
                        $"LND's payment failed: {payment.Status} {payment.FailureReason}");
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        await Poll.UntilAsync(async () =>
        {
            var after = await Node.GetChannelAsync(channelId, ct);
            return after.OfferedHtlcCount + after.ReceivedHtlcCount == 0
                && after.LocalBalance.MilliSatoshi == before.LocalBalance.MilliSatoshi + amount.MilliSatoshi;
        }, s_timeout, $"our balance up by {amount.MilliSatoshi} msat", ct);
    }

    private async Task WePayLndAsync(LndNodeConnection lnd, LightningMoney amount, CancellationToken ct)
    {
        var invoice = await LndTestHelpers.AddInvoiceAsync(lnd, (long)amount.MilliSatoshi, [], ct,
                                                           $"taproot we pay {amount.Satoshi} sat");
        var payment = await Node.PayInvoiceAsync(invoice.PaymentRequest, ct);
        Console.WriteLine($"Our payment of {amount.Satoshi} sat: {payment.Status}, failure {payment.FailureCode}: "
                        + payment.FailureReason);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        await LndTestHelpers.WaitForInvoiceStateAsync(lnd, invoice.RHash.ToByteArray(),
                                                      Invoice.Types.InvoiceState.Settled, s_timeout, ct);
    }

    /// <summary>
    /// We processed LND's <c>channel_reestablish</c> on the current connection and LND lists the channel active.
    /// </summary>
    private async Task WaitUntilReestablishedAsync(LndNodeConnection lnd, ChannelId channelId, string channelPoint,
                                                   CancellationToken ct)
    {
        await Poll.UntilAsync(async () =>
        {
            if (!Node.IsConnectedTo(lnd.LocalNodePubKeyBytes)
             || !Node.Services.GetRequiredService<IReestablishTracker>().IsReestablished(channelId))
                return false;

            try
            {
                var lndChannel = await LndTestHelpers.GetChannelByPointAsync(lnd, channelPoint, ct);
                return lndChannel is { Active: true };
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A restarting LND refuses RPCs for a moment
                return false;
            }
        }, s_timeout, "the channel reestablished and active on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [lnd], [Node], ct);
        Assert.True((await Node.GetChannelAsync(channelId, ct)).IsUsable());
    }

    private async Task AssertNoForceCloseAsync(LndNodeConnection lnd, string channelPoint, CancellationToken ct)
    {
        var pending = await lnd.LightningClient.PendingChannelsAsync(new PendingChannelsRequest(),
                                                                     cancellationToken: ct);
        Assert.DoesNotContain(pending.WaitingCloseChannels, c => c.Channel.ChannelPoint == channelPoint);
        Assert.DoesNotContain(pending.PendingForceClosingChannels, c => c.Channel.ChannelPoint == channelPoint);
        Assert.Equal(0, Node.CountLogLines("Failing channel"));
    }

    /// <summary>
    /// Each side's share of the channel once nothing is pending, in satoshis (rounded down): the non-funder's balance,
    /// and the rest of the capacity for the funder (its commitment fee and anchors come back to it in a close).
    /// </summary>
    private async Task<(long Ours, long Lnd)> GetSharesAsync(ChannelId channelId, bool weAreFunder,
                                                             CancellationToken ct)
    {
        var channel = await Node.GetChannelAsync(channelId, ct);
        Assert.Equal(0, channel.OfferedHtlcCount + channel.ReceivedHtlcCount);
        if (weAreFunder)
        {
            var lnd = (long)(channel.RemoteBalance.MilliSatoshi / 1_000);
            return (CapacitySat - lnd, lnd);
        }

        var ours = (long)(channel.LocalBalance.MilliSatoshi / 1_000);
        return (ours, CapacitySat - ours);
    }

    /// <summary>LND's <c>CloseChannel</c> at <paramref name="satPerVbyte"/>, read until it reports the pending close.</summary>
    private static async Task LndCloseAsync(LndNodeConnection lnd, string channelPoint, ulong satPerVbyte,
                                            CancellationToken ct)
    {
        var parts = channelPoint.Split(':');
        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        closeTimeout.CancelAfter(s_timeout);
        using var closeCall = lnd.LightningClient.CloseChannel(new CloseChannelRequest
        {
            ChannelPoint = new ChannelPoint { FundingTxidStr = parts[0], OutputIndex = uint.Parse(parts[1]) },
            SatPerVbyte = satPerVbyte
        }, cancellationToken: closeTimeout.Token);
        PendingUpdate? pending = null;
        while (pending is null && await closeCall.ResponseStream.MoveNext(closeTimeout.Token))
            pending = closeCall.ResponseStream.Current.ClosePending;
        Assert.NotNull(pending);
        Console.WriteLine($"LND close pending: {Convert.ToHexString(pending.Txid.ToByteArray())}, "
                        + $"local {pending.LocalCloseTx}");
    }

    /// <summary>
    /// A closing transaction of the funding output reaches bitcoind's mempool: a BOLT 3 simple close (version 2,
    /// sequence 0xFFFFFFFD) whose only input is a MuSig2 key-path spend (one 64-byte witness element), with each side's
    /// share less the fee on the closer's side. After 6 blocks LND lists a cooperative close with that txid and its
    /// output as its settled balance, our channel is Closed and our wallet holds our output.
    /// </summary>
    private async Task AssertCooperativelyClosedAsync(LndNodeConnection lnd, ChannelId channelId,
                                                      string channelPoint, (long Ours, long Lnd) shares,
                                                      LightningMoney walletBefore, CancellationToken ct)
    {
        // LND's RBF close: each side sends its closing_complete and the other signs it, MuSig2 both ways. The blocks
        // are mined as soon as the first closing transaction is in the mempool: a closing_sig that arrives after it
        // confirmed must not replace our record of the close (NL-983, seen here in run tap2lnd-2). Conflicting
        // transactions: bitcoind keeps one
        var parts = channelPoint.Split(':');
        var fundingOutPoint = new NBitcoin.OutPoint(uint256.Parse(parts[0]), uint.Parse(parts[1]));
        var closingTx = await Poll.ForAsync(async () =>
        {
            foreach (var txid in await _fixture.Bitcoin.GetRawMempoolAsync(ct))
            {
                // The two closing transactions conflict: the one listed may be replaced before it is read
                var mempoolTx = await _fixture.Bitcoin.GetRawTransactionAsync(txid, null, false, ct);
                if (mempoolTx is not null && mempoolTx.Inputs.Any(i => i.PrevOut == fundingOutPoint))
                    return mempoolTx;
            }

            return null;
        }, s_timeout, "a closing transaction in the mempool", ct);
        Assert.True(Node.ChannelMemoryRepository.TryGetChannel(channelId, out var channel));
        var ourScript = (byte[])channel.LocalShutdownScript!.Value;
        var input = Assert.Single(closingTx.Inputs);
        Assert.Equal(2U, closingTx.Version);
        Assert.Equal(0xFFFFFFFDU, (uint)input.Sequence);
        var witness = Assert.Single(input.WitScript.Pushes);
        Assert.Equal(64, witness.Length);

        var ourOutput = closingTx.Outputs.Where(o => o.ScriptPubKey.ToBytes().SequenceEqual(ourScript))
                                 .Sum(o => o.Value.Satoshi);
        var lndOutput = closingTx.Outputs.Where(o => !o.ScriptPubKey.ToBytes().SequenceEqual(ourScript))
                                 .Sum(o => o.Value.Satoshi);
        var fee = CapacitySat - ourOutput - lndOutput;
        Console.WriteLine($"Closing transaction {closingTx.GetHash()}: fee {fee} sat, ours {ourOutput} (share "
                        + $"{shares.Ours}), LND's {lndOutput} (share {shares.Lnd})");
        Assert.InRange(fee, 1, 20_000);
        // BOLT 2 simple close: the closer pays the whole fee from its output, the closee gets its whole share
        var lndClosed = lndOutput < shares.Lnd;
        Assert.Equal(lndClosed ? shares.Ours : shares.Ours - fee, ourOutput);
        Assert.Equal(lndClosed ? shares.Lnd - fee : shares.Lnd, lndOutput);

        await Poll.UntilAsync(async () =>
        {
            var pendingChannels = await lnd.LightningClient.PendingChannelsAsync(new PendingChannelsRequest(),
                                                                                 cancellationToken: ct);
            return pendingChannels.WaitingCloseChannels.Any(c => c.Channel.ChannelPoint == channelPoint);
        }, s_timeout, "LND waits for the closing transaction", ct);
        await Node.MineBlocksAsync(6, ct);

        var summary = await Poll.ForAsync(async () =>
        {
            var closedChannels = await lnd.LightningClient.ClosedChannelsAsync(
                                     new ClosedChannelsRequest { Cooperative = true }, cancellationToken: ct);
            return closedChannels.Channels.FirstOrDefault(c => c.ChannelPoint == channelPoint);
        }, s_timeout, "LND lists the cooperative close", ct);
        Console.WriteLine($"LND close summary: type {summary.CloseType}, initiator {summary.CloseInitiator}, "
                        + $"settled {summary.SettledBalance}, LND closed: {lndClosed}");
        Assert.Equal(ChannelCloseSummary.Types.ClosureType.CooperativeClose, summary.CloseType);
        Assert.Equal(closingTx.GetHash().ToString(), summary.ClosingTxHash);
        Assert.Equal(lndOutput, summary.SettledBalance);

        await Poll.UntilAsync(async () =>
        {
            var ours = (await Node.ListChannelsAsync(ct)).Channels.Single(c => c.ChannelId == channelId);
            return ours.State == ChannelState.Closed;
        }, s_timeout, "our channel is Closed", ct);
        await Poll.UntilAsync(() => Task.FromResult(WalletBalance() - walletBefore == LightningMoney.Satoshis(ourOutput)),
                              s_timeout, "our wallet received our closing output", ct);
    }

    private LightningMoney WalletBalance() => CooperativeCloseFlowTests.WalletBalance(Node);
}
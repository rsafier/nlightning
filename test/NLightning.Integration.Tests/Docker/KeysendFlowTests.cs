using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Testing.Lnd.Routerrpc;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Daemon.Extensions;
using Daemon.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Keysend;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// Lane lh1-l3 Docker proof of keysend against LND 0.21 (alice runs <c>--accept-keysend</c>), both directions, with
/// custom records: our <c>keysend</c> client handler pays alice (her invoice is a keysend invoice whose HTLC carries
/// our records), and alice's <c>SendPaymentV2</c> with <c>dest_custom_records</c> {5482373484: preimage, ...} pays us
/// (our <c>listinvoices</c> shows a settled keysend record with her records, an even type included).
/// </summary>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class KeysendFlowTests : IAsyncLifetime
{
    private const ulong BoostagramType = 7629169;
    private const ulong EvenCustomType = 133773310;

    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);

    private readonly LightningRegtestNetworkFixture _fixture;
    private NLightningTestNode? _node;

    public KeysendFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private NLightningTestNode Node => _node ?? throw new InvalidOperationException("The node was not created");

    public async ValueTask InitializeAsync()
    {
        _node = await NLightningTestNode.CreateAsync(_fixture, "keysend");
        // The daemon registers the keysend IPC handler through this extension (the integrator's line)
        _node.ConfigureServices = services => services.AddKeysendIpcServices();
        await Node.StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_AChannelWithLnd_When_KeysendBothWaysWithCustomRecords_Then_BothSettleWithTheRecords()
    {
        // Arrange: our channel to alice, part of it pushed to her
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var peerAddress = await Node.ConnectToAsync(alice, ct);
        var channel = await OpenUsableChannelAsync(alice, peerAddress, ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channel.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);

        // Act 1: we keysend alice 12,345 sat with a boostagram and an even custom record
        var boostagram = "{\"action\":\"boost\",\"app_name\":\"nltg\"}"u8.ToArray();
        var ours = await KeysendAsync(new KeysendClientRequest(new CompactPubKey(alice.LocalNodePubKeyBytes),
                                                               LightningMoney.Satoshis(12_345))
        {
            CustomRecords = [new CustomRecord(BoostagramType, boostagram), new CustomRecord(EvenCustomType, [0x2a])],
            TimeoutSeconds = 90
        }, ct);

        // Assert 1: succeeded directly (no fee), alice holds a settled keysend invoice with our records
        Console.WriteLine($"Our keysend: {ours.Payment.Status} {ours.Payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, ours.Payment.Status);
        Assert.True(ours.Payment.IsKeysend);
        Assert.Equal(LightningMoney.Zero, ours.Payment.Fee);
        Assert.Equal(ours.Payment.PaymentHash, new Hash(SHA256.HashData(ours.Payment.Preimage!.Value)));
        var aliceInvoice = await alice.LightningClient.LookupInvoiceAsync(
                               new PaymentHash { RHash = ByteString.CopyFrom(ours.Payment.PaymentHash) },
                               cancellationToken: ct);
        Assert.True(aliceInvoice.IsKeysend);
        Assert.Equal(Invoice.Types.InvoiceState.Settled, aliceInvoice.State);
        Assert.Equal(12_345_000L, aliceInvoice.AmtPaidMsat);
        var htlc = Assert.Single(aliceInvoice.Htlcs);
        Assert.Equal(boostagram, htlc.CustomRecords[BoostagramType].ToByteArray());
        Assert.Equal(new byte[] { 0x2a }, htlc.CustomRecords[EvenCustomType].ToByteArray());
        Assert.Equal((byte[])ours.Payment.Preimage!.Value, htlc.CustomRecords[CustomRecordCodec.KeysendPreimageType]
                                                              .ToByteArray());

        // Act 2: alice keysends us 7,777 sat with her preimage and records
        var preimage = RandomNumberGenerator.GetBytes(32);
        var hash = SHA256.HashData(preimage);
        var theirs = await LndKeysendUntilDoneAsync(alice, Node.NodeId, 7_777, preimage, lndChannel.ChanId,
                                                    new Dictionary<ulong, byte[]>
                                                    {
                                                        [BoostagramType] = "{\"message\":\"hi\"}"u8.ToArray(),
                                                        [EvenCustomType] = [0x01, 0x02]
                                                    }, ct);

        // Assert 2: LND succeeded, our record is a settled keysend invoice with her records
        Console.WriteLine($"Alice's keysend: {theirs.Status} {theirs.FailureReason}, {theirs.Htlcs.Count} attempt(s)");
        foreach (var attempt in theirs.Htlcs)
            Console.WriteLine($"  attempt {attempt.AttemptId}: {attempt.Status} {attempt.Failure?.Code} "
                            + $"from {attempt.Failure?.FailureSourceIndex}");
        Assert.Equal(Payment.Types.PaymentStatus.Succeeded, theirs.Status);
        Assert.Equal(Convert.ToHexString(preimage), theirs.PaymentPreimage, ignoreCase: true);
        var record = await Poll.ForAsync(async () =>
        {
            var stored = await Node.GetInvoiceAsync(new Hash(hash), ct);
            return stored?.Status == InvoiceStatus.Settled ? stored : null;
        }, s_timeout, "our keysend record settled", ct);
        Assert.Equal(InvoiceKind.Keysend, record.Kind);
        Assert.Equal(LightningMoney.Satoshis(7_777), record.AmountReceived);
        Assert.Null(record.Bolt11);
        Assert.Collection(record.CustomRecords,
                          r => Assert.Equal((BoostagramType, "{\"message\":\"hi\"}"),
                                            (r.Type, System.Text.Encoding.UTF8.GetString(r.Value.Span))),
                          r => Assert.Equal((EvenCustomType, "0102"), (r.Type, Convert.ToHexString(r.Value.Span))));

        // Our listpayments shows our keysend with its records
        var listed = await Node.GetPaymentAsync(ours.Payment.PaymentHash, ct);
        Assert.NotNull(listed);
        Assert.True(listed.IsKeysend);
        Assert.Equal([BoostagramType, EvenCustomType], listed.CustomRecords.Select(r => r.Type));
        Assert.True((await Node.GetChannelAsync(channel.ChannelId, ct)).IsUsable());
    }

    [Fact]
    public async Task Given_AKeysendWithAWrongPreimage_When_LndPaysUs_Then_FailedWithIncorrectPaymentDetails()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var peerAddress = await Node.ConnectToAsync(alice, ct);
        var channel = await OpenUsableChannelAsync(alice, peerAddress, ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channel.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        var preimage = RandomNumberGenerator.GetBytes(32);

        // Act: the hash is not SHA256 of the keysend_preimage record
        var payment = await LndKeysendUntilDoneAsync(alice, Node.NodeId, 1_000, preimage, lndChannel.ChanId, [], ct,
                                                     RandomNumberGenerator.GetBytes(32));

        // Assert: our final hop refused it as an unknown hash, and kept no record
        Assert.Equal(Payment.Types.PaymentStatus.Failed, payment.Status);
        Assert.Contains(payment.Htlcs,
                        h => h.Failure?.Code == Failure.Types.FailureCode.IncorrectOrUnknownPaymentDetails);
        Assert.Null(await Node.GetInvoiceAsync(new Hash(Convert.FromHexString(payment.PaymentHash)), ct));
        Assert.True((await Node.GetChannelAsync(channel.ChannelId, ct)).IsUsable());
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var line in _node?.NodeLog.TakeLast(300) ?? [])
                Console.WriteLine(line);
            await _fixture.DumpLndLogsAsync(["alice"]);
        }

        if (_node is not null)
            await _node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async Task<PayInvoiceClientResponse> KeysendAsync(KeysendClientRequest request, CancellationToken ct)
    {
        // A fresh private channel is active before LND's router has it; our own sends need no such wait, but a
        // refused HTLC (link not reestablished yet) is retried by the payment service within the timeout
        using var scope = Node.Services.CreateScope();
        var handler = scope.ServiceProvider
                           .GetRequiredService<IClientCommandHandler<KeysendClientRequest, PayInvoiceClientResponse>>();
        return await handler.HandleAsync(request, ct);
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelAsync(LndNodeConnection peer,
        string peerAddress, CancellationToken ct)
    {
        await Node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var channel = await Node.OpenChannelAsync(new OpenChannelClientRequest(peerAddress, s_capacity)
        {
            PushAmount = s_push,
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {peer.LocalAlias}");

        await Poll.UntilAsync(async () =>
        {
            var ours = await Node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(peer, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && lnd is { Active: true })
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [peer], [Node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [peer], [Node], ct);
        return channel;
    }

    /// <summary>
    /// LND keysends <paramref name="amountSat"/> to <paramref name="destination"/> over <paramref name="chanId"/>
    /// (<c>dest_custom_records</c> with the keysend preimage, final CLTV delta 40, TLV onion assumed for a node LND has
    /// no announcement of). Started again while LND fails it for want of a route or balance (a fresh private channel's
    /// edge, NL-319); the final update of any other outcome is returned.
    /// </summary>
    private static async Task<Payment> LndKeysendUntilDoneAsync(LndNodeConnection lnd, CompactPubKey destination,
                                                                long amountSat, byte[] preimage, ulong chanId,
                                                                Dictionary<ulong, byte[]> records,
                                                                CancellationToken ct, byte[]? paymentHash = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(s_timeout);
        while (true)
        {
            await LndTestHelpers.ResetMissionControlAsync(lnd, ct);
            var request = new SendPaymentRequest
            {
                Dest = ByteString.CopyFrom((byte[])destination),
                Amt = amountSat,
                PaymentHash = ByteString.CopyFrom(paymentHash ?? SHA256.HashData(preimage)),
                FinalCltvDelta = 40,
                TimeoutSeconds = 60,
                FeeLimitMsat = 1_000_000,
                MaxParts = 1,
                NoInflightUpdates = true
            };
            request.OutgoingChanIds.Add(chanId);
            request.DestFeatures.Add(FeatureBit.TlvOnionOpt);
            request.DestCustomRecords.Add(CustomRecordCodec.KeysendPreimageType, ByteString.CopyFrom(preimage));
            foreach (var (type, value) in records)
                request.DestCustomRecords.Add(type, ByteString.CopyFrom(value));

            var payment = await LndTestHelpers.SendPaymentV2Async(lnd, request, ct, TimeSpan.FromMinutes(2));
            if (payment.Status == Payment.Types.PaymentStatus.Succeeded
             || payment.FailureReason is not (PaymentFailureReason.FailureReasonInsufficientBalance
                                              or PaymentFailureReason.FailureReasonNoRoute)
             || payment.Htlcs.Any(h => h.Status == HTLCAttempt.Types.HTLCStatus.Succeeded))
                return payment;

            Console.WriteLine($"{lnd.LocalAlias}'s keysend failed with {payment.FailureReason} after "
                            + $"{payment.Htlcs.Count} attempt(s); retrying");
            await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
    }
}
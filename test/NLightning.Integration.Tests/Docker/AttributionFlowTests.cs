using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Lnrpc;
using LNUnit.LND;
using Microsoft.Extensions.DependencyInjection;
using Routerrpc;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Application.Gossip.Interfaces;
using Application.Payments.Onion;
using Domain.Bitcoin.Enums;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Fixtures;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using TestCollections;
using Utils;

/// <summary>
/// ABCD W7-A Docker proof of <c>attribution_data</c> on the wire (BOLT 2 <c>update_fail_htlc</c> TLV 1, BOLT 4
/// attributable failures and hold times; NL-326, NL-072, NL-022).
/// </summary>
/// <remarks>
/// <para>LND 0.20 does not implement <c>option_attribution_data</c> (no feature bit 36/37, no TLV on its
/// <c>update_fail_htlc</c>): the proof against LND is therefore that an <c>update_fail_htlc</c> carrying our 920-byte
/// TLV 1 is accepted and its legacy return packet read, and that a failure from LND without attribution is read as
/// before with no hold time. The attribution itself is proven between two NLightning nodes: the erring node creates it
/// with its hold time (measured from the receipt time stored with the HTLC) and the origin's <c>PaymentService</c>
/// verifies it and records the hold time on the payment's route.</para>
/// <para>The failure case runs a test decorator of <see cref="IHtlcSwitch"/> on the erring node that fails chosen payment
/// hashes through the attributed <c>IChannelOperations.FailHtlcAsync</c> overload. The production switch attributes
/// when the node advertises <c>option_attribution_data</c> (W7 integration): three NLightning nodes with the feature
/// forward a payment, and the payer verifies the payee's and the forwarding node's hold times from the fulfill.</para>
/// </remarks>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class AttributionFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private static readonly LightningMoney s_capacity = LightningMoney.Satoshis(1_000_000);
    private static readonly LightningMoney s_push = LightningMoney.Satoshis(300_000);
    private static readonly TimeSpan s_holdBeforeFailing = TimeSpan.FromMilliseconds(1_500);

    private readonly LightningRegtestNetworkFixture _fixture;
    private readonly ConcurrentDictionary<Hash, byte> _failWithAttribution = new();
    private readonly ConcurrentQueue<UpdateFailHtlcMessage> _sentFails = new();
    private readonly List<NLightningTestNode> _nodes = [];
    private readonly ConcurrentQueue<uint> _reportedHoldTimes = new();

    public AttributionFlowTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Given_Lnd020_When_ItsFeaturesAreRead_Then_OptionAttributionDataIsNotAdvertised()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");

        // Act
        var info = await alice.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: ct);

        // Assert: why OptionAttributionData stays experimental (the LND interop proof cannot pass yet)
        Console.WriteLine($"LND {info.Version}: features {string.Join(", ", info.Features.Keys.Order())}");
        Assert.DoesNotContain(36u, info.Features.Keys);
        Assert.DoesNotContain(37u, info.Features.Keys);
    }

    [Fact]
    public async Task Given_WeFailLndsHtlcWithAttributionData_When_LndReceivesIt_Then_ItAcceptsTheTlvAndReadsTheFailure()
    {
        // Arrange: our node fails the hash with an attributed update_fail_htlc
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var node = await StartNodeAsync("attr-lnd-in", attributing: true, ct);
        var channel = await OpenUsableChannelToLndAsync(node, alice, ct);
        var lndChannel = await LndTestHelpers.GetChannelByPointAsync(alice, channel.ChannelPoint(), ct);
        Assert.NotNull(lndChannel);
        const long amountMsat = 10_000_000;
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        _failWithAttribution[new Hash(paymentHash)] = 0;
        // LND's router learns a fresh private channel's policy a moment after the channel turns active (NL-319)
        var route = await Poll.ForAsync(async () =>
        {
            try
            {
                return await alice.RouterClient.BuildRouteAsync(new BuildRouteRequest
                {
                    AmtMsat = amountMsat,
                    FinalCltvDelta = 40,
                    OutgoingChanId = lndChannel.ChanId,
                    HopPubkeys = { ByteString.CopyFrom(node.NodeId) }
                }, cancellationToken: ct);
            }
            catch (RpcException e)
            {
                Console.WriteLine($"BuildRoute: {e.Status.Detail}; retrying");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                return null;
            }
        }, s_timeout, "LND builds a route to us", ct);
        route.Route.Hops[^1].MppRecord = new MPPRecord
        {
            PaymentAddr = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            TotalAmtMsat = amountMsat
        };

        // Act
        var attempt = await alice.RouterClient.SendToRouteV2Async(new Routerrpc.SendToRouteRequest
        {
            PaymentHash = ByteString.CopyFrom(paymentHash),
            Route = route.Route
        }, cancellationToken: ct);

        // Assert: we sent TLV 1 (920 bytes) and LND still read our return packet (0x400F from us, index 1)
        Console.WriteLine($"SendToRouteV2: {attempt.Status}, {attempt.Failure?.Code}, index "
                        + $"{attempt.Failure?.FailureSourceIndex}");
        var sent = Assert.Single(_sentFails);
        Assert.NotNull(sent.AttributionDataTlv);
        Assert.Equal(920, sent.AttributionDataTlv.AttributionData.Length);
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Failed, attempt.Status);
        Assert.NotNull(attempt.Failure);
        Assert.Equal(Failure.Types.FailureCode.IncorrectOrUnknownPaymentDetails, attempt.Failure.Code);
        Assert.Equal(1u, attempt.Failure.FailureSourceIndex);
        Assert.True(Assert.Single(_reportedHoldTimes) >= 15, "our hold time is at least the time we held it");

        // The channel stays usable on both sides: LND did not fail it over the unknown odd TLV
        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channel.ChannelId, ct);
            var lnd = await LndTestHelpers.GetChannelByPointAsync(alice, channel.ChannelPoint(), ct);
            return ours.IsUsable() && ours.OfferedHtlcCount + ours.ReceivedHtlcCount == 0
                && lnd is { Active: true, PendingHtlcs.Count: 0 };
        }, s_timeout, "the channel usable with nothing pending after the attributed failure", ct);
        Assert.True(await LndTestHelpers.IsConnectedToAsync(alice, node.NodeIdHex, ct), "LND disconnected");
    }

    [Fact]
    public async Task Given_LndFailsOurPayment_When_ItSendsNoAttribution_Then_TheFailureIsReadAndNoHoldTimeRecorded()
    {
        // Arrange: a hold invoice of alice's, canceled before we pay it
        var ct = TestContext.Current.CancellationToken;
        var alice = _fixture.GetLndNode("alice");
        var node = await StartNodeAsync("attr-lnd-out", attributing: false, ct);
        await OpenUsableChannelToLndAsync(node, alice, ct);
        var (_, paymentHash) = LndTestHelpers.NewPreimage();
        var invoice = await LndTestHelpers.AddHoldInvoiceAsync(alice, paymentHash, 20_000_000, [], ct,
                                                               "w7a canceled");
        await LndTestHelpers.CancelInvoiceAsync(alice, paymentHash, ct);

        // Act
        var payment = await node.PayInvoiceAsync(invoice.PaymentRequest, ct);

        // Assert: LND's legacy failure is read (alice is the payee, index 0) and nothing claims a hold time
        Console.WriteLine($"Our payment: {payment.Status}, {payment.FailureCode}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        Assert.Equal(0, payment.FailureSourceIndex);
        Assert.DoesNotContain("hold times", payment.FailureReason);
        var stored = await node.Services.GetRequiredService<IPaymentService>()
                               .GetPaymentAsync(new Hash(paymentHash), ct);
        Assert.NotNull(stored);
        Assert.All(stored.Route, h => Assert.Null(h.HoldTime));
    }

    [Fact]
    public async Task Given_TwoNLightningNodes_When_ThePayeeFailsWithAttribution_Then_ThePayerVerifiesItAndRecordsItsHoldTime()
    {
        // Arrange: bob (production) pays carol, whose node fails the hash with attribution_data after holding it
        var ct = TestContext.Current.CancellationToken;
        var bob = await StartNodeAsync("attr-bob", attributing: false, ct);
        var carol = await StartNodeAsync("attr-carol", attributing: true, ct);
        await bob.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [], [bob, carol], ct);
        await bob.ConnectToAsync(carol, ct);
        var channel = await bob.OpenChannelAsync(new OpenChannelClientRequest(carol.Address, s_capacity)
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        await Poll.UntilAsync(async () =>
        {
            var ours = await bob.GetChannelAsync(channel.ChannelId, ct);
            var theirs = await carol.GetChannelAsync(channel.ChannelId, ct);
            if (ours.IsUsable() && theirs.IsUsable() && ours.ShortChannelId is not null)
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [], [bob, carol], ct);
            return false;
        }, s_timeout, "the bob-carol channel usable on both ends", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [], [bob, carol], ct);

        var invoice = await carol.CreateInvoiceAsync(LightningMoney.Satoshis(25_000), "w7a attributed", ct);
        _failWithAttribution[invoice.PaymentHash] = 0;

        // Act
        var payment = await bob.PayInvoiceAsync(invoice.Bolt11, ct);

        // Assert: carol's failure reached bob with attribution_data; bob verified it and blames carol (index 0)
        Console.WriteLine($"Bob's payment: {payment.Status}, {payment.FailureCode}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, payment.FailureCode);
        Assert.Equal(0, payment.FailureSourceIndex);
        Assert.Contains("hold times", payment.FailureReason);
        Assert.NotNull(Assert.Single(_sentFails).AttributionDataTlv);

        // The hold time bob recorded is the one carol reported, at least the time carol held the HTLC
        var reported = Assert.Single(_reportedHoldTimes);
        var stored = await bob.Services.GetRequiredService<IPaymentService>().GetPaymentAsync(invoice.PaymentHash, ct);
        Assert.NotNull(stored);
        var hop = Assert.Single(stored.Route);
        Assert.Equal(AttributionHoldTime.ToDuration(reported), hop.HoldTime);
        Assert.True(hop.HoldTime >= s_holdBeforeFailing, $"carol's hold time {hop.HoldTime}");
        Assert.True((await bob.GetChannelAsync(channel.ChannelId, ct)).IsUsable());
    }

    [Fact]
    public async Task Given_ThreeNodesAdvertisingAttribution_When_APaymentIsForwarded_Then_ThePayerRecordsEveryHopsHoldTime()
    {
        // Arrange: payer -> hop -> payee, production switches with option_attribution_data (experimental) advertised
        var ct = TestContext.Current.CancellationToken;
        var payer = await StartAdvertisingNodeAsync("attr-payer", ct);
        var hop = await StartAdvertisingNodeAsync("attr-hop", ct);
        var payee = await StartAdvertisingNodeAsync("attr-payee", ct);
        await payer.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await hop.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [], [payer, hop, payee], ct);
        await payer.ConnectToAsync(hop, ct);
        await hop.ConnectToAsync(payee, ct);
        var first = await payer.OpenChannelAsync(new OpenChannelClientRequest(hop.Address, s_capacity)
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        var second = await hop.OpenChannelAsync(new OpenChannelClientRequest(payee.Address, s_capacity)
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        await Poll.UntilAsync(async () =>
        {
            var usable = (await payer.GetChannelAsync(first.ChannelId, ct)).IsUsable()
                      && (await hop.GetChannelAsync(first.ChannelId, ct)).IsUsable()
                      && (await hop.GetChannelAsync(second.ChannelId, ct)).IsUsable()
                      && (await payee.GetChannelAsync(second.ChannelId, ct)) is { ShortChannelId: not null } theirs
                      && theirs.IsUsable()
                      && payee.Services.GetRequiredService<IChannelUpdateService>()
                              .TryGetRemoteChannelUpdate(second.ChannelId, out _);
            if (usable)
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [], [payer, hop, payee], ct);
            return false;
        }, s_timeout, "both channels usable and the hop's channel_update at the payee", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [], [payer, hop, payee], ct);
        var fulfills = new ConcurrentQueue<UpdateFulfillHtlcMessage>();
        foreach (var node in new[] { hop, payee })
            node.Services.GetRequiredService<IChannelManager>().OnResponseMessageReady += (_, args) =>
            {
                if (args.ResponseMessage is UpdateFulfillHtlcMessage fulfill)
                    fulfills.Enqueue(fulfill);
            };

        // The payee's invoice carries a route hint through the hop (a private channel)
        var invoice = await payee.CreateInvoiceAsync(LightningMoney.Satoshis(25_000), "w7 attributed forward", ct);

        // Act
        var payment = await payer.PayInvoiceAsync(invoice.Bolt11, ct);

        // Assert: both fulfills carried attribution_data, and the payer verified a hold time for both hops
        Console.WriteLine($"Payer's payment: {payment.Status}, {payment.FailureCode}: {payment.FailureReason}");
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        await Poll.UntilAsync(() => Task.FromResult(fulfills.Count >= 2), s_timeout, "both fulfills sent", ct);
        Assert.All(fulfills, f => Assert.NotNull(f.AttributionDataTlv));
        var stored = await payer.Services.GetRequiredService<IPaymentService>().GetPaymentAsync(invoice.PaymentHash, ct);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.Route.Count);
        Assert.All(stored.Route, h => Assert.NotNull(h.HoldTime));
        Assert.True(stored.Route[0].HoldTime >= stored.Route[1].HoldTime,
                    $"the hop held the HTLC at least as long as the payee ({stored.Route[0].HoldTime} < "
                  + $"{stored.Route[1].HoldTime})");
    }

    public async ValueTask DisposeAsync()
    {
        if (DockerDiagnostics.CurrentTestFailed)
        {
            foreach (var node in _nodes)
                foreach (var line in node.NodeLog.TakeLast(200))
                    Console.WriteLine(line);
            await DockerDiagnostics.DumpContainerLogsAsync(["alice"]);
        }

        foreach (var node in _nodes)
            await node.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A fresh node; with <paramref name="attributing"/> its switch fails the hashes in
    /// <see cref="_failWithAttribution"/> with <c>attribution_data</c>, and every <c>update_fail_htlc</c> it sends is
    /// recorded.
    /// </summary>
    private async Task<NLightningTestNode> StartNodeAsync(string name, bool attributing, CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name);
        _nodes.Add(node);
        if (attributing)
            node.ConfigureServices = services => DecorateSwitch(services);

        await node.StartAsync(ct);
        if (attributing)
            node.Services.GetRequiredService<IChannelManager>().OnResponseMessageReady += RecordFail;
        return node;
    }

    /// <summary>A fresh production node that advertises <c>option_attribution_data</c> (experimental).</summary>
    private async Task<NLightningTestNode> StartAdvertisingNodeAsync(string name, CancellationToken ct)
    {
        var node = await NLightningTestNode.CreateAsync(_fixture, name, configureNodeOptions: options =>
        {
            options.Features.AllowExperimentalFeatures = true;
            options.Features.OptionAttributionData = FeatureSupport.Optional;
        });
        _nodes.Add(node);
        await node.StartAsync(ct);
        return node;
    }

    private void RecordFail(object? _, ChannelResponseMessageEventArgs args)
    {
        if (args.ResponseMessage is UpdateFailHtlcMessage fail)
            _sentFails.Enqueue(fail);
    }

    private void DecorateSwitch(IServiceCollection services)
    {
        var registered = services.Last(d => d.ServiceType == typeof(IHtlcSwitch));
        services.Remove(registered);
        services.AddSingleton<IHtlcSwitch>(sp =>
        {
            var inner = registered.ImplementationFactory?.Invoke(sp) as IHtlcSwitch
                     ?? registered.ImplementationInstance as IHtlcSwitch
                     ?? (IHtlcSwitch)ActivatorUtilities.CreateInstance(sp, registered.ImplementationType!);
            return new AttributingFailSwitch(inner, sp, _failWithAttribution, _reportedHoldTimes);
        });
    }

    private async Task<OpenChannelClientSubscriptionResponse> OpenUsableChannelToLndAsync(NLightningTestNode node,
        LNDNodeConnection lnd, CancellationToken ct)
    {
        await node.FundWalletAsync(LightningMoney.Satoshis(2_000_000), AddressType.P2Wpkh, ct);
        var address = await node.ConnectToAsync(lnd, ct);
        var channel = await node.OpenChannelAsync(new OpenChannelClientRequest(address, s_capacity)
        {
            PushAmount = s_push,
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        }, ct);
        Console.WriteLine($"Opened channel {channel.ChannelId} ({channel.ChannelPoint()}) to {lnd.LocalAlias}");

        await Poll.UntilAsync(async () =>
        {
            var ours = await node.GetChannelAsync(channel.ChannelId, ct);
            var theirs = await LndTestHelpers.GetChannelByPointAsync(lnd, channel.ChannelPoint(), ct);
            if (ours.IsUsable() && ours.ShortChannelId is not null && theirs is { Active: true })
                return true;

            await ChainSync.MineAndWaitAsync(_fixture, 1, [lnd], [node], ct);
            return false;
        }, s_timeout, $"channel {channel.ChannelId} usable on both sides", ct);
        await ChainSync.WaitAllAtTipAsync(_fixture, [lnd], [node], ct);
        return channel;
    }

    /// <summary>
    /// A test decorator of the node's <see cref="IHtlcSwitch"/>: an incoming HTLC for one of the chosen hashes is held
    /// for <see cref="s_holdBeforeFailing"/>, then failed as the erring node with
    /// <c>incorrect_or_unknown_payment_details</c> and <c>attribution_data</c> carrying this node's hold time; every
    /// other event goes to the production switch.
    /// </summary>
    private sealed class AttributingFailSwitch(
        IHtlcSwitch inner,
        IServiceProvider services,
        ConcurrentDictionary<Hash, byte> hashes,
        ConcurrentQueue<uint> reportedHoldTimes) : IHtlcSwitch
    {
        private readonly ConcurrentDictionary<(Domain.Channels.ValueObjects.ChannelId, ulong), byte> _handled = new();

        public async Task HandleAsync(IChannelDomainEvent channelEvent, CancellationToken cancellationToken)
        {
            if (channelEvent is not IncomingHtlcLockedIn lockedIn || !hashes.ContainsKey(lockedIn.Htlc.PaymentHash))
            {
                await inner.HandleAsync(channelEvent, cancellationToken);
                return;
            }

            if (!_handled.TryAdd((lockedIn.ChannelId, lockedIn.HtlcId), 0))
                return;

            var htlc = lockedIn.Htlc;
            var result = await services.GetRequiredService<IncomingOnionProcessor>()
                                       .ProcessAsync(htlc.OnionRoutingPacket, htlc.PaymentHash, null);
            var secret = result.SharedSecretOrNull
                      ?? throw new InvalidOperationException($"The onion of HTLC {htlc.Id} could not be peeled");
            var operations = services.GetRequiredService<IChannelOperations>();
            await operations.RecordOnionSecretAsync(lockedIn.ChannelId, htlc.Id, secret, cancellationToken);

            await Task.Delay(s_holdBeforeFailing, cancellationToken);
            var holdTime = await operations.GetHoldTimeAsync(lockedIn.ChannelId, htlc.Id, cancellationToken);
            reportedHoldTimes.Enqueue(holdTime);
            var height = services.GetRequiredService<IBlockchainMonitor>().LastProcessedBlockHeight;
            var failure = FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(htlc.AmountMsat),
                                                                          height);
            var packet = services.GetRequiredService<IAttributionDataService>()
                                 .CreateErrorPacket(secret, failure, holdTime);
            Console.WriteLine($"Failing HTLC {htlc.Id} of channel {lockedIn.ChannelId} with attribution_data, hold "
                            + $"time {holdTime} (x 100 ms)");
            await operations.FailHtlcAsync(lockedIn.ChannelId, htlc.Id, packet, cancellationToken);
        }
    }
}
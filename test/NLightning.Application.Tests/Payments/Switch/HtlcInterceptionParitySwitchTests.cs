using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Onchain.Resolvers;
using Application.Payments.Interception;
using Channels.Harness;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Payments.Interception;
using Domain.Payments.Keysend;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;

/// <summary>
/// NL-1182 at the HTLC switch on three in-process nodes Alice → Bob → Carol (<see cref="ThreeNodeHarness"/>, real onions
/// and SQLite): Bob's interceptor resumes a forward modified (LND's <c>RESUME_MODIFIED</c>: amounts and wire custom
/// records), an interceptor is required (LND's <c>requireinterceptor</c>: a new forward fails, a replay waits for the
/// client), and a forward whose incoming channel is closing on chain is held for a settle whose preimage the resolvers
/// claim it with (LND's on-chain interception).
/// </summary>
public partial class HtlcInterceptionParitySwitchTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(40_000_000);
    private static readonly CustomRecord s_record = new(65_537, [0xCA, 0xFE]);

    [Fact]
    public async Task Given_AHeldForward_When_ResumedModified_Then_CarolGetsTheAmountAndRecordsAndThePaymentCompletes()
    {
        // Arrange: Carol's switch waits, so both HTLC records can be read before the payment settles
        await using var harness = await CreateAsync();
        var (hub, client, connection) = Connect(harness);
        using var _ = connection;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "modified", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Carol.SwitchSuspended = true;
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        var offered = Assert.Single(client.Offered);
        var outAmount = offered.OutgoingAmount + LightningMoney.MilliSatoshis(1);

        // Act: one msat more downstream, the policy judged against one msat more upstream (the fee unchanged)
        var result = await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                                            ForwardInterceptResolution.Modified(
                                                offered.IncomingAmount + LightningMoney.MilliSatoshis(1), outAmount,
                                                [s_record]));
        await harness.PumpAsync();

        // Assert: the add on the wire
        Assert.Equal(InterceptResolveResult.Resolved, result);
        var add = Assert.Single(harness.Sent, m => m is { From: "Bob", To: "Carol" }
                                                && m.Message is UpdateAddHtlcMessage).Message as UpdateAddHtlcMessage;
        Assert.Equal(outAmount, add!.Payload.Amount);
        Assert.Equal(s_record, Assert.Single(add.CustomRecords));

        // ... persisted on Bob's outgoing and Carol's incoming record (for retransmissions and in_wire_custom_records)
        var bobOutgoing = await StoredHtlcAsync(harness.Bob, ThreeNodeHarness.BobCarolChannelId, HtlcDirection.Outgoing);
        var carolIncoming = await StoredHtlcAsync(harness.Carol, ThreeNodeHarness.BobCarolChannelId,
                                                  HtlcDirection.Incoming);
        Assert.Equal(s_record, Assert.Single(WireCustomRecordCodec.Decode(bobOutgoing.WireCustomRecords)));
        Assert.Equal(s_record, Assert.Single(WireCustomRecordCodec.Decode(carolIncoming.WireCustomRecords)));

        // ... history records the interpretation override while accounting retains real channel custody
        var circuit = await harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository.GetByIncomingAsync(
                                                         ThreeNodeHarness.AliceBobChannelId, offered.IncomingHtlcId));
        Assert.Equal(offered.IncomingAmount + LightningMoney.MilliSatoshis(1), circuit!.IncomingAmount);
        Assert.Equal(offered.IncomingAmount, circuit.ActualIncomingAmount);
        Assert.Equal(outAmount, circuit.OutgoingAmount);

        // ... and Carol accepts the (over)payment
        harness.Carol.SwitchSuspended = false;
        var lockedIn = harness.Carol.Events.OfType<IncomingHtlcLockedIn>().Single();
        await harness.Carol.Switch.HandleAsync(lockedIn, TestContext.Current.CancellationToken);
        await harness.PumpAsync();
        Assert.Equal(invoice.Preimage!.Value, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        var accounting = Assert.Single(await harness.Bob.InScopeAsync(u => u.AccountingEventDbRepository.GetUnsealedAsync(1_000)));
        Assert.Equal(AccountingEventKind.ForwardSettled, accounting.Kind);
        Assert.Equal((long)(offered.IncomingAmount - outAmount).MilliSatoshi, accounting.AmountMsat);
    }

    [Fact]
    public async Task Given_AHeldForward_When_ResumedModifiedToSendMoreThanItReceives_Then_ItFailsBack()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var (hub, client, connection) = Connect(harness);
        using var _ = connection;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "too much", null,
                                                                      TestContext.Current.CancellationToken);
        var (_, onion) = await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash,
                                                                           invoice.PaymentSecret));
        await harness.PumpAsync();
        var offered = Assert.Single(client.Offered);

        // Act: the claimed incoming amount covers the fee, but the HTLC itself does not cover the outgoing amount
        var big = offered.IncomingAmount + LightningMoney.Satoshis(1_000);
        var result = await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                                            ForwardInterceptResolution.Modified(big, offered.IncomingAmount
                                                                                   + LightningMoney.MilliSatoshis(1),
                                                                                null));
        await harness.PumpAsync();

        // Assert
        Assert.Equal(InterceptResolveResult.Resolved, result);
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Carol" } && m.Message is UpdateAddHtlcMessage);
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = harness.Alice.Services.GetRequiredService<IFailureOnionService>()
                               .DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span);
        Assert.Equal(0, decrypted!.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryChannelFailure, decrypted.Code);
    }

    [Fact]
    public async Task Given_AnInterceptorRequiredAndNone_When_AlicePays_Then_BobFailsTheNewForward()
    {
        // Arrange
        await using var harness = await CreateAsync(require: true);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "required", null,
                                                                      TestContext.Current.CancellationToken);

        // Act
        var (_, onion) = await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash,
                                                                           invoice.PaymentSecret));
        await harness.PumpAsync();

        // Assert
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Carol" } && m.Message is UpdateAddHtlcMessage);
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = harness.Alice.Services.GetRequiredService<IFailureOnionService>()
                               .DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span);
        Assert.Equal(0, decrypted!.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryChannelFailure, decrypted.Code);
    }

    [Fact]
    public async Task Given_AnInterceptorRequiredAndAHeldForward_When_BobRestartsWithoutAClient_Then_TheReplayWaitsForOne()
    {
        // Arrange: the first start of Bob has a client, which holds the forward; the next start has none
        var clients = new List<RecordingClient>();
        var connectOnStart = true;
        await using var harness = await ThreeNodeHarness.CreateAsync(h => h.Bob.ConfigureServices = services =>
        {
            services.Configure<HtlcInterceptorSettings>(s => s.RequireInterceptor = true);
            services.AddSingleton(sp =>
            {
                var hub = ActivatorUtilities.CreateInstance<HtlcInterceptorHub>(sp);
                if (connectOnStart)
                {
                    var client = new RecordingClient();
                    clients.Add(client);
                    hub.Connect(client);
                }

                return hub;
            });
            services.AddSingleton<IHtlcForwardInterceptor>(sp => sp.GetRequiredService<HtlcInterceptorHub>());
        });
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "restart", null,
                                                                      TestContext.Current.CancellationToken);
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        Assert.Single(clients[^1].Offered);

        // Act: Bob "crashes" with the forward held and starts again with no client
        connectOnStart = false;
        await harness.RestartAsync(harness.Bob);
        await harness.ReconnectAsync(harness.Bob);
        await harness.PumpAsync();
        var hub = harness.Bob.Services.GetRequiredService<HtlcInterceptorHub>();
        await WaitUntilAsync(() => hub.HeldCount == 1);
        var heldWithoutClient = hub.HeldCount;
        var late = new RecordingClient();
        using var connection = hub.Connect(late);
        var offered = Assert.Single(late.Offered);
        await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                               ForwardInterceptResolution.Resume);
        await harness.PumpAsync();

        // Assert: nothing failed while no client was there; the late client's resume pays
        Assert.Equal(1, heldWithoutClient);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
    }

    [Fact]
    public async Task Given_AForwardOnAChannelClosingOnChain_When_TheInterceptorSettles_Then_ThePreimageIsKeptForTheClaim()
    {
        // Arrange: Bob's switch has not acted on the locked-in HTLC when his channel with Alice goes on chain
        await using var harness = await CreateAsync();
        var (hub, client, connection) = Connect(harness);
        using var _ = connection;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "on chain", null,
                                                                      TestContext.Current.CancellationToken);
        harness.Bob.SwitchSuspended = true;
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        harness.Bob.SwitchSuspended = false;
        var htlc = BobIncoming(harness);
        harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).UpdateState(ChannelState.OnchainResolving);
        var sentBefore = harness.Sent.Count;

        // Act: the resolver hands it to the switch (every round); the interceptor tries to fail it, then settles it
        await harness.Bob.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.AliceBobChannelId, htlc),
                                             TestContext.Current.CancellationToken);
        await harness.Bob.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.AliceBobChannelId, htlc),
                                             TestContext.Current.CancellationToken);
        var offered = Assert.Single(client.Offered);
        var fail = await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                                          new ForwardInterceptResolution(ForwardInterceptAction.Fail));
        var settle = await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                                            new ForwardInterceptResolution(ForwardInterceptAction.Settle,
                                                                           invoice.Preimage!.Value));
        await harness.PumpAsync();

        // Assert: offered once, settle only, with the incoming expiry as its deadline
        Assert.True(offered.IsOnChain);
        Assert.Equal(htlc.CltvExpiry, offered.AutoFailHeight);
        Assert.Equal(InterceptResolveResult.NotAllowedOnChain, fail);
        Assert.Equal(InterceptResolveResult.Resolved, settle);
        Assert.Equal(sentBefore, harness.Sent.Count);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(0, hub.HeldCount);

        // ... the preimage on the incoming record (memory and database), booked once, and the resolvers may claim it
        Assert.Equal(invoice.Preimage.Value, BobIncoming(harness).KnownPreimage);
        var stored = await StoredHtlcAsync(harness.Bob, ThreeNodeHarness.AliceBobChannelId, HtlcDirection.Incoming);
        Assert.Equal(invoice.Preimage.Value, stored.KnownPreimage);
        var settled = Assert.Single(await harness.Bob.InScopeAsync(u => u.AccountingEventDbRepository
                                                                         .GetUnsealedAsync(1_000)));
        Assert.Equal(AccountingEventKind.InterceptedHtlcSettled, settled.Kind);
        Assert.Equal(AccountingEventKeys.InterceptedHtlcSettled(ThreeNodeHarness.AliceBobChannelId, htlc.Id),
                     settled.EventKey);
        var claim = await harness.Bob.InScopeAsync(u => InterceptorClaims.GetSettledPreimageAsync(
                                                       u, ThreeNodeHarness.AliceBobChannelId, stored));
        Assert.Equal((byte[])invoice.Preimage.Value, claim);

        // ... and a later round offers nothing again
        await harness.Bob.Switch.HandleAsync(new IncomingHtlcLockedIn(ThreeNodeHarness.AliceBobChannelId, stored),
                                             TestContext.Current.CancellationToken);
        Assert.Single(client.Offered);
    }

    [Fact]
    public async Task Given_AForwardHeldOffChain_When_ItsChannelGoesOnChainBeforeTheResume_Then_OnlyASettleIsLeft()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var (hub, client, connection) = Connect(harness);
        using var _ = connection;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "promoted", null,
                                                                      TestContext.Current.CancellationToken);
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        var offered = Assert.Single(client.Offered);
        harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).UpdateState(ChannelState.OnchainResolving);

        // Act
        var resume = await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                                            ForwardInterceptResolution.Resume);
        var settle = await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                                            new ForwardInterceptResolution(ForwardInterceptAction.Settle,
                                                                           invoice.Preimage!.Value));
        await harness.PumpAsync();

        // Assert: nothing forwarded from the channel on chain; re-offered on chain; the settle kept for the claim
        Assert.Equal(InterceptResolveResult.NotAllowedOnChain, resume);
        Assert.Equal(InterceptResolveResult.Resolved, settle);
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Carol" } && m.Message is UpdateAddHtlcMessage);
        Assert.True(client.Offered.Last().IsOnChain);
        Assert.Equal(invoice.Preimage.Value, BobIncoming(harness).KnownPreimage);
    }

    private static Task<ThreeNodeHarness> CreateAsync(bool require = false, ulong maxDustMsat = 50_000_000) =>
        ThreeNodeHarness.CreateAsync(h =>
        {
            h.Bob.Options.MaxDustHtlcExposureMsat = maxDustMsat;
            h.Bob.ConfigureServices = services =>
            {
                if (require)
                    services.Configure<HtlcInterceptorSettings>(s => s.RequireInterceptor = true);
                services.AddSingleton<HtlcInterceptorHub>();
                services.AddSingleton<IHtlcForwardInterceptor>(sp => sp.GetRequiredService<HtlcInterceptorHub>());
            };
        });

    private static (HtlcInterceptorHub Hub, RecordingClient Client, IDisposable Connection) Connect(
        ThreeNodeHarness harness)
    {
        var hub = harness.Bob.Services.GetRequiredService<HtlcInterceptorHub>();
        var client = new RecordingClient();
        return (hub, client, hub.Connect(client));
    }

    private static HtlcRecord BobIncoming(ThreeNodeHarness harness) =>
        harness.Bob.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!.Htlcs.Values
               .Single(h => h.Direction == HtlcDirection.Incoming);

    private static Task<HtlcRecord> StoredHtlcAsync(SwitchNode node, ChannelId channelId, HtlcDirection direction)
    {
        var channel = node.Channel(channelId);
        return node.InScopeAsync(async u => (await u.ChannelStateDbRepository.LoadAsync(
                                                 channelId, channel.Commitments!.Params))!
                                           .Commitments.Htlcs.Values.Single(h => h.Direction == direction));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class RecordingClient : IHtlcInterceptorClient
    {
        public ConcurrentQueue<InterceptedForward> Offered { get; } = new();

        public bool TryOffer(InterceptedForward forward)
        {
            Offered.Enqueue(forward);
            return true;
        }
    }
}
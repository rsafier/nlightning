using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Payments.Interception;
using Channels.Harness;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interception;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;

/// <summary>
/// Forward interception (LND's HtlcInterceptor, NL-1183) in <c>HtlcSwitch</c> on three in-process nodes Alice → Bob →
/// Carol (<see cref="ThreeNodeHarness"/>): Bob's interceptor holds the forward and its resolution fails, resumes or
/// settles it; a disconnect resumes it; a restart offers it again.
/// </summary>
public class HtlcInterceptionSwitchTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(40_000_000);

    [Fact]
    public async Task Given_AnInterceptorAtBob_When_AlicePays_Then_TheForwardIsHeldAndItsFailReachesAlice()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var (hub, client, connection) = Connect(harness);
        using var _ = connection;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "held", null,
                                                                      TestContext.Current.CancellationToken);

        // Act: the forward is held, nothing reaches Carol
        var (_, onion) = await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash,
                                                                           invoice.PaymentSecret));
        await harness.PumpAsync();
        var offered = Assert.Single(client.Offered);
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Carol" } && m.Message is UpdateAddHtlcMessage);
        Assert.Equal(1, hub.HeldCount);
        var result = await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                                            new ForwardInterceptResolution(ForwardInterceptAction.Fail));
        await harness.PumpAsync();

        // Assert
        Assert.Equal(InterceptResolveResult.Resolved, result);
        Assert.Equal(ThreeNodeHarness.AliceBobScid, offered.IncomingShortChannelId);
        Assert.Equal(ThreeNodeHarness.BobCarolScid, offered.OutgoingRequestedShortChannelId);
        Assert.Equal(s_amount, offered.OutgoingAmount);
        Assert.Equal(offered.IncomingExpiry - 19, offered.AutoFailHeight);
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = harness.Alice.Services.GetRequiredService<IFailureOnionService>()
                               .DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryChannelFailure, decrypted.Code);
        Assert.Equal(0, hub.HeldCount);
    }

    [Fact]
    public async Task Given_AHeldForward_When_Resumed_Then_ThePaymentCompletes()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var (hub, client, connection) = Connect(harness);
        using var _ = connection;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "resumed", null,
                                                                      TestContext.Current.CancellationToken);
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        var offered = Assert.Single(client.Offered);

        // Act
        await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                               ForwardInterceptResolution.Resume);
        await harness.PumpAsync();

        // Assert
        Assert.Equal(invoice.Preimage!.Value, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                           .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
    }

    [Fact]
    public async Task Given_AHeldForward_When_SettledWithThePreimage_Then_AliceIsPaidAndCarolSeesNothing()
    {
        // Arrange: the interceptor knows the preimage (a JIT-LSP that receives itself)
        await using var harness = await CreateAsync();
        var (hub, client, connection) = Connect(harness);
        using var _ = connection;
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "settled", null,
                                                                      TestContext.Current.CancellationToken);
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        var offered = Assert.Single(client.Offered);

        // Act: a wrong preimage first (refused, still held), then the right one
        var wrong = await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                                           new ForwardInterceptResolution(ForwardInterceptAction.Settle,
                                                                          new byte[32]));
        var right = await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                                           new ForwardInterceptResolution(ForwardInterceptAction.Settle,
                                                                          invoice.Preimage!.Value));
        await harness.PumpAsync();

        // Assert
        Assert.Equal(InterceptResolveResult.PreimageMismatch, wrong);
        Assert.Equal(InterceptResolveResult.Resolved, right);
        Assert.Equal(invoice.Preimage.Value, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Carol" } && m.Message is UpdateAddHtlcMessage);
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                           .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Open, stored!.Status);
    }

    [Fact]
    public async Task Given_AHeldForward_When_TheInterceptorDisconnects_Then_ItIsResumed()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var (hub, client, connection) = Connect(harness);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "released", null,
                                                                      TestContext.Current.CancellationToken);
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        Assert.Single(client.Offered);

        // Act: the resumes run in the background
        connection.Dispose();
        for (var round = 0; round < 500 && harness.Alice.PaymentHandler.Fulfilled.Count == 0; round++)
        {
            await harness.PumpAsync();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        // Assert
        Assert.False(hub.IsActive);
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
    }

    [Fact]
    public async Task Given_AHeldForward_When_BobRestarts_Then_TheReplayOffersItAgainAndItsResolutionHolds()
    {
        // Arrange: each start of Bob connects its interceptor as its hub is made, before any replay
        var clients = new List<RecordingClient>();
        await using var harness = await ThreeNodeHarness.CreateAsync(h => h.Bob.ConfigureServices = services =>
        {
            services.AddSingleton(sp =>
            {
                var hub = ActivatorUtilities.CreateInstance<HtlcInterceptorHub>(sp);
                var client = new RecordingClient();
                clients.Add(client);
                hub.Connect(client);
                return hub;
            });
            services.AddSingleton<IHtlcForwardInterceptor>(sp => sp.GetRequiredService<HtlcInterceptorHub>());
        });
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "restart", null,
                                                                      TestContext.Current.CancellationToken);
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();
        Assert.Single(clients[^1].Offered);

        // Act: Bob "crashes" with the forward held (memory only) and starts again; the lock-in's replay offers it to
        // the interceptor of the new start, which resumes it
        await harness.RestartAsync(harness.Bob);
        await harness.ReconnectAsync(harness.Bob);
        await harness.PumpAsync();
        var hub = harness.Bob.Services.GetRequiredService<HtlcInterceptorHub>();
        var again = clients[^1];
        await WaitUntilAsync(() => !again.Offered.IsEmpty);
        var offered = again.Offered.First();
        await hub.ResolveAsync(offered.IncomingShortChannelId, offered.IncomingHtlcId,
                               ForwardInterceptResolution.Resume);
        await harness.PumpAsync();

        // Assert
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Single(harness.Sent, m => m is { From: "Bob", To: "Carol" } && m.Message is UpdateAddHtlcMessage);
    }

    private static Task<ThreeNodeHarness> CreateAsync() =>
        ThreeNodeHarness.CreateAsync(h => h.Bob.ConfigureServices = services =>
        {
            // One hub per start of Bob, as in a process (held forwards are memory only)
            services.AddSingleton<HtlcInterceptorHub>();
            services.AddSingleton<IHtlcForwardInterceptor>(sp => sp.GetRequiredService<HtlcInterceptorHub>());
        });

    private static (HtlcInterceptorHub Hub, RecordingClient Client, IDisposable Connection) Connect(
        ThreeNodeHarness harness)
    {
        var hub = harness.Bob.Services.GetRequiredService<HtlcInterceptorHub>();
        var client = new RecordingClient();
        return (hub, client, hub.Connect(client));
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
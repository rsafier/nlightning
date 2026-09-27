using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Offers.Send;

using Application.Gossip.Interfaces;
using Application.Payments.Invoices;
using Application.Payments.Send;
using Channels.Harness;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Models;

/// <summary>
/// BOLT 12 plan B4-T1: <see cref="PaymentService.PayBlindedAsync"/> honours <see cref="PayBlindedRequest.PayeeNodeId"/>,
/// <see cref="PayBlindedRequest.AllowMpp"/> and <see cref="PayBlindedRequest.Bolt12"/>, and pays a path whose
/// introduction node is the payer (B12-PAY-02), all through the production switches of
/// <see cref="ThreeNodeHarness"/> (Alice - Bob - Carol, real Sphinx, blinding crypto, commitments and SQLite).
/// </summary>
public class PayBlindedTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(40_000_321);

    [Fact]
    public async Task Given_APayeeIdAndBolt12Details_When_AlicePays_Then_TheRowNamesThePayee()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "bolt12", null, ct);
        var paths = await BuildPathsAsync(harness, invoice.Preimage, invoice.MinFinalCltvExpiry, 1);
        var details = new Bolt12PaymentDetails("lno1test", new byte[] { 1, 2, 3 }, new byte[] { 9, 9 }, "a note");
        var request = new PayBlindedRequest(invoice.PaymentHash, s_amount, paths)
        {
            PayeeNodeId = harness.Carol.NodeId,
            Bolt12 = details
        };

        // Act
        var paying = Payments(harness.Alice).PayBlindedAsync(request, Options(), ct);
        await harness.PumpAsync();
        var result = await paying;

        // Assert
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        Assert.Equal(harness.Carol.NodeId, result.Payment.PayeeNodeId);
        Assert.Equal(harness.Carol.NodeId, result.Payment.Route[^1].NodeId);
        // The row's BOLT 12 columns come with lane B12-C's migration (AddBolt12Offers); until then SQLite drops them
    }

    [Fact]
    public async Task Given_TheSecondPathIsCheaper_When_Paying_Then_ItIsUsedAndTheRowStillNamesThePayee()
    {
        // Arrange: the row's payee is path 0's last blinded id; before, a route over path 1 could not be stored (its
        // last hop was another blinded id than the payee)
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "two paths", null, ct);
        var paths = await BuildPathsAsync(harness, invoice.Preimage, invoice.MinFinalCltvExpiry, 2);
        var dearer = paths[0] with { PayInfo = paths[0].PayInfo with { FeeBaseMsat = paths[0].PayInfo.FeeBaseMsat + 500 } };

        // Act
        var paying = Payments(harness.Alice).PayBlindedAsync(
            new PayBlindedRequest(invoice.PaymentHash, s_amount, [dearer, paths[1]]), Options(), ct);
        await harness.PumpAsync();
        var result = await paying;

        // Assert
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        Assert.Equal(paths[0].Path.Hops[^1].BlindedNodeId, result.Payment.PayeeNodeId);
        Assert.Equal(paths[0].Path.Hops[^1].BlindedNodeId, result.Payment.Route[^1].NodeId);
        Assert.Equal(paths[1].PayInfo.ComputeFeeMsat(s_amount.MilliSatoshi), result.Payment.Fee.MilliSatoshi);
    }

    [Fact]
    public async Task Given_PathsTooSmallForTheAmountAndMppAllowed_When_Paying_Then_TwoPartsSettleTheInvoice()
    {
        // Arrange: each path takes at most 60 % of the amount, so one HTLC cannot carry it
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "split", null, ct);
        var paths = (await BuildPathsAsync(harness, invoice.Preimage, invoice.MinFinalCltvExpiry, 2))
                   .Select(p => p with { PayInfo = p.PayInfo with { HtlcMaximumMsat = s_amount.MilliSatoshi * 6 / 10 } })
                   .ToList();
        var request = new PayBlindedRequest(invoice.PaymentHash, s_amount, paths)
        {
            PayeeNodeId = harness.Carol.NodeId,
            AllowMpp = true
        };

        // Act
        var paying = Payments(harness.Alice).PayBlindedAsync(request, Options(), ct);
        await harness.PumpAsync();
        var result = await paying;

        // Assert
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        Assert.Equal(2, result.Parts);
        Assert.Equal(2, harness.Carol.Received.Count(m => m is UpdateAddHtlcMessage));
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
    }

    [Fact]
    public async Task Given_PathsTooSmallAndNoMpp_When_Paying_Then_FailedWithoutAnHtlc()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "no split", null, ct);
        var paths = (await BuildPathsAsync(harness, invoice.Preimage, invoice.MinFinalCltvExpiry, 2))
                   .Select(p => p with { PayInfo = p.PayInfo with { HtlcMaximumMsat = s_amount.MilliSatoshi * 6 / 10 } })
                   .ToList();

        // Act
        var result = await Payments(harness.Alice).PayBlindedAsync(
            new PayBlindedRequest(invoice.PaymentHash, s_amount, paths), Options(TimeSpan.FromSeconds(5)), ct);

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(0, result.Attempts);
        Assert.Contains("does not accept a split payment", result.Payment.FailureReason);
    }

    [Fact]
    public async Task Given_BobIsTheIntroductionNode_When_BobPays_Then_HeSendsToCarolWithTheNextPathKey()
    {
        // Arrange: B12-PAY-02, Carol's only peer is Bob, so her path starts at him
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync(bobPays: true);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "introduced by the payer", null, ct);
        var path = Assert.Single(await BuildPathsAsync(harness, invoice.Preimage, invoice.MinFinalCltvExpiry, 1));
        Assert.Equal(harness.Bob.NodeId, path.Path.FirstNodeId);
        var request = new PayBlindedRequest(invoice.PaymentHash, s_amount, [path]) { PayeeNodeId = harness.Carol.NodeId };

        // Act
        var paying = Payments(harness.Bob).PayBlindedAsync(request, Options(), ct);
        await harness.PumpAsync();
        var result = await paying;

        // Assert: one HTLC straight to Carol, carrying the next path_key; Carol settled; Bob paid no one a fee beyond
        // what his own hop's payment_relay leaves
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        Assert.Equal(invoice.Preimage, result.Payment.Preimage);
        var add = Assert.IsType<UpdateAddHtlcMessage>(Assert.Single(harness.Carol.Received, m => m is UpdateAddHtlcMessage));
        Assert.NotNull(add.BlindedPathTlv);
        Assert.NotEqual(path.Path.FirstPathKey, add.BlindedPathTlv!.PathKey);
        Assert.True(add.Payload.Amount >= s_amount);
        Assert.True(result.Payment.Fee.MilliSatoshi <= path.PayInfo.ComputeFeeMsat(s_amount.MilliSatoshi));
        Assert.DoesNotContain(harness.Alice.Received, m => m is UpdateAddHtlcMessage);
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
    }

    [Fact]
    public async Task Given_APathThatEndsAtThePayer_When_Paying_Then_ItIsRefusedWithoutAnHtlc()
    {
        // Arrange: a path Bob built to himself, paid by Bob
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync(bobPays: true);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "to Carol", null, ct);
        var path = Assert.Single(await BuildPathsAsync(harness, invoice.Preimage, invoice.MinFinalCltvExpiry, 1));
        var onlyBob = path with { Path = path.Path with { Hops = [path.Path.Hops[0]] } };

        // Act
        var result = await Payments(harness.Bob).PayBlindedAsync(
            new PayBlindedRequest(invoice.PaymentHash, s_amount, [onlyBob]), Options(TimeSpan.FromSeconds(5)), ct);

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(0, result.Attempts);
        Assert.Contains("cannot pay itself", result.Payment.FailureReason);
    }

    [Fact]
    public async Task Given_OurOwnNodeIdAsPayee_When_Paying_Then_ArgumentException()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "self", null, ct);
        var paths = await BuildPathsAsync(harness, invoice.Preimage, invoice.MinFinalCltvExpiry, 1);

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() => Payments(harness.Alice).PayBlindedAsync(
            new PayBlindedRequest(invoice.PaymentHash, s_amount, paths) { PayeeNodeId = harness.Alice.NodeId },
            Options(), ct));
    }

    private static IPaymentService Payments(SwitchNode node) => node.Services.GetRequiredService<IPaymentService>();

    private static PayInvoiceOptions Options(TimeSpan? timeout = null) =>
        new() { Timeout = timeout ?? TimeSpan.FromSeconds(30) };

    private static async Task<List<BlindedPaymentPath>> BuildPathsAsync(ThreeNodeHarness harness, Secret preimage,
                                                                        ushort minFinalCltvExpiryDelta, int count)
    {
        // Carol holds Bob's signed channel_update of their channel (the gossip exchange the harness leaves out)
        Assert.True(harness.Bob.Services.GetRequiredService<IChannelUpdateService>()
                           .TryGetLocalChannelUpdate(ThreeNodeHarness.BobCarolChannelId, out var bobsUpdate));
        harness.Carol.Services.GetRequiredService<IChannelUpdateService>()
               .HandleRemoteChannelUpdate(harness.Bob.NodeId, bobsUpdate!);

        var builder = harness.Carol.Services.GetRequiredService<BlindedPathBuilder>();
        var paths = new List<BlindedPaymentPath>();
        for (var i = 0; i < count; i++)
            paths.AddRange(await builder.BuildAsync(
                               new BlindedPathRequest(preimage, s_amount, minFinalCltvExpiryDelta,
                                                      ThreeNodeHarness.BlockHeight, IncludePrivateChannels: true),
                               TestContext.Current.CancellationToken));
        Assert.Equal(count, paths.Count);
        return paths;
    }

    private static async Task<ThreeNodeHarness> CreateAsync(bool bobPays = false) =>
        await ThreeNodeHarness.CreateAsync(h =>
        {
            foreach (var node in h.Nodes)
                node.Options.Features.OptionRouteBlinding = FeatureSupport.Optional;

            h.Alice.ConfigureServices = services => services.AddPaymentSendServices();
            if (bobPays)
                h.Bob.ConfigureServices = services => services.AddPaymentSendServices();
        });
}
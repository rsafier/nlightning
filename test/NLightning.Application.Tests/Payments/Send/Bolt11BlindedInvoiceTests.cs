using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Application.Tests.Payments.Send;

using Application.Gossip.Interfaces;
using Application.Payments.Invoices;
using Application.Payments.Routing;
using Application.Payments.Send;
using Bolt11.Models;
using Channels.Harness;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Models;

/// <summary>
/// NL-440: bLIP 39 BOLT 11 invoices with blinded paths (tagged field 20) end to end on <see cref="ThreeNodeHarness"/>
/// (Alice - Bob - Carol, production switches, Sphinx, blinding crypto and SQLite): Carol's invoice made by the production
/// <see cref="InvoiceService"/> with <see cref="InvoiceOptions.BlindedPaths"/>, paid by Alice's
/// <see cref="PaymentService.PayInvoiceAsync(string, LightningMoney?, PayInvoiceOptions, CancellationToken)"/> from
/// the decoded paths alone (no <c>s</c>, no <c>r</c>, an ephemeral signing key), with Carol's dummy hops, and split over
/// two paths when the invoice sets <c>basic_mpp</c>.
/// </summary>
public class Bolt11BlindedInvoiceTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(40_000_321);

    [Fact]
    public async Task Given_CarolsBlindedBolt11Invoice_When_AlicePaysIt_Then_PaidOverThePathAndCarolSettles()
    {
        // Arrange: bLIP 39 "An invoice containing the `b` field type: MUST not contain the `r` field type. MUST not
        // contain the `s` field type ... SHOULD sign the invoice with a private key that is not the same as their
        // public node ID"
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync(carolBlindedInvoices: true);
        HandBobsUpdateToCarol(harness);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "blinded bolt11", null, ct);
        var decoded = Invoice.Decode(invoice.Bolt11, harness.Carol.Options.BitcoinNetwork);
        Assert.Null(decoded.PaymentSecret);
        Assert.Empty(decoded.RouteHints);
        Assert.NotEqual(harness.Carol.NodeId, new CompactPubKey(decoded.PayeePubKey!.ToBytes()));
        var path = Assert.Single(decoded.BlindedPaymentPaths);
        Assert.Equal(harness.Bob.NodeId, path.Path.FirstNodeId);
        Assert.Equal(2 + InvoiceOptions.DefaultBlindedPathDummyHops, path.Path.Hops.Count);

        // Act
        var paying = Payments(harness.Alice).PayInvoiceAsync(invoice.Bolt11!, null, Options(), ct);
        await harness.PumpAsync();
        var result = await paying;

        // Assert
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        Assert.Equal(invoice.Preimage!.Value, result.Payment.Preimage);
        Assert.Equal(path.PayInfo.ComputeFeeMsat(s_amount.MilliSatoshi), result.Payment.Fee.MilliSatoshi);
        var add = Assert.IsType<UpdateAddHtlcMessage>(Assert.Single(harness.Carol.Received,
                                                                    m => m is UpdateAddHtlcMessage));
        Assert.NotNull(add.BlindedPathTlv);
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);

        // NL-526: Carol's invoice records what her HTLC carried, the amount plus what her own dummy hop kept (BOLT 4
        // lets a final node take more than the amount): above the amount, and at most the path's aggregated fee less
        // what Bob, the introduction node, kept for his hop
        Assert.Equal(add.Payload.Amount, stored.AmountReceived);
        Assert.True(harness.Bob.Services.GetRequiredService<IChannelUpdateService>()
                           .TryGetLocalChannelUpdate(ThreeNodeHarness.BobCarolChannelId, out var bobsUpdate));
        var bobsFee = new BlindedPayInfo(bobsUpdate!.Payload.FeeBaseMsat, bobsUpdate.Payload.FeeProportionalMillionths, 0, 0, 0)
                     .ComputeFeeMsat(s_amount.MilliSatoshi);
        var dummyHopsFee = stored.AmountReceived!.MilliSatoshi - s_amount.MilliSatoshi;
        Assert.InRange(dummyHopsFee, 1UL, path.PayInfo.ComputeFeeMsat(s_amount.MilliSatoshi) - bobsFee);
    }

    [Fact]
    public async Task Given_TwoPathsTooSmallAndBasicMpp_When_AlicePaysTheInvoice_Then_TwoPartsSettleIt()
    {
        // Arrange: BOLT 4 "Basic Multi-Part Payments" over bLIP 39 paths; each path takes 60 % of the amount
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        HandBobsUpdateToCarol(harness);
        var record = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "split", null, ct);
        var paths = (await BuildPathsAsync(harness, record.Preimage!.Value, record.MinFinalCltvExpiry, 2))
                   .Select(p => p with { PayInfo = p.PayInfo with { HtlcMaximumMsat = s_amount.MilliSatoshi * 6 / 10 } })
                   .ToList();
        var bolt11 = EncodeBlindedInvoice(harness, record.PaymentHash, paths, basicMpp: true);

        // Act
        var paying = Payments(harness.Alice).PayInvoiceAsync(bolt11, null, Options(), ct);
        await harness.PumpAsync();
        var result = await paying;

        // Assert
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        Assert.Equal(2, result.Parts);
        Assert.Equal(2, harness.Carol.Received.Count(m => m is UpdateAddHtlcMessage));
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(record.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
    }

    [Fact]
    public async Task Given_TwoPathsTooSmallWithoutBasicMpp_When_AlicePaysTheInvoice_Then_FailedWithoutAnHtlc()
    {
        // Arrange: BOLT 4 "the payer MUST NOT split" without basic_mpp
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        HandBobsUpdateToCarol(harness);
        var record = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "no split", null, ct);
        var paths = (await BuildPathsAsync(harness, record.Preimage!.Value, record.MinFinalCltvExpiry, 2))
                   .Select(p => p with { PayInfo = p.PayInfo with { HtlcMaximumMsat = s_amount.MilliSatoshi * 6 / 10 } })
                   .ToList();
        var bolt11 = EncodeBlindedInvoice(harness, record.PaymentHash, paths, basicMpp: false);

        // Act
        var result = await Payments(harness.Alice).PayInvoiceAsync(bolt11, null, Options(TimeSpan.FromSeconds(5)), ct);

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(0, result.Attempts);
        Assert.Contains("does not accept a split payment", result.Payment.FailureReason);
    }

    [Fact]
    public async Task Given_BobIntroducesTheInvoicePath_When_BobPaysIt_Then_HeSendsToCarolDirectly()
    {
        // Arrange: a path whose introduction node is the payer (NL-440 gap 2) read from a bLIP 39 invoice
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync(carolBlindedInvoices: true, bobPays: true);
        HandBobsUpdateToCarol(harness);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "introduced by Bob", null, ct);

        // Act
        var paying = Payments(harness.Bob).PayInvoiceAsync(invoice.Bolt11!, null, Options(), ct);
        await harness.PumpAsync();
        var result = await paying;

        // Assert
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        var add = Assert.IsType<UpdateAddHtlcMessage>(Assert.Single(harness.Carol.Received,
                                                                    m => m is UpdateAddHtlcMessage));
        Assert.NotNull(add.BlindedPathTlv);
        Assert.DoesNotContain(harness.Alice.Received, m => m is UpdateAddHtlcMessage);
    }

    [Fact]
    public async Task Given_NoPeerUpdate_When_CarolCreatesABlindedInvoice_Then_RefusedAndNothingStored()
    {
        // Arrange: no channel_update from Bob, so no path can be built
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync(carolBlindedInvoices: true);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "no path", null, ct));
        var listed = await harness.Carol.Invoices.ListInvoicesAsync(0, 10, ct);
        Assert.Empty(listed);
    }

    private static string EncodeBlindedInvoice(ThreeNodeHarness harness, Hash paymentHash,
                                               IReadOnlyList<BlindedPaymentPath> paths, bool basicMpp)
    {
        var invoice = new Invoice(s_amount, "hand-made bLIP 39", PaymentTarget.FromWireBytes(paymentHash),
                                  PaymentTarget.FromWireBytes(Enumerable.Repeat((byte)9, 32).ToArray()), harness.Carol.Options.BitcoinNetwork)
        {
            MinFinalCltvExpiry = 18
        };
        if (basicMpp)
        {
            var features = FeatureSet.DeserializeFromBytes([0x41, 0x00]);
            features.SetFeature(Feature.BasicMpp, false);
            invoice.Features = features;
        }

        foreach (var path in paths)
            invoice.AddBlindedPaymentPath(path);
        invoice.RemovePaymentSecret();
        using var ephemeral = new Key();
        return invoice.Encode(ephemeral);
    }

    private static void HandBobsUpdateToCarol(ThreeNodeHarness harness)
    {
        // Carol holds Bob's signed channel_update of their channel (the gossip exchange the harness leaves out)
        Assert.True(harness.Bob.Services.GetRequiredService<IChannelUpdateService>()
                           .TryGetLocalChannelUpdate(ThreeNodeHarness.BobCarolChannelId, out var bobsUpdate));
        Assert.True(harness.Carol.Services.GetRequiredService<IChannelUpdateService>()
                           .HandleRemoteChannelUpdate(harness.Bob.NodeId, bobsUpdate!));
    }

    private static async Task<List<BlindedPaymentPath>> BuildPathsAsync(ThreeNodeHarness harness, Secret preimage,
                                                                        ushort minFinalCltvExpiryDelta, int count)
    {
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

    private static IPaymentService Payments(SwitchNode node) => node.Services.GetRequiredService<IPaymentService>();

    private static PayInvoiceOptions Options(TimeSpan? timeout = null) =>
        new() { Timeout = timeout ?? TimeSpan.FromSeconds(30) };

    private static async Task<ThreeNodeHarness> CreateAsync(bool carolBlindedInvoices = false, bool bobPays = false) =>
        await ThreeNodeHarness.CreateAsync(h =>
        {
            foreach (var node in h.Nodes)
                node.Options.Features.OptionRouteBlinding = FeatureSupport.Optional;
            h.Alice.ConfigureServices = services => services.AddPaymentSendServices();
            if (bobPays)
                h.Bob.ConfigureServices = services => services.AddPaymentSendServices();
            if (carolBlindedInvoices)
                h.Carol.ConfigureServices = services =>
                    services.Configure<InvoiceOptions>(o => o.BlindedPaths = true);
        });
}
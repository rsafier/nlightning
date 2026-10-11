using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Send;

using Application.Gossip.Interfaces;
using Application.Payments.Invoices;
using Application.Payments.Send;
using Channels.Harness;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;

/// <summary>
/// ONION M5 step 2 in-process: Carol makes a blinded path to herself with the production
/// <see cref="BlindedPathBuilder"/> (Bob the introduction node, from her channel to him and his policy), and Alice pays
/// it with the production <see cref="PaymentService.PayBlindedAsync"/> through the production switches of Bob and Carol
/// (<see cref="ThreeNodeHarness"/>: real Sphinx, blinding crypto, commitments and SQLite).
/// </summary>
public class BlindedSendThreeNodeTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(40_000_321);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Given_CarolsBlindedPathThroughBob_When_AlicePaysIt_Then_PaymentSucceedsAndCarolSettles(
        int dummyHops)
    {
        // Arrange: BOLT 4 "MAY add additional dummy hops at the end of the path (which it will ignore on receipt)" and
        // "SHOULD add padding data to ensure all encrypted_data_tlv[i] have the same length"
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "blinded receive", null, ct);
        var paths = await BuildPathsAsync(harness, invoice.Preimage!.Value, invoice.MinFinalCltvExpiry, dummyHops);
        var path = Assert.Single(paths);
        Assert.Equal(harness.Bob.NodeId, path.Path.FirstNodeId);
        Assert.Equal(2 + dummyHops, path.Path.Hops.Count);
        Assert.Single(path.Path.Hops.Select(h => h.EncryptedRecipientData.Length).Distinct());
        var payments = harness.Alice.Services.GetRequiredService<IPaymentService>();

        // Act
        var paying = payments.PayBlindedAsync(new PayBlindedRequest(invoice.PaymentHash, s_amount, paths),
                                              new PayInvoiceOptions { Timeout = TimeSpan.FromSeconds(30) }, ct);
        await harness.PumpAsync();
        var result = await paying;

        // Assert: succeeded with the path's fee; Bob forwarded with the next path_key; Carol settled
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        Assert.Equal(invoice.Preimage!.Value, result.Payment.Preimage);
        Assert.Equal(path.PayInfo.ComputeFeeMsat(s_amount.MilliSatoshi), result.Payment.Fee.MilliSatoshi);
        var forwarded = Assert.IsType<UpdateAddHtlcMessage>(
            Assert.Single(harness.Carol.Received, m => m is UpdateAddHtlcMessage));
        Assert.NotNull(forwarded.BlindedPathTlv);
        if (dummyHops == 0)
            Assert.Equal(s_amount, forwarded.Payload.Amount);
        else
            Assert.True(forwarded.Payload.Amount > s_amount, "Carol keeps the dummy hops' relay fees");
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
    }

    [Fact]
    public async Task Given_TwoDummyHops_When_CarolBuildsAPath_Then_EachHopDecryptsToTheBolt4Layout()
    {
        // Arrange: BOLT 4 writer: dummy hops "at the end of the path", every encrypted_data_tlv padded to one length
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "layout", null, ct);

        // Act
        var path = Assert.Single(await BuildPathsAsync(harness, invoice.Preimage!.Value, invoice.MinFinalCltvExpiry, 2));

        // Assert: Bob's hop names the channel; Carol's two relays name Carol with Bob's relay policy and a chained
        // max_cltv_expiry; the last hop carries the path_id; the pay info aggregates three relays
        var bobBlinding = harness.Bob.Services.GetRequiredService<IRouteBlindingService>();
        var carolBlinding = harness.Carol.Services.GetRequiredService<IRouteBlindingService>();
        var bob = bobBlinding.UnblindAsLocalNode(path.Path.FirstPathKey, path.Path.Hops[0].EncryptedRecipientData);
        Assert.NotNull(bob.RecipientData.ShortChannelId);
        var relay = bob.RecipientData.PaymentRelay!;
        var pathKey = bob.NextPathKey;
        var maxCltv = bob.RecipientData.PaymentConstraints!.MaxCltvExpiry;
        for (var i = 1; i <= 2; i++)
        {
            var dummy = carolBlinding.UnblindAsLocalNode(pathKey, path.Path.Hops[i].EncryptedRecipientData);
            Assert.Equal(harness.Carol.NodeId, dummy.RecipientData.NextNodeId);
            Assert.Null(dummy.RecipientData.ShortChannelId);
            Assert.Null(dummy.RecipientData.PathId);
            Assert.Equal(relay, dummy.RecipientData.PaymentRelay);
            Assert.Equal(maxCltv - relay.CltvExpiryDelta, dummy.RecipientData.PaymentConstraints!.MaxCltvExpiry);
            maxCltv = dummy.RecipientData.PaymentConstraints.MaxCltvExpiry;
            pathKey = dummy.NextPathKey;
        }

        var final = carolBlinding.UnblindAsLocalNode(pathKey, path.Path.Hops[3].EncryptedRecipientData);
        Assert.True(BlindedPathId.Matches(final.RecipientData.PathId!.Value.Span, invoice.Preimage!.Value));
        Assert.Null(final.RecipientData.NextNodeId);
        Assert.Equal(maxCltv - relay.CltvExpiryDelta, final.RecipientData.PaymentConstraints!.MaxCltvExpiry);
        Assert.Single(path.Path.Hops.Select(h => h.EncryptedRecipientData.Length).Distinct());
        var (feeBase, feeProportional, cltvDelta) = BlindedPayInfo.Aggregate([relay, relay, relay],
                                                                              invoice.MinFinalCltvExpiry);
        Assert.Equal(feeBase, path.PayInfo.FeeBaseMsat);
        Assert.Equal(feeProportional, path.PayInfo.FeeProportionalMillionths);
        Assert.Equal(cltvDelta, path.PayInfo.CltvExpiryDelta);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(InvoiceOptions.MaxBlindedPathDummyHops + 1)]
    public async Task Given_ADummyHopCountOutOfRange_When_Building_Then_ArgumentOutOfRange(int dummyHops)
    {
        // Arrange
        await using var harness = await CreateAsync();

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => BuildPathsAsync(harness, new Secret(new byte[32]), 18, dummyHops));
    }

    [Fact]
    public async Task Given_APathForAnotherInvoice_When_AlicePaysThisHash_Then_FailedWithInvalidOnionBlindingFromBob()
    {
        // Arrange: the path_id belongs to another invoice's preimage, so Carol refuses and Bob answers for the path
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "target", null, ct);
        var paths = await BuildPathsAsync(harness, new Secret(Enumerable.Repeat((byte)7, 32).ToArray()), invoice.MinFinalCltvExpiry);
        var payments = harness.Alice.Services.GetRequiredService<IPaymentService>();

        // Act
        var paying = payments.PayBlindedAsync(new PayBlindedRequest(invoice.PaymentHash, s_amount, paths),
                                              new PayInvoiceOptions { Timeout = TimeSpan.FromSeconds(30) }, ct);
        await harness.PumpAsync();
        var result = await paying;

        // Assert: one HTLC, failed by the introduction node (hop 0), no other path to try
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(FailureCode.InvalidOnionBlinding, result.Payment.FailureCode);
        Assert.Equal(0, result.Payment.FailureSourceIndex);
        Assert.Equal(1, result.Attempts);
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Open, stored!.Status);
    }

    [Fact]
    public async Task Given_NoUsablePath_When_Paying_Then_FailedWithoutAnHtlc()
    {
        // Arrange: the only path's introduction node takes at most 1 msat
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "too big", null, ct);
        var path = Assert.Single(await BuildPathsAsync(harness, invoice.Preimage!.Value, invoice.MinFinalCltvExpiry));
        var tooSmall = path with { PayInfo = path.PayInfo with { HtlcMaximumMsat = 1 } };
        var payments = harness.Alice.Services.GetRequiredService<IPaymentService>();

        // Act
        var result = await payments.PayBlindedAsync(new PayBlindedRequest(invoice.PaymentHash, s_amount, [tooSmall]),
                                                    new PayInvoiceOptions { Timeout = TimeSpan.FromSeconds(5) }, ct);

        // Assert
        Assert.Equal(PaymentStatus.Failed, result.Payment.Status);
        Assert.Equal(0, result.Attempts);
        Assert.Contains("maximum", result.Payment.FailureReason);
        Assert.DoesNotContain(harness.Bob.Received, m => m is UpdateAddHtlcMessage);
    }

    private static async Task<IReadOnlyList<Domain.Protocol.Onion.Models.BlindedPaymentPath>> BuildPathsAsync(
        ThreeNodeHarness harness, Secret preimage, ushort minFinalCltvExpiryDelta, int? dummyHops = null)
    {
        // Carol holds Bob's signed channel_update of their channel (the gossip exchange the harness leaves out)
        Assert.True(harness.Bob.Services.GetRequiredService<IChannelUpdateService>()
                           .TryGetLocalChannelUpdate(ThreeNodeHarness.BobCarolChannelId, out var bobsUpdate));
        Assert.True(harness.Carol.Services.GetRequiredService<IChannelUpdateService>()
                           .HandleRemoteChannelUpdate(harness.Bob.NodeId, bobsUpdate!));

        var builder = harness.Carol.Services.GetRequiredService<BlindedPathBuilder>();
        return await builder.BuildAsync(new BlindedPathRequest(preimage, s_amount, minFinalCltvExpiryDelta, ThreeNodeHarness.BlockHeight,
                                                               IncludePrivateChannels: true,
                                                               DummyHops: dummyHops),
                                        TestContext.Current.CancellationToken);
    }

    private static async Task<ThreeNodeHarness> CreateAsync() =>
        await ThreeNodeHarness.CreateAsync(h =>
        {
            foreach (var node in h.Nodes)
            {
                node.Options.Features.OptionRouteBlinding = FeatureSupport.Optional;
            }

            h.Alice.ConfigureServices = services => services.AddPaymentSendServices();
        });
}
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

/// <summary>
/// ONION M5 step 2 in-process: Carol makes a blinded path to herself with the production
/// <see cref="BlindedPathBuilder"/> (Bob the introduction node, from her channel to him and his policy), and Alice pays
/// it with the production <see cref="PaymentService.PayBlindedAsync"/> through the production switches of Bob and Carol
/// (<see cref="ThreeNodeHarness"/>: real Sphinx, blinding crypto, commitments and SQLite).
/// </summary>
public class BlindedSendThreeNodeTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(40_000_321);

    [Fact]
    public async Task Given_CarolsBlindedPathThroughBob_When_AlicePaysIt_Then_PaymentSucceedsAndCarolSettles()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await CreateAsync();
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "blinded receive", null, ct);
        var paths = await BuildPathsAsync(harness, invoice.Preimage, invoice.MinFinalCltvExpiry);
        var path = Assert.Single(paths);
        Assert.Equal(harness.Bob.NodeId, path.Path.FirstNodeId);
        Assert.Equal(2, path.Path.Hops.Count);
        Assert.Equal(path.Path.Hops[0].EncryptedRecipientData.Length, path.Path.Hops[1].EncryptedRecipientData.Length);
        var payments = harness.Alice.Services.GetRequiredService<IPaymentService>();

        // Act
        var paying = payments.PayBlindedAsync(new PayBlindedRequest(invoice.PaymentHash, s_amount, paths),
                                              new PayInvoiceOptions { Timeout = TimeSpan.FromSeconds(30) }, ct);
        await harness.PumpAsync();
        var result = await paying;

        // Assert: succeeded with the path's fee; Bob forwarded with the next path_key; Carol settled
        Assert.True(result.Payment.Status == PaymentStatus.Succeeded, result.Payment.FailureReason);
        Assert.Equal(invoice.Preimage, result.Payment.Preimage);
        Assert.Equal(path.PayInfo.ComputeFeeMsat(s_amount.MilliSatoshi), result.Payment.Fee.MilliSatoshi);
        var forwarded = Assert.IsType<UpdateAddHtlcMessage>(
            Assert.Single(harness.Carol.Received, m => m is UpdateAddHtlcMessage));
        Assert.NotNull(forwarded.BlindedPathTlv);
        Assert.Equal(s_amount, forwarded.Payload.Amount);
        var stored = await harness.Carol.InScopeAsync(u => u.InvoiceDbRepository
                                                             .GetByPaymentHashAsync(invoice.PaymentHash));
        Assert.Equal(InvoiceStatus.Settled, stored!.Status);
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
        var path = Assert.Single(await BuildPathsAsync(harness, invoice.Preimage, invoice.MinFinalCltvExpiry));
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
        ThreeNodeHarness harness, Secret preimage, ushort minFinalCltvExpiryDelta)
    {
        // Carol holds Bob's signed channel_update of their channel (the gossip exchange the harness leaves out)
        Assert.True(harness.Bob.Services.GetRequiredService<IChannelUpdateService>()
                           .TryGetLocalChannelUpdate(ThreeNodeHarness.BobCarolChannelId, out var bobsUpdate));
        Assert.True(harness.Carol.Services.GetRequiredService<IChannelUpdateService>()
                           .HandleRemoteChannelUpdate(harness.Bob.NodeId, bobsUpdate!));

        var builder = harness.Carol.Services.GetRequiredService<BlindedPathBuilder>();
        return await builder.BuildAsync(new BlindedPathRequest(preimage, s_amount, minFinalCltvExpiryDelta, ThreeNodeHarness.BlockHeight,
                                                               IncludePrivateChannels: true),
                                        TestContext.Current.CancellationToken);
    }

    private static async Task<ThreeNodeHarness> CreateAsync() =>
        await ThreeNodeHarness.CreateAsync(h =>
        {
            foreach (var node in h.Nodes)
            {
                node.Options.Features.AllowExperimentalFeatures = true;
                node.Options.Features.OptionRouteBlinding = FeatureSupport.Optional;
            }

            h.Alice.ConfigureServices = services => services.AddPaymentSendServices();
        });
}
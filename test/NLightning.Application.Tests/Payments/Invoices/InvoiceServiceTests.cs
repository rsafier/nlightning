using System.Security.Cryptography;

namespace NLightning.Application.Tests.Payments.Invoices;

using Bolt11.Models;
using Bolt11.Services;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.ValueObjects;

/// <summary>
/// <c>InvoiceService</c>: CSPRNG preimage/secret, BOLT 11 encoded with the node key (W0-D path) and accepted by the
/// Bolt11 decoder and validator, persisted before it is returned.
/// </summary>
public class InvoiceServiceTests : IDisposable
{
    private readonly PaymentsTestNode _node = new("bob", 0x42);

    public void Dispose() => _node.Dispose();

    [Fact]
    public async Task Given_Amount_When_InvoiceCreated_Then_Bolt11DecodesAndValidatesWithEveryField()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var amount = LightningMoney.MilliSatoshis(50_000_123);

        // Act
        var invoice = await _node.InvoiceService.CreateInvoiceAsync(amount, "coffee", null, ct);
        var decoded = Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest);

        // Assert: the decoder accepted it (signature, features, fields); the writer rules hold too
        Assert.True(new InvoiceValidationService().ValidateForEncoding(decoded).IsValid);
        Assert.StartsWith("lnbcrt", invoice.Bolt11);
        Assert.Equal(amount, decoded.Amount);
        Assert.Equal("coffee", decoded.Description);
        Assert.Equal(_node.NodeId, new CompactPubKey(decoded.PayeePubKey!.ToBytes()));
        Assert.Equal(invoice.PaymentHash.ToString(), decoded.PaymentHash!.ToString());
        Assert.Equal(Convert.ToHexString(invoice.PaymentSecret).ToLowerInvariant(),
                     decoded.PaymentSecret!.ToString());
        Assert.Equal(_node.Options.Routing.InvoiceMinFinalCltvExpiry, decoded.MinFinalCltvExpiry);
        Assert.Equal(invoice.MinFinalCltvExpiry, decoded.MinFinalCltvExpiry);
        Assert.Equal(3_600, decoded.ExpiryDate.ToUnixTimeSeconds() - decoded.Timestamp);
        Assert.Equal(invoice.CreatedAt.ToUnixTimeSeconds(), decoded.Timestamp);
        Assert.True(decoded.Features!.IsFeatureSet(Feature.VarOnionOptin, true));
        Assert.True(decoded.Features.IsFeatureSet(Feature.PaymentSecret, true));
        Assert.True(decoded.Features.IsFeatureSet(Feature.BasicMpp, false)); // ABCD W6-B: we receive MPP
        Assert.False(decoded.Features.IsFeatureSet(Feature.BasicMpp, true));

        // Assert: the model
        Assert.Equal(invoice.PaymentHash, (Hash)SHA256.HashData(invoice.Preimage));
        Assert.Equal(amount, invoice.Amount);
        Assert.Equal(InvoiceStatus.Open, invoice.Status);
        Assert.Equal(3_600U, invoice.ExpirySeconds);
    }

    [Fact]
    public async Task Given_BasicMppTurnedOff_When_InvoiceCreated_Then_NoBasicMppBit()
    {
        // Arrange
        _node.Options.Features.BasicMpp = FeatureSupport.No;

        // Act
        var invoice = await _node.InvoiceService.CreateInvoiceAsync(LightningMoney.Satoshis(1_000), "single", null,
                                                                    TestContext.Current.CancellationToken);
        var decoded = Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest);

        // Assert: exactly bits 8 and 14 compulsory
        Assert.False(decoded.Features!.HasFeature(Feature.BasicMpp));
        Assert.True(decoded.Features.IsFeatureSet(Feature.VarOnionOptin, true));
        Assert.True(decoded.Features.IsFeatureSet(Feature.PaymentSecret, true));
    }

    [Fact]
    public async Task Given_NewInvoice_When_Created_Then_PersistedAndSavedOnceBeforeReturn()
    {
        // Act
        var invoice = await _node.InvoiceService.CreateInvoiceAsync(LightningMoney.Satoshis(1), "", 60,
                                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, _node.Invoices.AddCalls);
        Assert.Same(invoice, Assert.Single(_node.Invoices.Invoices));
        _node.UnitOfWork.Verify(x => x.SaveChangesAsync(), Times.Once);
        Assert.Equal(60U, invoice.ExpirySeconds);
    }

    [Fact]
    public async Task Given_NoAmount_When_Created_Then_AnyAmountInvoice()
    {
        // Act
        var invoice = await _node.InvoiceService.CreateInvoiceAsync(null, "tip jar", null,
                                                                    TestContext.Current.CancellationToken);
        var decoded = Invoice.Decode(invoice.Bolt11, BitcoinNetwork.Regtest);

        // Assert
        Assert.Null(invoice.Amount);
        Assert.True(decoded.Amount.IsZero);
    }

    [Fact]
    public async Task Given_TwoInvoices_When_Created_Then_PreimagesAndSecretsAreFresh()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;

        // Act
        var first = await _node.InvoiceService.CreateInvoiceAsync(LightningMoney.Satoshis(1), "a", null, ct);
        var second = await _node.InvoiceService.CreateInvoiceAsync(LightningMoney.Satoshis(1), "a", null, ct);

        // Assert
        Assert.NotEqual(first.Preimage, second.Preimage);
        Assert.NotEqual(first.PaymentSecret, second.PaymentSecret);
        Assert.NotEqual(first.PaymentHash, second.PaymentHash);
    }

    [Fact]
    public async Task Given_ZeroAmount_When_Created_Then_ArgumentOutOfRangeAndNothingPersisted()
    {
        // Act / Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _node.InvoiceService.CreateInvoiceAsync(
                                                                  LightningMoney.Zero, "x", null,
                                                                  TestContext.Current.CancellationToken));
        Assert.Equal(0, _node.Invoices.AddCalls);
    }

    [Fact]
    public async Task Given_DescriptionTooLong_When_Created_Then_EncodeFailsAndNothingPersisted()
    {
        // Act / Assert: BOLT 11 d is at most 639 bytes
        await Assert.ThrowsAsync<ArgumentException>(() => _node.InvoiceService.CreateInvoiceAsync(
                                                                    LightningMoney.Satoshis(1),
                                                                    new string('x', 1_000), null,
                                                                    TestContext.Current.CancellationToken));
        Assert.Equal(0, _node.Invoices.AddCalls);
        _node.UnitOfWork.Verify(x => x.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task Given_OpenInvoice_When_Canceled_Then_TrueAndStatusCanceledAndSaved()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var invoice = await _node.InvoiceService.CreateInvoiceAsync(LightningMoney.Satoshis(5), "x", null, ct);

        // Act
        var canceled = await _node.InvoiceService.CancelInvoiceAsync(invoice.PaymentHash, ct);
        var again = await _node.InvoiceService.CancelInvoiceAsync(invoice.PaymentHash, ct);
        var unknown = await _node.InvoiceService.CancelInvoiceAsync(Enumerable.Repeat((byte)1, 32).ToArray(), ct);

        // Assert
        Assert.True(canceled);
        Assert.False(again);
        Assert.False(unknown);
        Assert.Equal(InvoiceStatus.Canceled,
                     (await _node.InvoiceService.GetInvoiceAsync(invoice.PaymentHash, ct))!.Status);
        Assert.Equal(1, _node.Invoices.UpdateCalls);
        _node.UnitOfWork.Verify(x => x.SaveChangesAsync(), Times.Exactly(2));
    }

    [Fact]
    public async Task Given_Invoices_When_Listed_Then_RepositoryPageReturned()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await _node.InvoiceService.CreateInvoiceAsync(LightningMoney.Satoshis(1), "a", null, ct);
        await _node.InvoiceService.CreateInvoiceAsync(LightningMoney.Satoshis(2), "b", null, ct);

        // Act
        var page = await _node.InvoiceService.ListInvoicesAsync(0, 10, ct);
        var unknown = await _node.InvoiceService.GetInvoiceAsync(Enumerable.Repeat((byte)1, 32).ToArray(), ct);

        // Assert
        Assert.Equal(2, page.Count);
        Assert.Null(unknown);
    }
}
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Payments.Invoices;

using Domain.Accounting.Labels;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Fencing;

/// <summary>
/// NL-1341: <c>InvoiceService</c> asks the node write fence before a BOLT 11 invoice is signed with the node key. A
/// refusal signs and stores nothing; a fence that allows it changes nothing.
/// </summary>
public class InvoiceServiceWriteFenceTests
{
    [Fact]
    public async Task Given_ARefusingFence_When_AnInvoiceIsCreated_Then_NothingIsSignedOrStored()
    {
        // Arrange
        var fence = new FakeNodeWriteFence { Refuse = true };
        using var node = new PaymentsTestNode("bob", 0x42, writeFence: fence);

        // Act / Assert
        await Assert.ThrowsAsync<NodeFencedException>(() =>
            node.InvoiceService.CreateInvoiceAsync(LightningMoney.Satoshis(1_000), "coffee", null,
                                                   TestContext.Current.CancellationToken));
        Assert.Empty(node.Invoices.Invoices);
        node.UnitOfWork.Verify(x => x.SaveChangesAsync(), Times.Never);
        Assert.Equal([NodeEffect.Sign], fence.Effects);
    }

    [Fact]
    public async Task Given_ARefusingFence_When_AHoldInvoiceIsCreated_Then_NothingIsSignedOrStored()
    {
        // Arrange
        var fence = new FakeNodeWriteFence { Refuse = true };
        using var node = new PaymentsTestNode("bob", 0x42, writeFence: fence);

        // Act / Assert
        await Assert.ThrowsAsync<NodeFencedException>(() =>
            node.InvoiceService.CreateHoldInvoiceAsync(new Hash(Enumerable.Repeat((byte)0x55, 32).ToArray()),
                                                       LightningMoney.Satoshis(1_000), "hold", null, null,
                                                       SourceLabels.None, TestContext.Current.CancellationToken));
        Assert.Empty(node.Invoices.Invoices);
    }

    [Fact]
    public async Task Given_AFenceThatAllowsIt_When_AnInvoiceIsCreated_Then_ItIsSignedAndStored()
    {
        // Arrange
        var fence = new FakeNodeWriteFence();
        using var node = new PaymentsTestNode("bob", 0x42, writeFence: fence);

        // Act
        var invoice = await node.InvoiceService.CreateInvoiceAsync(LightningMoney.Satoshis(1_000), "coffee", null,
                                                                   TestContext.Current.CancellationToken);

        // Assert
        Assert.StartsWith("lnbcrt", invoice.Bolt11);
        Assert.Single(node.Invoices.Invoices);
        Assert.Equal([NodeEffect.Sign], fence.Effects);
    }
}
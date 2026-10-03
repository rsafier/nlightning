namespace NLightning.Application.Tests.Channels.Taproot;

using Domain.Money;
using Domain.Protocol.Messages;
using Harness;

/// <summary>
/// A payment forwarded over simple taproot channels (NL-877 T5): Alice → Bob → Carol on two taproot channels, each
/// node on its own SQLite database with the production HTLC switch (<see cref="ThreeNodeHarness"/>), so Bob's forward
/// signs and verifies MuSig2 commitments on both sides of the switch.
/// </summary>
public class TaprootForwardTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);

    [Fact]
    public async Task Given_TaprootChannels_When_AlicePaysCarolThroughBob_Then_TheForwardSettlesOnMusigCommitments()
    {
        // Arrange
        await using var harness = await ThreeNodeHarness.CreateAsync(simpleTaproot: true);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "taproot", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret);
        var fee = ThreeNodeHarness.ForwardingFeeOf(ThreeNodeHarness.BobRouting, s_amount);
        var aliceBefore = harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!.LocalBalanceMsat;

        // Act
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert - Alice learnt the preimage; every commitment_signed was a MuSig2 one
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage, fulfilled.PaymentPreimage);
        Assert.Equal(aliceBefore - (s_amount + fee).MilliSatoshi,
                     harness.Alice.Channel(ThreeNodeHarness.AliceBobChannelId).Commitments!.LocalBalanceMsat);
        var signed = harness.Sent.Select(s => s.Message).OfType<CommitmentSignedMessage>().ToList();
        Assert.True(signed.Count >= 8, $"{signed.Count} commitment_signed");
        Assert.All(signed, m =>
        {
            Assert.True(m.Payload.Signature.IsZero);
            Assert.NotNull(m.PartialSignatureWithNonceTlv);
        });
        Assert.All(harness.Sent.Select(s => s.Message).OfType<RevokeAndAckMessage>(),
                   m => Assert.NotNull(m.NextLocalNoncesTlv));
        foreach (var node in harness.Nodes)
            Assert.All(node.Memory.FindChannels(_ => true), c => Assert.Empty(c.Commitments!.Htlcs));
    }
}
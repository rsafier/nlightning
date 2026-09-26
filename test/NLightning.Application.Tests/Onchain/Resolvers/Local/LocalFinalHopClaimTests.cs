using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Local;

using Channels.Services;
using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Payments.Models;
using Remote;

/// <summary>
/// NL-316 and NL-322 on our own commitment (B5-LCL-RO-01 with B5-LCL-RO-02): an HTLC the peer offered that pays one of
/// our invoices and was not fulfilled before the close is handed to the HTLC switch as a final-hop candidate while the
/// invoice is <c>Open</c>; once the switch accepted it (the preimage persisted on the HTLC's record) our HTLC-success
/// transaction claims it with that preimage. Every transaction is script-executed against the output it spends.
/// </summary>
public sealed class LocalFinalHopClaimTests
{
    private const ulong ReceivedMsat = 30_000_000;
    private const uint ReceivedCltv = 1_020;

    private static readonly Secret s_receivedPreimage = RealSigningCommitmentPair.Preimage(2);

    [Fact]
    public async Task Given_PeerHtlcForOurOpenInvoice_When_OurCommitmentConfirms_Then_TheSwitchDecidesAndNothingIsClaimed()
    {
        // Arrange
        ulong id = 0;
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            id = pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Settle(pair.Bob);
        });
        AddInvoice(harness);

        // Act
        await harness.ResolveAsync();
        await harness.MineAsync();

        // Assert: one decision per round, no HTLC-success without the switch's acceptance
        var decisions = harness.Events.Select(e => e.Event).OfType<IncomingHtlcLockedIn>().ToList();
        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, d => Assert.Equal(id, d.Htlc.Id));
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
    }

    [Fact]
    public async Task Given_PeerHtlcTheSwitchAcceptedAsFinalHop_When_OurCommitmentConfirms_Then_HtlcSuccessWithItsPreimage()
    {
        // Arrange: the switch accepted the HTLC on chain (preimage on its record, invoice settled)
        ulong id = 0;
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            id = pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Settle(pair.Bob);
        });
        harness.Channel.UpdateCommitments(
            RemoteFinalHopClaimTests.WithKnownPreimage(harness.Channel.Commitments!, id, s_receivedPreimage));
        var invoice = AddInvoice(harness);
        invoice.Accept(LightningMoney.MilliSatoshis(ReceivedMsat));
        invoice.Settle(DateTimeOffset.UtcNow);

        // Act
        await harness.ResolveAsync();

        // Assert: the HTLC-success at once, with the preimage, valid against the output, deadline cltv_expiry
        var vout = harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc);
        var success = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(new OutPoint(harness.CommitmentTransaction, vout), Assert.Single(success.Inputs).PrevOut);
        Assert.Equal((byte[])s_receivedPreimage, success.Inputs[0].WitScript.Pushes.ElementAt(3));
        harness.AssertAllInputsVerify(success);
        Assert.Equal(ReceivedCltv, harness.CommitmentRow(vout).DeadlineHeight);
        Assert.DoesNotContain(harness.Events, e => e.Event is IncomingHtlcLockedIn);
    }

    [Fact]
    public async Task Given_ARecordPreimageOfAnotherHash_When_OurCommitmentConfirms_Then_NeverClaimed()
    {
        // Arrange
        ulong id = 0;
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            id = pair.Add(pair.Bob, ReceivedMsat, s_receivedPreimage, ReceivedCltv);
            pair.Settle(pair.Bob);
        });
        harness.Channel.UpdateCommitments(
            RemoteFinalHopClaimTests.WithKnownPreimage(harness.Channel.Commitments!, id,
                                                       RealSigningCommitmentPair.Preimage(9)));

        // Act
        await harness.ResolveAsync();
        await harness.MineToAsync(ReceivedCltv);

        // Assert
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(OutputResolutionState.Ignored,
                     harness.CommitmentRow(harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc)).State);
    }

    private static InvoiceModel AddInvoice(LocalCommitResolutionHarness harness)
    {
        var invoice = new InvoiceModel(RealSigningCommitmentPair.Hash(s_receivedPreimage), s_receivedPreimage,
                                       new Secret(Enumerable.Repeat((byte)0x53, 32).ToArray()),
                                       LightningMoney.MilliSatoshis(ReceivedMsat), "final hop", "lnbcrt-test",
                                       DateTimeOffset.UtcNow, 3_600, 18);
        harness.Invoices[invoice.PaymentHash] = invoice;
        return invoice;
    }
}
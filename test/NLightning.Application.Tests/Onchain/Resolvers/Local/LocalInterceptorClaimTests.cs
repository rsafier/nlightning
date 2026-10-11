using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Local;

using Channels.Services;
using Domain.Accounting.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Payments.Models;
using Remote;

/// <summary>
/// NL-1182 on our own commitment: a forward held for the HTLC interceptor while its incoming channel closed on chain
/// and settled by it keeps the interceptor's preimage on the incoming HTLC's record (<c>KnownPreimage</c>);
/// <see cref="Application.Onchain.Resolvers.LocalCommitResolver"/> claims the HTLC with it through our HTLC-success
/// transaction, with the settle's <c>InterceptedHtlcSettled</c> event and without it (the feed's gate, NL-619). A
/// record that is a final hop's mark of an open invoice, one failed off chain or one of another hash is never claimed.
/// Every transaction is script-executed against the output it spends.
/// </summary>
public sealed class LocalInterceptorClaimTests
{
    private const ulong ReceivedMsat = 30_000_000;
    private const uint ReceivedCltv = 1_020;

    private static readonly Secret s_preimage = RealSigningCommitmentPair.Preimage(4);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AForwardTheInterceptorSettled_When_OurCommitmentConfirms_Then_HtlcSuccessWithItsPreimage(
        bool withSettleEvent)
    {
        // Arrange: no invoice for the hash (a forward), the interceptor's preimage on the incoming record
        ulong id = 0;
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            id = pair.Add(pair.Bob, ReceivedMsat, s_preimage, ReceivedCltv);
            pair.Settle(pair.Bob);
        });
        harness.Channel.UpdateCommitments(
            RemoteFinalHopClaimTests.WithKnownPreimage(harness.Channel.Commitments!, id, s_preimage));
        if (withSettleEvent)
            harness.AccountingEventKeys.Add(AccountingEventKeys.InterceptedHtlcSettled(harness.Channel.ChannelId, id));

        // Act
        await harness.ResolveAsync();

        // Assert: the HTLC-success at once, revealing the interceptor's preimage, valid against the output
        var vout = harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc);
        var success = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(new OutPoint(harness.CommitmentTransaction, vout), Assert.Single(success.Inputs).PrevOut);
        Assert.Equal((byte[])s_preimage, success.Inputs[0].WitScript.Pushes.ElementAt(3));
        harness.AssertAllInputsVerify(success);
        Assert.Equal(ReceivedCltv, harness.CommitmentRow(vout).DeadlineHeight);
    }

    [Fact]
    public async Task Given_AMarkWithoutTheSettleEventOnAnHtlcOfAnOpenInvoice_When_OurCommitmentConfirms_Then_NotClaimed()
    {
        // Arrange: without the interceptor's event a mark on an HTLC of one of our invoices is a final hop's, and the
        // invoice is still Open (NL-323)
        ulong id = 0;
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            id = pair.Add(pair.Bob, ReceivedMsat, s_preimage, ReceivedCltv);
            pair.Settle(pair.Bob);
        });
        harness.Channel.UpdateCommitments(
            RemoteFinalHopClaimTests.WithKnownPreimage(harness.Channel.Commitments!, id, s_preimage));
        var hash = RealSigningCommitmentPair.Hash(s_preimage);
        harness.Invoices[hash] = new InvoiceModel(hash, s_preimage,
                                                  new Secret(Enumerable.Repeat((byte)0x53, 32).ToArray()),
                                                  LightningMoney.MilliSatoshis(ReceivedMsat), "final hop",
                                                  "lnbcrt-test", DateTimeOffset.UtcNow, 3_600, 18);

        // Act
        await harness.ResolveAsync();

        // Assert
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
    }

    [Fact]
    public async Task Given_AnHtlcWeFailedOffChainWithTheSettleEvent_When_OurCommitmentConfirms_Then_NeverClaimed()
    {
        // Arrange: our update_fail_htlc is not signed yet, so our commitment still holds the HTLC; a fail removal is
        // never claimed, whatever the record and the feed say
        ulong id = 0;
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            id = pair.Add(pair.Bob, ReceivedMsat, s_preimage, ReceivedCltv);
            pair.Settle(pair.Bob);
            pair.Fail(pair.Alice, id);
        });
        harness.Channel.UpdateCommitments(
            RemoteFinalHopClaimTests.WithKnownPreimage(harness.Channel.Commitments!, id, s_preimage));
        harness.AccountingEventKeys.Add(AccountingEventKeys.InterceptedHtlcSettled(harness.Channel.ChannelId, id));

        // Act
        await harness.ResolveAsync();
        await harness.MineToAsync(ReceivedCltv);

        // Assert
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
    }

    [Fact]
    public async Task Given_TheSettleEventButARecordPreimageOfAnotherHash_When_OurCommitmentConfirms_Then_NeverClaimed()
    {
        // Arrange
        ulong id = 0;
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            id = pair.Add(pair.Bob, ReceivedMsat, s_preimage, ReceivedCltv);
            pair.Settle(pair.Bob);
        });
        harness.Channel.UpdateCommitments(
            RemoteFinalHopClaimTests.WithKnownPreimage(harness.Channel.Commitments!, id,
                                                       RealSigningCommitmentPair.Preimage(9)));
        harness.AccountingEventKeys.Add(AccountingEventKeys.InterceptedHtlcSettled(harness.Channel.ChannelId, id));

        // Act
        await harness.ResolveAsync();
        await harness.MineToAsync(ReceivedCltv);

        // Assert
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Equal(OutputResolutionState.Ignored,
                     harness.CommitmentRow(harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc)).State);
    }
}
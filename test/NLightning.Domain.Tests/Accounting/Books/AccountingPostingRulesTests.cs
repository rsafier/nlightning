using NLightning.Tests.Utils.Accounting;

namespace NLightning.Domain.Tests.Accounting.Books;

using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;

/// <summary>
/// The operational posting rules (plan §6.1 "Rules") over events built in the writers' exact detail format
/// (<c>PaymentAccountingEvents</c>, <c>ChannelAccountingEvents</c>, <c>OnchainAccounting</c>,
/// <c>BlockchainMonitorService.Accounting</c>, <see cref="AccountingConfirmations"/>).
/// </summary>
public class AccountingPostingRulesTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TxId s_txId = new(Enumerable.Repeat((byte)0x3c, 32).ToArray());

    #region Off-chain

    [Fact]
    public void Given_AnInvoiceSettled_When_Posted_Then_ChannelsDebitedAndReceivedCredited()
    {
        // Arrange
        var settled = Event(AccountingEventKind.InvoiceSettled, 50_000_123, 0,
                            ("kind", "bolt11"), ("description", "coffee"), ("parts", "1"), ("settledBy", "fulfill"));

        // Act
        var postings = Post(settled);

        // Assert
        AssertPostings(postings, (AccountRole.Channels, 50_000_123), (AccountRole.Received, -50_000_123));
    }

    [Fact]
    public void Given_APaymentSucceeded_When_Posted_Then_AmountPlusFeeLeaveTheChannels()
    {
        // Arrange: 50,000 sat paid with a 1,005 msat route fee (AmountMsat = -(amount + fee))
        var paid = Event(AccountingEventKind.PaymentSucceeded, -50_001_005, 1_005, ("kind", "bolt11"), ("parts", "1"));

        // Act
        var postings = Post(paid);

        // Assert
        AssertPostings(postings, (AccountRole.Channels, -50_001_005), (AccountRole.Sent, 50_000_000),
                       (AccountRole.RoutingFees, 1_005));
    }

    [Fact]
    public void Given_ARebalance_When_BothSidesArePosted_Then_OnlyTheRouteFeeIsAnExpenseAndNothingIsIncome()
    {
        // Arrange - NL-609: a circular payment of our own invoice: 50,000 sat out through one channel with a 1,005 msat
        // route fee, the same 50,000 sat back in through another
        var paid = Event(AccountingEventKind.PaymentSucceeded, -50_001_005, 1_005, ("kind", "bolt11"), ("parts", "1"),
                         ("selfPayment", "true"));
        var settled = Event(AccountingEventKind.InvoiceSettled, 50_000_000, 0, ("kind", "bolt11"), ("parts", "1"),
                            ("selfPayment", "true"));

        // Act
        var paidPostings = Post(paid);
        var settledPostings = Post(settled);

        // Assert: each entry balances; together the channels lost the fee and the rebalance account holds it
        AssertPostings(paidPostings, (AccountRole.Channels, -50_001_005), (AccountRole.Rebalance, 50_001_005));
        AssertPostings(settledPostings, (AccountRole.Channels, 50_000_000), (AccountRole.Rebalance, -50_000_000));
        var together = paidPostings.Concat(settledPostings).GroupBy(p => p.Account)
                                   .ToDictionary(g => g.Key, g => g.Sum(p => p.AmountMsat));
        Assert.Equal(-1_005, together[AccountRole.Channels]);
        Assert.Equal(1_005, together[AccountRole.Rebalance]);
        Assert.DoesNotContain(paidPostings.Concat(settledPostings),
                              p => p.Account is AccountRole.Received or AccountRole.Sent or AccountRole.RoutingFees);
    }

    [Fact]
    public void Given_AFeeFreePayment_When_Posted_Then_NoRoutingFeeLine()
    {
        // Act
        var postings = Post(Event(AccountingEventKind.PaymentSucceeded, -42_000, 0, ("kind", "keysend")));

        // Assert
        AssertPostings(postings, (AccountRole.Channels, -42_000), (AccountRole.Sent, 42_000));
    }

    [Fact]
    public void Given_APaymentFailed_When_Posted_Then_NothingMoves()
    {
        // Act
        var postings = Post(Event(AccountingEventKind.PaymentFailed, 0, 0, ("reason", "no route"),
                                  ("amountMsat", "1000")));

        // Assert
        Assert.Empty(postings);
    }

    [Fact]
    public void Given_AForwardSettled_When_Posted_Then_TheFeeIsRoutingIncome()
    {
        // Act
        var postings = Post(Event(AccountingEventKind.ForwardSettled, 1_050, 0, ("incomingAmountMsat", "51050"),
                                  ("outgoingAmountMsat", "50000")));

        // Assert
        AssertPostings(postings, (AccountRole.Channels, 1_050), (AccountRole.Routing, -1_050));
    }

    [Fact]
    public void Given_ATrampolineRelaySettled_When_Posted_Then_ItsNetIsRoutingIncome()
    {
        // Act (NL-875): 100,000 sat in, 99,000 sat out (amount and routing fees paid)
        var postings = Post(Event(AccountingEventKind.TrampolineRelaySettled, 1_000_000, 0,
                                  ("incomingAmountMsat", "100000000"), ("outgoingAmountMsat", "99000000")));

        // Assert
        AssertPostings(postings, (AccountRole.Channels, 1_000_000), (AccountRole.Routing, -1_000_000));
    }

    [Fact]
    public void Given_ATrampolineRelayThatCostMoreThanItBrought_When_Posted_Then_TheDifferenceIsARoutingExpense()
    {
        // Act (NL-875)
        var postings = Post(Event(AccountingEventKind.TrampolineRelaySettled, -2_500, 0));

        // Assert
        AssertPostings(postings, (AccountRole.Channels, -2_500), (AccountRole.RoutingFees, 2_500));
    }

    [Fact]
    public void Given_AForwardLostOnchain_When_Posted_Then_TheOutgoingAmountIsALoss()
    {
        // Act
        var postings = Post(Event(AccountingEventKind.ForwardLostOnchain, -50_000_000, 0,
                                  ("outgoingAmountMsat", "50000000")));

        // Assert
        AssertPostings(postings, (AccountRole.Channels, -50_000_000), (AccountRole.LossOnchain, 50_000_000));
    }

    [Fact]
    public void Given_AnInvoiceLostOnchain_When_Posted_Then_TheHtlcAmountLeavesTheChannelsAsALoss()
    {
        // Act (NL-688): the HTLC of a settled invoice the peer timed out on chain
        var accountingEvent = Event(AccountingEventKind.InvoiceLostOnchain, -30_000_000, 0, ("cause", "invoiceOnchain"),
                                    (AccountingDetailKeys.Description, "coffee"));
        var postings = Post(accountingEvent);

        // Assert
        AssertPostings(postings, (AccountRole.Channels, -30_000_000), (AccountRole.LossOnchain, 30_000_000));
        Assert.Equal("Invoice payment lost on chain: coffee", AccountingPostingRules.Describe(accountingEvent));
    }

    #endregion

    #region Channels

    [Fact]
    public void Given_OurFunding_When_Posted_Then_TheContributionAndFeeComeOutOfClearing()
    {
        // Arrange: a v1 funder, 1,000,000 sat capacity, 1,234 sat funding fee
        var funded = Event(AccountingEventKind.ChannelFunded, 1_000_000_000, 1_234_000, ("bucketFrom", "wallet"),
                           ("bucketTo", "channel"), ("isInitiator", "true"), ("dualFunded", "false"));

        // Act
        var postings = Post(funded);

        // Assert
        AssertPostings(postings, (AccountRole.Channels, 1_000_000_000), (AccountRole.FeeFunding, 1_234_000),
                       (AccountRole.Clearing, -1_001_234_000));
    }

    [Fact]
    public void Given_APeerFundedChannel_When_Posted_Then_NothingMoves()
    {
        // Act
        var postings = Post(Event(AccountingEventKind.ChannelFunded, 0, 0, ("isInitiator", "false")));

        // Assert
        Assert.Empty(postings);
    }

    [Fact]
    public void Given_APushSentAndAPushReceived_When_Posted_Then_TheyAreExpenseAndIncome()
    {
        // Act
        var sent = Post(Event(AccountingEventKind.PushSent, -400_000_000, 0, ("bucketFrom", "channel")));
        var received = Post(Event(AccountingEventKind.PushReceived, 600_000_000, 0, ("bucketTo", "channel")));

        // Assert
        AssertPostings(sent, (AccountRole.Channels, -400_000_000), (AccountRole.PushSent, 400_000_000));
        AssertPostings(received, (AccountRole.Channels, 600_000_000), (AccountRole.PushReceived, -600_000_000));
    }

    [Fact]
    public void Given_ASpliceInAndASpliceOut_When_Posted_Then_TheDeltaMovesAgainstClearingWithItsFee()
    {
        // Arrange: delta has our fee taken out already (D16)
        var spliceIn = Event(AccountingEventKind.SpliceLocked, 200_000_000, 700_000, ("deltaIncludesFee", "true"));
        var spliceOut = Event(AccountingEventKind.SpliceLocked, -300_700_000, 700_000, ("deltaIncludesFee", "true"));

        // Act
        var inPostings = Post(spliceIn);
        var outPostings = Post(spliceOut);

        // Assert: the wallet side moves by -(delta + fee)
        AssertPostings(inPostings, (AccountRole.Channels, 200_000_000), (AccountRole.FeeSplice, 700_000),
                       (AccountRole.Clearing, -200_700_000));
        AssertPostings(outPostings, (AccountRole.Channels, -300_700_000), (AccountRole.FeeSplice, 700_000),
                       (AccountRole.Clearing, 300_000_000));
    }

    [Fact]
    public void Given_AMutualClose_When_Posted_Then_TheBalanceLessTheFeeReachesClearing()
    {
        // Arrange: 1,000,000 sat balance, 999,000 sat closing output, 1,000 sat fee (ours)
        var closed = Event(AccountingEventKind.ChannelClosedMutual, -1_000_000_000, 1_000_000,
                           ("balanceMsat", "1000000000"), ("ourOutputSat", "999000"));

        // Act
        var postings = Post(closed);

        // Assert
        AssertPostings(postings, (AccountRole.Channels, -1_000_000_000), (AccountRole.FeeClose, 1_000_000),
                       (AccountRole.Clearing, 999_000_000));
    }

    #endregion

    #region Force close

    [Fact]
    public void Given_AForceClose_When_Posted_Then_TheBalanceSplitsIntoPendingFeeAndLoss()
    {
        // Arrange: B = 700,000,500; pending 690,000,000; commitment fee 9,000,000; lost 1,000,500 (trimmed + rounding)
        var closed = Event(AccountingEventKind.ChannelForceClosed, -700_000_500, 9_000_000, ("pendingMsat", "690000000"),
                           ("lostMsat", "1000500"), ("closeKind", "LocalCommitment"), ("countedVouts", "0,2"));

        // Act
        var result = Evaluate(closed);

        // Assert
        AssertPostings(result.Postings, (AccountRole.Channels, -700_000_500), (AccountRole.FeeCommitment, 9_000_000),
                       (AccountRole.Pending, 690_000_000), (AccountRole.LossOnchain, 1_000_500));
        Assert.Null(result.Note);
    }

    [Fact]
    public void Given_AForceCloseWhoseCommitmentPaysMoreThanTheStandInBalance_When_Posted_Then_TheExcessIsAGain()
    {
        // Act
        var postings = Post(Event(AccountingEventKind.ChannelForceClosed, -500_000_000, 0,
                                  ("pendingMsat", "520000000"), ("lostMsat", "-20000000"),
                                  ("balanceSource", "latest-local-commitment")));

        // Assert
        AssertPostings(postings, (AccountRole.Channels, -500_000_000), (AccountRole.Pending, 520_000_000),
                       (AccountRole.OnchainGain, -20_000_000));
    }

    [Fact]
    public void Given_ABackfilledForceClose_When_Posted_Then_NothingMoves()
    {
        // Act
        var result = Evaluate(Event(AccountingEventKind.ChannelForceClosed, -500_000_000, 0,
                                    ("pendingMsat", "500000000"), ("openingBalance", "true")));

        // Assert
        Assert.Empty(result.Postings);
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void Given_AForceCloseWithoutItsPendingDetail_When_Posted_Then_TheBalanceLessFeeIsPendingWithANote()
    {
        // Act
        var result = Evaluate(Event(AccountingEventKind.ChannelForceClosed, -500_000_000, 2_000_000));

        // Assert
        AssertPostings(result.Postings, (AccountRole.Channels, -500_000_000), (AccountRole.FeeCommitment, 2_000_000),
                       (AccountRole.Pending, 498_000_000));
        Assert.NotNull(result.Note);
    }

    #endregion

    #region Resolutions

    [Fact]
    public void Given_OurSweepOfACountedOutput_When_Posted_Then_PendingReachesClearingLessTheSweepFee()
    {
        // Arrange: OnchainAccounting.Ours of a counted to_local, 500 sat fee
        var swept = Resolution(AccountingEventKind.OutputResolved, amount: 99_500_000, fee: 500_000,
                               pendingOut: 100_000_000, pendingIn: 0, wallet: 99_500_000, counted: true, by: "us");

        // Act
        var postings = Post(swept);

        // Assert
        AssertPostings(postings, (AccountRole.Pending, -100_000_000), (AccountRole.Clearing, 99_500_000),
                       (AccountRole.FeeSweep, 500_000));
    }

    [Fact]
    public void Given_OurHtlcTransaction_When_Posted_Then_PendingMovesToItsSecondLevelOutputLessTheFee()
    {
        // Arrange: our HTLC-timeout of a counted offered HTLC (rule (c))
        var timedOut = Resolution(AccountingEventKind.OutputResolved, amount: 0, fee: 300_000,
                                  pendingOut: 20_000_000, pendingIn: 19_700_000, wallet: 0, counted: true, by: "us",
                                  ("htlcDirection", "offered"));

        // Act
        var postings = Post(timedOut);

        // Assert
        AssertPostings(postings, (AccountRole.Pending, -300_000), (AccountRole.FeeSweep, 300_000));
    }

    [Fact]
    public void Given_OurAnchorsHtlcTransactionWithWalletFeeInputs_When_Posted_Then_TheWalletsFeeComesOutOfClearing()
    {
        // Arrange (NL-748): our anchors HTLC-success of a counted incoming HTLC keeps the HTLC's value on its
        // second-level output; its 268 sat fee was paid by a wallet input (whose spend and change are wallet events)
        var claimed = Resolution(AccountingEventKind.OutputResolved, amount: 0, fee: 268_000,
                                 pendingOut: 20_000_000, pendingIn: 20_000_000, wallet: 0, counted: true, by: "us",
                                 ("htlcDirection", "incoming"), ("valueBookedBy", "invoice"),
                                 ("walletFeeMsat", "268000"));

        // Act
        var postings = Post(claimed);

        // Assert: no gain, loss or channel movement; the fee is a sweep fee against the clearing account
        AssertPostings(postings, (AccountRole.Clearing, -268_000), (AccountRole.FeeSweep, 268_000));
    }

    [Fact]
    public void Given_AnIncomingHtlcWeClaimedWhoseValueAnInvoiceBooked_When_Posted_Then_TheChannelsAreCredited()
    {
        // Arrange: rule (a), an uncounted incoming HTLC claimed with the preimage
        var claimed = Resolution(AccountingEventKind.OutputResolved, amount: 29_600_000, fee: 400_000,
                                 pendingOut: 0, pendingIn: 0, wallet: 29_600_000, counted: false, by: "us",
                                 ("htlcDirection", "incoming"), ("valueBookedBy", "invoice"));

        // Act
        var postings = Post(claimed);

        // Assert
        AssertPostings(postings, (AccountRole.Clearing, 29_600_000), (AccountRole.FeeSweep, 400_000),
                       (AccountRole.Channels, -30_000_000));
    }

    [Fact]
    public void Given_OurOfferedHtlcThePeerClaimedWhoseValueAPaymentBooked_When_Posted_Then_TheChannelsAreDebited()
    {
        // Arrange: rule (b), written off the pending bucket
        var claimed = Resolution(AccountingEventKind.OutputResolved, amount: -20_000_000, fee: 0,
                                 pendingOut: 20_000_000, pendingIn: 0, wallet: 0, counted: true, by: "peer",
                                 ("htlcDirection", "offered"), ("claimedBy", "peer"), ("valueBookedBy", "payment"),
                                 ("bucket", "onchain-pending"));

        // Act
        var postings = Post(claimed);

        // Assert
        AssertPostings(postings, (AccountRole.Pending, -20_000_000), (AccountRole.Channels, 20_000_000));
    }

    [Fact]
    public void Given_OurOfferedHtlcWithASubSatoshiPartThePeerClaimed_When_TheCloseThePaymentAndTheClaimArePosted_Then_TheChannelsLoseExactlyB()
    {
        // Arrange - NL-1007 (FAFO2, 2026-10-04): we fund the channel and offered an HTLC of 5,001,005 msat; its output
        // holds 5,001 sat, so the close's fee (644,000) took the 5 msat. The payment then booked 5,001,005 out of the
        // channels and the peer's preimage claim gave back only the output's 5,001,000: the channels drifted by -5 msat
        var closed = Event(AccountingEventKind.ChannelForceClosed, -200_000_000, 644_000, "close",
                           ("pendingMsat", "199356000"), ("lostMsat", "0"), ("funder", "true"));
        var paid = Event(AccountingEventKind.PaymentSucceeded, -5_001_005, 1_005, "payment");
        var claimed = Resolution(AccountingEventKind.OutputResolved, amount: -5_001_000, fee: 0,
                                 pendingOut: 5_001_000, pendingIn: 0, wallet: 0, counted: true, by: "peer",
                                 ("htlcDirection", "offered"), ("claimedBy", "peer"), ("valueBookedBy", "payment"),
                                 ("htlcRoundingMsat", "5"), ("funder", "true"));

        // Act
        var postings = new[] { closed, paid, claimed }.SelectMany(Post).ToList();

        // Assert: the channels lose the close's B and nothing more; the 5 msat stays the payment's, not a fee too
        Assert.Equal(-200_000_000, Amount(postings, AccountRole.Channels));
        Assert.Equal(643_995, Amount(postings, AccountRole.FeeCommitment));
        Assert.Equal(199_356_000 - 5_001_000, Amount(postings, AccountRole.Pending));
        AssertPostings(Post(claimed), (AccountRole.Pending, -5_001_000), (AccountRole.Channels, 5_001_005),
                       (AccountRole.FeeCommitment, -5));
    }

    [Fact]
    public void Given_OurOfferedHtlcWithASubSatoshiPartOnAChannelWeDidNotFund_When_Posted_Then_TheCloseLossTakesItBack()
    {
        // Arrange: the close of a channel we did not fund books the HTLC's rounding in its lostMsat (NL-1007)
        var claimed = Resolution(AccountingEventKind.OutputResolved, amount: -5_001_000, fee: 0,
                                 pendingOut: 5_001_000, pendingIn: 0, wallet: 0, counted: true, by: "peer",
                                 ("htlcDirection", "offered"), ("valueBookedBy", "forward"), ("htlcRoundingMsat", "5"),
                                 ("funder", "false"));

        // Act
        var postings = Post(claimed);

        // Assert
        AssertPostings(postings, (AccountRole.Pending, -5_001_000), (AccountRole.Channels, 5_001_005),
                       (AccountRole.LossOnchain, -5));
    }

    [Theory]
    [InlineData("true", AccountRole.FeeCommitment)]
    [InlineData("false", AccountRole.LossOnchain)]
    public void Given_AnIncomingHtlcWithASubSatoshiPartWeClaimed_When_Posted_Then_ItsWholeAmountLeavesTheChannels(
        string funder, AccountRole roundingAccount)
    {
        // Arrange - NL-1007 (FAFO, the forwarder): the forward booked 5,001,005 msat into the channels, our claim of
        // the 5,001 sat output gave back only 5,001,000 of it: the channels drifted by +5 msat
        var claimed = Resolution(AccountingEventKind.OutputResolved, amount: 4_860_000, fee: 141_000,
                                 pendingOut: 0, pendingIn: 0, wallet: 4_860_000, counted: false, by: "us",
                                 ("htlcDirection", "incoming"), ("valueBookedBy", "forward"),
                                 ("htlcRoundingMsat", "5"), ("funder", funder));

        // Act
        var postings = Post(claimed);

        // Assert: the HTLC's whole msat amount leaves the channels, its sub-satoshi part to the commitment
        AssertPostings(postings, (AccountRole.Clearing, 4_860_000), (AccountRole.FeeSweep, 141_000),
                       (AccountRole.Channels, -5_001_005), (roundingAccount, 5));
    }

    [Fact]
    public void Given_ASubSatoshiPartWithoutAnOffChainOwner_When_Posted_Then_ItIsNotMoved()
    {
        // Arrange: our offered HTLC timed out back to us; no off-chain event booked its value, so the close's fee keeps
        // its rounding (NL-1007)
        var timedOut = Resolution(AccountingEventKind.OutputResolved, amount: 4_800_000, fee: 201_000,
                                  pendingOut: 5_001_000, pendingIn: 0, wallet: 4_800_000, counted: true, by: "us",
                                  ("htlcDirection", "offered"), ("htlcRoundingMsat", "5"), ("funder", "true"));

        // Act
        var postings = Post(timedOut);

        // Assert
        AssertPostings(postings, (AccountRole.Pending, -5_001_000), (AccountRole.Clearing, 4_800_000),
                       (AccountRole.FeeSweep, 201_000));
    }

    [Fact]
    public void Given_ACountedOutputThePeerTookWithoutABooking_When_Posted_Then_ItIsALoss()
    {
        // Arrange: a breach loss of our HTLC, no origin known
        var lost = Resolution(AccountingEventKind.BreachLoss, amount: -20_000_000, fee: 0, pendingOut: 20_000_000,
                              pendingIn: 0, wallet: 0, counted: true, by: "peer", ("claimedBy", "peer"));

        // Act
        var postings = Post(lost);

        // Assert
        AssertPostings(postings, (AccountRole.Pending, -20_000_000), (AccountRole.LossOnchain, 20_000_000));
    }

    [Fact]
    public void Given_APenaltyOfAnUncountedOutput_When_Posted_Then_ItIsAGain()
    {
        // Act
        var postings = Post(Resolution(AccountingEventKind.PenaltyClaimed, amount: 49_499_500, fee: 500_500,
                                       pendingOut: 0, pendingIn: 0, wallet: 49_499_500, counted: false, by: "us"));

        // Assert
        AssertPostings(postings, (AccountRole.Clearing, 49_499_500), (AccountRole.FeeSweep, 500_500),
                       (AccountRole.OnchainGain, -50_000_000));
    }

    [Fact]
    public void Given_AnUncountedOutputThePeerTook_When_Posted_Then_NothingMoves()
    {
        // Act
        var postings = Post(Resolution(AccountingEventKind.OutputResolved, amount: 0, fee: 0, pendingOut: 0,
                                       pendingIn: 0, wallet: 0, counted: false, by: "peer",
                                       ("htlcDirection", "incoming")));

        // Assert
        Assert.Empty(postings);
    }

    [Fact]
    public void Given_ACountedOutputGivenUp_When_Posted_Then_ItIsALoss()
    {
        // Act
        var postings = Post(Resolution(AccountingEventKind.OutputResolved, amount: -330_000, fee: 0,
                                       pendingOut: 330_000, pendingIn: 0, wallet: 0, counted: true, by: "ignored"));

        // Assert
        AssertPostings(postings, (AccountRole.Pending, -330_000), (AccountRole.LossOnchain, 330_000));
    }

    [Theory]
    [InlineData(true, 330_000, 330_000, 0)]
    [InlineData(false, 0, 330_000, -330_000)]
    public void Given_AnAnchorMergedIntoOurCpfpChild_When_Posted_Then_ItsValueGoesToClearing(bool counted,
                                                                                            long pendingOut,
                                                                                            long clearing,
                                                                                            long gain)
    {
        // Arrange: OnchainAccounting.Ours of a row spent with wallet inputs (note "merged")
        var merged = Resolution(AccountingEventKind.OutputResolved, amount: -pendingOut, fee: 0, pendingOut: pendingOut,
                                pendingIn: 0, wallet: 0, counted: counted, by: "us",
                                ("note", AccountingDetailKeys.MergedNote), ("valueMsat", "330000"));

        // Act
        var result = Evaluate(merged);

        // Assert: counted, the pending bucket's value; not counted (the peer funded it), a gain of its value
        Assert.Equal(-pendingOut, Amount(result.Postings, AccountRole.Pending));
        Assert.Equal(clearing, Amount(result.Postings, AccountRole.Clearing));
        Assert.Equal(gain, Amount(result.Postings, AccountRole.OnchainGain));
        Assert.Equal(0, Amount(result.Postings, AccountRole.FeeSweep));
        Assert.NotNull(result.Note);
    }

    [Theory]
    [InlineData(99_500_000, 500_000, -100_000_000, 99_500_000, 0)]
    [InlineData(-20_000_000, 0, -20_000_000, 0, 20_000_000)]
    public void Given_AResolutionWithoutFlowDetails_When_Posted_Then_ItsAmountComesOutOfPendingWithANote(
        long amount, long fee, long pending, long clearing, long loss)
    {
        // Arrange: an older writer
        var resolved = Event(AccountingEventKind.OutputResolved, amount, fee, ("descriptor", "DelayedToLocal"));

        // Act
        var result = Evaluate(resolved);

        // Assert
        Assert.Equal(pending, Amount(result.Postings, AccountRole.Pending));
        Assert.Equal(clearing, Amount(result.Postings, AccountRole.Clearing));
        Assert.Equal(fee, Amount(result.Postings, AccountRole.FeeSweep));
        Assert.Equal(loss, Amount(result.Postings, AccountRole.LossOnchain));
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void Given_ACpfpFeeAndASweepFeeBump_When_Posted_Then_OnlyTheCpfpFeeMoves()
    {
        // Act
        var cpfp = Post(Event(AccountingEventKind.AnchorCpfpFee, 0, 2_000_000, ("purpose", "AnchorCpfp")));
        var bump = Post(Event(AccountingEventKind.SweepFeeBump, 0, 1_000_000, ("purpose", "Sweep"),
                              ("replaces", s_txId.ToString())));

        // Assert
        AssertPostings(cpfp, (AccountRole.FeeCpfp, 2_000_000), (AccountRole.Clearing, -2_000_000));
        Assert.Empty(bump);
    }

    #endregion

    #region Wallet

    [Theory]
    [InlineData("external", AccountRole.TransfersIn)]
    [InlineData("broadcast", AccountRole.Clearing)]
    [InlineData("channel", AccountRole.Clearing)]
    [InlineData("wallet", AccountRole.Clearing)]
    public void Given_AWalletOutputReceived_When_Posted_Then_TheWalletIsDebitedAgainstItsSource(string source,
                                                                                              AccountRole credited)
    {
        // Act
        var postings = Post(Event(AccountingEventKind.WalletReceived, 100_000_000, 0, ("source", source),
                                  ("change", "false")));

        // Assert
        AssertPostings(postings, (AccountRole.Wallet, 100_000_000), (credited, -100_000_000));
    }

    [Fact]
    public void Given_AWalletOutputSpentAndAWithdrawal_When_Posted_Then_ClearingNetsToZeroWithTheChange()
    {
        // Arrange: 100,000 sat spent, 60,000 sat sent out, 39,000 sat change, 1,000 sat fee
        var books = BooksSimulator.Of(
        [
            Event(AccountingEventKind.WalletOutputSpent, -100_000_000, 0, "wallet:a:1:spent", ("source", "broadcast"),
                  ("purpose", "WalletSend")),
            Event(AccountingEventKind.WalletReceived, 39_000_000, 0, "wallet:b:1:in", ("source", "broadcast"),
                  ("purpose", "WalletSend"), ("change", "true")),
            Event(AccountingEventKind.WalletSent, -60_000_000, 1_000_000, "wsend:b", ("externalOutputs", "1"))
        ]);

        // Assert
        Assert.Equal(-61_000_000, books[AccountRole.Wallet]);
        Assert.Equal(0, books[AccountRole.Clearing]);
        Assert.Equal(60_000_000, books[AccountRole.TransfersOut]);
        Assert.Equal(1_000_000, books[AccountRole.FeeWithdraw]);
    }

    #endregion

    #region Opening balances and memos

    [Theory]
    [InlineData("open:channel:abcd", null, AccountRole.Channels)]
    [InlineData("open:wallet", null, AccountRole.Wallet)]
    [InlineData("open:pending:ef01", null, AccountRole.Pending)]
    [InlineData("open:x", "channel", AccountRole.Channels)]
    [InlineData("open:y", "wallet", AccountRole.Wallet)]
    [InlineData("open:z", "onchain-pending", AccountRole.Pending)]
    public void Given_AnOpeningBalance_When_Posted_Then_ItsBucketIsDebitedAgainstOpening(string key, string? bucket,
                                                                                       AccountRole account)
    {
        // Act
        var postings = Post(Event(AccountingEventKind.OpeningBalance, 5_000_000, 0, key, ("bucket", bucket)));

        // Assert
        AssertPostings(postings, (account, 5_000_000), (AccountRole.Opening, -5_000_000));
    }

    [Theory]
    [InlineData("open:cutover")]
    [InlineData("open:memo:payments")]
    [InlineData("open:unknown-bucket")]
    public void Given_AnOpeningMarkerOrMemo_When_Posted_Then_NothingMoves(string key)
    {
        // Act
        var result = Evaluate(Event(AccountingEventKind.OpeningBalance, 5_000_000, 0, key));

        // Assert
        Assert.Empty(result.Postings);
        Assert.NotNull(result.Note);
    }

    [Theory]
    [InlineData(AccountingEventKind.InvoiceSettled)]
    [InlineData(AccountingEventKind.ChannelFunded)]
    [InlineData(AccountingEventKind.WalletReceived)]
    [InlineData(AccountingEventKind.OpeningBalance)]
    public void Given_AMemoEvent_When_Posted_Then_NothingMoves(AccountingEventKind kind)
    {
        // Act
        var postings = Post(Event(kind, 7_000_000, 1_000, "open:wallet", ("memo", "true")));

        // Assert
        Assert.Empty(postings);
    }

    #endregion

    #region Reversals

    [Theory]
    [MemberData(nameof(ReversibleEvents))]
    public void Given_AnEventAndItsReversal_When_Posted_Then_EveryBalanceIsBackToZero(AccountingEventModel original)
    {
        // Arrange
        var books = new BooksSimulator();
        var entry = books.Apply(original);
        var reversal = AccountingConfirmations.CreateReversal(original, s_at, 99);

        // Act
        var negated = books.Apply(reversal);

        // Assert: the exact negation, line by line
        Assert.NotNull(entry);
        Assert.NotNull(negated);
        Assert.Equal(entry.Postings.Select(p => (p.Account, -p.AmountMsat)),
                     negated.Postings.Select(p => (p.Account, p.AmountMsat)));
        Assert.All(books.Balances.Values, balance => Assert.Equal(0, balance));
    }

    public static TheoryData<AccountingEventModel> ReversibleEvents() =>
    [
        Event(AccountingEventKind.WalletReceived, 100_000_000, 0, "wallet:a:0:in", ("source", "external")),
        Event(AccountingEventKind.WalletSent, -60_000_000, 1_000_000, "wsend:a"),
        Event(AccountingEventKind.ChannelForceClosed, -700_000_500, 9_000_000, "chan:a:forceclosed:b",
              ("pendingMsat", "690000000"), ("lostMsat", "1000500")),
        Resolution(AccountingEventKind.OutputResolved, amount: 29_600_000, fee: 400_000, pendingOut: 0, pendingIn: 0,
                   wallet: 29_600_000, counted: false, by: "us", ("valueBookedBy", "invoice")),
        Event(AccountingEventKind.ChannelClosedMutual, -1_000_000_000, 1_000_000, "chan:a:closed:b"),
        Event(AccountingEventKind.AnchorCpfpFee, 0, 2_000_000, "cpfp:a"),
        Event(AccountingEventKind.LiquidityFeePaid, -4_500_000, 4_500_000, "chan:a:liquidity:b", ("role", "buyer")),
        Event(AccountingEventKind.LiquidityFeeEarned, 4_500_000, 0, "chan:c:liquidity:d", ("role", "seller"))
    ];

    [Fact]
    public void Given_AReversalOfAnEventThatPostedNothing_When_Posted_Then_NothingMoves()
    {
        // Arrange
        var books = new BooksSimulator();
        var bump = Event(AccountingEventKind.SweepFeeBump, 0, 1_000_000, "sweep:a:fee");
        books.Apply(bump);

        // Act
        var negated = books.Apply(AccountingConfirmations.CreateReversal(bump, s_at, 99));

        // Assert
        Assert.NotNull(negated);
        Assert.Empty(negated.Postings);
    }

    [Fact]
    public void Given_AReversalOfAnUnknownEvent_When_Posted_Then_NothingMovesAndItSaysSo()
    {
        // Arrange
        var original = Event(AccountingEventKind.WalletSent, -60_000_000, 1_000_000, "wsend:never-seen");
        var reversal = AccountingConfirmations.CreateReversal(original, s_at, 99);

        // Act
        var result = AccountingPostingRules.Evaluate(reversal, _ => null);

        // Assert
        Assert.Empty(result.Postings);
        Assert.Contains("wsend:never-seen", result.Note);
    }

    [Fact]
    public void Given_AReversalOfAWalletDepositFromBeforeTheFeed_When_Posted_Then_TheWalletMovesAgainstOpening()
    {
        // Arrange: what the chain monitor writes for a deposit it never recorded
        var standIn = Event(AccountingEventKind.WalletReceived, 100_000_000, 0, "wallet:a:1:in");
        var reversal = AccountingConfirmations.CreateReversal(standIn, s_at, 100, unrecorded: true);

        // Act
        var result = AccountingPostingRules.Evaluate(reversal, _ => null);

        // Assert
        AssertPostings(result.Postings, (AccountRole.Wallet, -100_000_000), (AccountRole.Opening, 100_000_000));
        Assert.NotNull(result.Note);
    }

    [Fact]
    public void Given_AReversalOfAnUnbalancedEntry_When_Posted_Then_TheBugGuardThrows()
    {
        // Arrange: an entry that does not balance (a corrupted book)
        var original = Event(AccountingEventKind.WalletSent, -60_000_000, 1_000_000, "wsend:a");
        var corrupted = new AccountingEntry(1, original.EventKey, original.Kind, s_at, null, null,
                                            [new AccountingPosting(AccountRole.Wallet, 5)]);
        var reversal = AccountingConfirmations.CreateReversal(original, s_at, 99);

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => AccountingPostingRules.Post(reversal, _ => corrupted));
    }

    #endregion

    #region Properties

    [Fact]
    public void Given_RandomEventsOfEveryKind_When_Posted_Then_EveryEntrySumsToZero()
    {
        // Arrange
        var random = new Random(602);
        var kinds = Enum.GetValues<AccountingEventKind>();
        string[] keys =
        [
            "pendingMsat", "lostMsat", "pendingOutMsat", "pendingInMsat", "walletMsat", "valueMsat", "valueBookedBy",
            "source", "selfPayment", "memo", "openingBalance", "bucket", "note", "reverses", "originalKind",
            "unrecorded", "htlcRoundingMsat", "htlcDirection", "funder"
        ];
        var books = new BooksSimulator();
        var applied = new List<AccountingEventModel>();

        // Act
        for (var i = 0; i < 5_000; i++)
        {
            var details = new List<(string, string?)>();
            foreach (var key in keys)
                if (random.Next(3) == 0)
                    details.Add((key, RandomValue(random, key, applied)));

            var kind = kinds[random.Next(kinds.Length)];
            var accountingEvent = Event(kind, random.NextInt64(-1_000_000_000_000, 1_000_000_000_000),
                                        random.NextInt64(0, 1_000_000_000_000), $"k:{i}", details.ToArray());
            var entry = books.Apply(accountingEvent);
            applied.Add(accountingEvent);

            // Assert
            Assert.NotNull(entry);
            Assert.True(entry.IsBalanced, $"{kind} {i} does not balance");
            Assert.All(entry.Postings, p => Assert.NotEqual(0, p.AmountMsat));
            Assert.Equal(entry.Postings.Count, entry.Postings.Select(p => p.Account).Distinct().Count());
        }

        Assert.Equal(0, books.Total);
    }

    private static string? RandomValue(Random random, string key, IReadOnlyList<AccountingEventModel> applied) =>
        key switch
        {
            "valueBookedBy" => random.Next(2) == 0 ? "invoice" : "payment",
            "source" => new[] { "external", "broadcast", "channel", "wallet" }[random.Next(4)],
            "selfPayment" or "memo" or "openingBalance" or "unrecorded" or "funder" =>
                random.Next(2) == 0 ? "true" : "false",
            "htlcDirection" => random.Next(2) == 0 ? "offered" : "incoming",
            "htlcRoundingMsat" => random.Next(-1, 1_001).ToString(),
            "bucket" => new[] { "channel", "wallet", "onchain-pending", "cutover", "nonsense" }[random.Next(5)],
            "note" => random.Next(2) == 0 ? AccountingDetailKeys.MergedNote : "something else",
            "reverses" => applied.Count > 0 ? applied[random.Next(applied.Count)].EventKey : "nothing",
            "originalKind" => random.Next(2) == 0 ? "WalletReceived" : "WalletOutputSpent",
            _ => random.Next(10) == 0
                     ? "not a number"
                     : random.NextInt64(-1_000_000_000_000, 1_000_000_000_000).ToString()
        };

    #endregion

    #region Describe

    [Fact]
    public void Given_EventsOfSeveralKinds_When_Described_Then_TheTextNamesWhatHappened()
    {
        // Act / Assert
        Assert.Equal("Invoice settled: coffee",
                     AccountingPostingRules.Describe(Event(AccountingEventKind.InvoiceSettled, 1, 0,
                                                           ("description", "coffee"))));
        Assert.Equal("Keysend received",
                     AccountingPostingRules.Describe(Event(AccountingEventKind.InvoiceSettled, 1, 0,
                                                           ("kind", "keysend"))));
        Assert.Equal("Rebalance received: loop",
                     AccountingPostingRules.Describe(Event(AccountingEventKind.InvoiceSettled, 1, 0,
                                                           ("selfPayment", "true"), ("description", "loop"))));
        Assert.Equal("Rebalance: loop",
                     AccountingPostingRules.Describe(Event(AccountingEventKind.PaymentSucceeded, -1, 0,
                                                           ("selfPayment", "true"), ("description", "loop"))));
        Assert.Equal("Forward settled: 400x1x0 -> 400x2x1",
                     AccountingPostingRules.Describe(Event(AccountingEventKind.ForwardSettled, 1, 0,
                                                           ("incomingScid", "400x1x0"), ("outgoingScid", "400x2x1"))));
        Assert.Equal("Trampoline relay settled: 400x1x0 -> ?",
                     AccountingPostingRules.Describe(Event(AccountingEventKind.TrampolineRelaySettled, 1, 0,
                                                           ("incomingScid", "400x1x0"))));
        Assert.Equal("Channel force-closed: RemoteCommitment",
                     AccountingPostingRules.Describe(Event(AccountingEventKind.ChannelForceClosed, -1, 0,
                                                           ("closeKind", "RemoteCommitment"))));
        Assert.Equal("Output given up: OurAnchor",
                     AccountingPostingRules.Describe(Event(AccountingEventKind.OutputResolved, -1, 0,
                                                           ("resolvedBy", "ignored"), ("descriptor", "OurAnchor"))));
        Assert.Equal("Deposit",
                     AccountingPostingRules.Describe(Event(AccountingEventKind.WalletReceived, 1, 0,
                                                           ("source", "external"))));
        Assert.Equal("Splice out locked",
                     AccountingPostingRules.Describe(Event(AccountingEventKind.SpliceLocked, -1, 0)));
        Assert.Equal("Opening balance: wallet",
                     AccountingPostingRules.Describe(Event(AccountingEventKind.OpeningBalance, 1, 0, "open:wallet")));
        Assert.Equal("Reversal of WalletSent: wsend:a",
                     AccountingPostingRules.Describe(AccountingConfirmations.CreateReversal(
                                                         Event(AccountingEventKind.WalletSent, -1, 0, "wsend:a"),
                                                         s_at, 9)));
    }

    #endregion

    private static IReadOnlyList<AccountingPosting> Post(AccountingEventModel accountingEvent) =>
        AccountingPostingRules.Post(accountingEvent, _ => null);

    private static AccountingPostingResult Evaluate(AccountingEventModel accountingEvent) =>
        AccountingPostingRules.Evaluate(accountingEvent, _ => null);

    private static long Amount(IEnumerable<AccountingPosting> postings, AccountRole account) =>
        postings.Where(p => p.Account == account).Sum(p => p.AmountMsat);

    private static void AssertPostings(IReadOnlyList<AccountingPosting> postings,
                                       params (AccountRole Account, long AmountMsat)[] expected)
    {
        Assert.Equal(expected.OrderBy(e => e.Account), postings.Select(p => (p.Account, p.AmountMsat))
                                                               .OrderBy(p => p.Account));
        Assert.Equal(0, postings.Sum(p => p.AmountMsat));
    }

    private static AccountingEventModel Event(AccountingEventKind kind, long amountMsat, long feeMsat,
                                              params (string Key, string? Value)[] details) =>
        Event(kind, amountMsat, feeMsat, $"{kind}:test", details);

    private static AccountingEventModel Event(AccountingEventKind kind, long amountMsat, long feeMsat, string key,
                                              params (string Key, string? Value)[] details) =>
        new()
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = s_at,
            BlockHeight = 100,
            TxId = s_txId,
            AmountMsat = amountMsat,
            FeeMsat = feeMsat,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create(details)
        };

    /// <summary>A resolution in <c>OnchainAccounting.Resolution</c>'s format.</summary>
    private static AccountingEventModel Resolution(AccountingEventKind kind, long amount, long fee, long pendingOut,
                                                   long pendingIn, long wallet, bool counted, string by,
                                                   params (string Key, string? Value)[] extra) =>
        Event(kind, amount, fee, $"out:{kind}:{amount}",
        [
            ("pendingOutMsat", pendingOut.ToString()), ("pendingInMsat", pendingIn.ToString()),
            ("walletMsat", wallet.ToString()), ("counted", counted ? "true" : "false"), ("resolvedBy", by),
            ("closeKind", "LocalCommitment"), .. extra
        ]);
}
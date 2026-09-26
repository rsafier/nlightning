namespace NLightning.Domain.Tests.Onchain;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Onchain.Planners;

/// <summary>
/// BOLT 5 plan O3-T2, O4-T2, O5-T2: the <see cref="OutputResolutionPlanner"/> table, one row (or a few) per
/// requirement of plan §6.3 (B5-LCL-*), §6.4 (B5-RMT-*) and §6.5 (B5-REV-*), plus the no-output rows and the 100-block
/// rule (B5-GEN-02).
/// </summary>
public class OutputResolutionPlannerTests
{
    private const uint CommitHeight = 1_000;
    private const ushort ToSelfDelay = 144;
    private const uint Cltv = 1_100;

    private static readonly byte[] s_preimage = Enumerable.Repeat((byte)7, 32).ToArray();

    #region B5-LCL-01 / B5-LCL-02: to_local and the peer's output on our commitment

    [Fact]
    public void Given_ToLocalBeforeCsv_When_Planning_Then_WaitUntilTheTipBeforeTheFirstSpendableBlock()
    {
        // Act: a CSV spend of an output confirmed at h can enter block h + to_self_delay
        var plan = OutputResolutionPlanner.Plan(Output(OutputDescriptorKind.DelayedToLocal, csv: ToSelfDelay),
                                                Facts(CommitHeight + ToSelfDelay - 2));

        // Assert
        Assert.Equal(OutputResolutionState.Unresolved, plan.State);
        var wait = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.Wait, "B5-LCL-01", CommitHeight + ToSelfDelay - 1),
                     (wait.Kind, wait.RequirementId, wait.WaitUntilHeight!.Value));
    }

    [Fact]
    public void Given_ToLocalAtCsv_When_Planning_Then_SweptWithDelayedKey()
    {
        // Act: broadcast at tip h + csv - 1, mined at h + csv
        var plan = OutputResolutionPlanner.Plan(Output(OutputDescriptorKind.DelayedToLocal, csv: ToSelfDelay),
                                                Facts(CommitHeight + ToSelfDelay - 1));

        // Assert
        var sweep = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.Sweep, SweepSpendKind.DelayedOutput, false),
                     (sweep.Kind, sweep.SpendKind!.Value, sweep.OnSecondLevel));
    }

    [Theory]
    [InlineData(1, OutputResolutionState.Resolved)]
    [InlineData(99, OutputResolutionState.Resolved)]
    [InlineData(100, OutputResolutionState.IrrevocablyResolved)]
    public void Given_ToLocalSweptByUs_When_Planning_Then_IrrevocableAt100Blocks(uint depth,
                                                                                  OutputResolutionState expected)
    {
        // Arrange (B5-GEN-02)
        const uint sweepHeight = CommitHeight + ToSelfDelay;

        // Act
        var plan = OutputResolutionPlanner.Plan(Output(OutputDescriptorKind.DelayedToLocal, csv: ToSelfDelay),
                                                Facts(sweepHeight + depth - 1, Spend(sweepHeight, byUs: true)));

        // Assert
        Assert.Equal(expected, plan.State);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Given_ToLocalTakenByPeer_When_Planning_Then_LostFundsAlert()
    {
        // Act: only a revocation spend can take it, which S1/NL-189 must make impossible
        var plan = OutputResolutionPlanner.Plan(Output(OutputDescriptorKind.DelayedToLocal, csv: ToSelfDelay),
                                                Facts(CommitHeight + 10,
                                                      Spend(CommitHeight + 5, false, HtlcSpendPath.Unknown)));

        // Assert
        Assert.True(plan.Has(ResolutionActionKind.AlertLostFunds));
        Assert.Equal(OutputResolutionState.Resolved, plan.State);
    }

    [Theory]
    [InlineData(OutputDescriptorKind.PeerOutput)]
    [InlineData(OutputDescriptorKind.PeerAnchor)]
    [InlineData(OutputDescriptorKind.Unknown)]
    public void Given_OutputNotOurs_When_Planning_Then_ResolvedByTheCommitment(OutputDescriptorKind kind)
    {
        // Act (B5-LCL-02, B5-RMT-02)
        var young = OutputResolutionPlanner.Plan(Output(kind), Facts(CommitHeight + 5));
        var old = OutputResolutionPlanner.Plan(Output(kind), Facts(CommitHeight + 99));

        // Assert
        Assert.Equal((OutputResolutionState.Resolved, 0), (young.State, young.Actions.Count));
        Assert.Equal((OutputResolutionState.IrrevocablyResolved, 0), (old.State, old.Actions.Count));
    }

    #endregion

    #region B5-LCL-LO-*: an HTLC we offered, on our commitment

    [Fact]
    public void Given_OurOfferedHtlcBeforeExpiry_When_Planning_Then_WaitForCltv()
    {
        // Act
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalOfferedHtlc, HtlcDirection.Outgoing),
                                                Facts(Cltv - 1));

        // Assert (B5-LCL-LO-02: timed out means tip >= cltv_expiry)
        var wait = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.Wait, Cltv), (wait.Kind, wait.WaitUntilHeight!.Value));
        Assert.Equal(OutputResolutionState.Unresolved, plan.State);
    }

    [Fact]
    public void Given_OurOfferedHtlcTimedOut_When_Planning_Then_HtlcTimeoutTxBroadcast()
    {
        // Act
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalOfferedHtlc, HtlcDirection.Outgoing),
                                                Facts(Cltv));

        // Assert (B5-LCL-LO-02)
        var action = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.BroadcastHtlcTimeoutTx, "B5-LCL-LO-02"),
                     (action.Kind, action.RequirementId));
    }

    [Fact]
    public void Given_PeerClaimedOurOfferedHtlcWithPreimage_When_Planning_Then_UpstreamFulfilledAtOnce()
    {
        // Act: depth 1, no waiting (B5-LCL-LO-01)
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalOfferedHtlc, HtlcDirection.Outgoing),
                                                Facts(CommitHeight + 1,
                                                      Spend(CommitHeight + 1, false, HtlcSpendPath.PreimageClaim,
                                                            s_preimage)));

        // Assert
        var fulfill = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.RaiseFulfilled, "B5-LCL-LO-01"), (fulfill.Kind, fulfill.RequirementId));
        Assert.Equal(s_preimage, fulfill.Preimage);
        Assert.Equal(OutputResolutionState.Resolved, plan.State);
    }

    [Fact]
    public void Given_PreimageClaimAlreadyReportedUpstream_When_Planning_Then_NoEventAgain()
    {
        // Act
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalOfferedHtlc, HtlcDirection.Outgoing),
                                                Facts(CommitHeight + 1,
                                                      Spend(CommitHeight + 1, false, HtlcSpendPath.PreimageClaim,
                                                            s_preimage)) with
                                                { UpstreamResolved = true });

        // Assert
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Given_PreimageKnownOffChainBeforeExpiry_When_Planning_Then_FulfilledAndStillWaitingForCltv()
    {
        // Act: the peer fulfilled off chain before the close; the output is still ours to time out if it never claims
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalOfferedHtlc, HtlcDirection.Outgoing),
                                                Facts(Cltv - 10) with { AllowedPreimage = s_preimage });

        // Assert
        Assert.Equal([ResolutionActionKind.RaiseFulfilled, ResolutionActionKind.Wait],
                     plan.Actions.Select(a => a.Kind));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void Given_OurHtlcTimeoutConfirmed_When_Planning_Then_UpstreamFailedAtReasonableDepth(uint depth,
                                                                                                  bool failed)
    {
        // Arrange (B5-LCL-LO-03)
        const uint timeoutHeight = Cltv + 1;

        // Act
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalOfferedHtlc, HtlcDirection.Outgoing),
                                                SecondLevelFacts(timeoutHeight + depth - 1,
                                                                 Spend(timeoutHeight, true,
                                                                       HtlcSpendPath.HtlcTimeoutTransaction)));

        // Assert: fail at depth 6, else wait for it; the second-level output waits for its CSV either way
        Assert.Equal(failed, plan.Has(ResolutionActionKind.RaiseFailed));
        if (!failed)
            Assert.Equal(timeoutHeight + 5, plan.Actions[0].WaitUntilHeight);
        var secondLevel = plan.Actions.Last();
        Assert.Equal((ResolutionActionKind.Wait, timeoutHeight + ToSelfDelay - 1),
                     (secondLevel.Kind, secondLevel.WaitUntilHeight!.Value));
        Assert.Equal(OutputResolutionState.Unresolved, plan.State);
    }

    [Fact]
    public void Given_OurHtlcTimeoutOutputAtCsv_When_Planning_Then_SecondLevelOutputSwept()
    {
        // Arrange
        const uint timeoutHeight = Cltv + 1;

        // Act
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalOfferedHtlc, HtlcDirection.Outgoing),
                                                SecondLevelFacts(timeoutHeight + ToSelfDelay - 1,
                                                                 Spend(timeoutHeight, true,
                                                                       HtlcSpendPath.HtlcTimeoutTransaction))
                                                   with
                                                { UpstreamResolved = true });

        // Assert (B5-LCL-LO-03: <local_delayedsig> 0 after to_self_delay)
        var sweep = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.Sweep, SweepSpendKind.DelayedOutput, true),
                     (sweep.Kind, sweep.SpendKind!.Value, sweep.OnSecondLevel));
    }

    [Theory]
    [InlineData(99, OutputResolutionState.Resolved)]
    [InlineData(100, OutputResolutionState.IrrevocablyResolved)]
    public void Given_SecondLevelOutputSwept_When_Planning_Then_StateFollowsTheSweepDepth(
        uint depth, OutputResolutionState expected)
    {
        // Arrange
        const uint timeoutHeight = Cltv + 1;
        const uint sweepHeight = timeoutHeight + ToSelfDelay;

        // Act
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalOfferedHtlc, HtlcDirection.Outgoing),
                                                SecondLevelFacts(sweepHeight + depth - 1,
                                                                 Spend(timeoutHeight, true,
                                                                       HtlcSpendPath.HtlcTimeoutTransaction))
                                                   with
                                                {
                                                    SecondLevelSpend = Spend(sweepHeight, true),
                                                    UpstreamResolved = true
                                                });

        // Assert
        Assert.Equal(expected, plan.State);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Given_OurHtlcTimeoutConfirmedButPreimageKnown_When_Planning_Then_UpstreamFulfilledNotFailed()
    {
        // Arrange: the peer fulfilled off chain but let us time the output out
        const uint timeoutHeight = Cltv + 1;

        // Act
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalOfferedHtlc, HtlcDirection.Outgoing),
                                                SecondLevelFacts(timeoutHeight + 10,
                                                                 Spend(timeoutHeight, true,
                                                                       HtlcSpendPath.HtlcTimeoutTransaction))
                                                   with
                                                { AllowedPreimage = s_preimage });

        // Assert
        Assert.True(plan.Has(ResolutionActionKind.RaiseFulfilled));
        Assert.False(plan.Has(ResolutionActionKind.RaiseFailed));
    }

    [Fact]
    public void Given_SecondLevelConfirmedWithoutCsv_When_Planning_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => OutputResolutionPlanner.Plan(
                                             Htlc(OutputDescriptorKind.LocalOfferedHtlc, HtlcDirection.Outgoing),
                                             Facts(Cltv + 10,
                                                   Spend(Cltv + 1, true, HtlcSpendPath.HtlcTimeoutTransaction))));
    }

    #endregion

    #region B5-LCL-LO-04, B5-RMT-LO-03, B5-REV-RES-03: committed HTLCs without an output

    [Fact]
    public void Given_OurHtlcWithoutOutputAndPreimageKnown_When_Planning_Then_FulfilledAtOnce()
    {
        // Act
        var plan = OutputResolutionPlanner.PlanHtlcWithoutOutput(
            SpecHtlc(HtlcDirection.Outgoing), new HtlcWithoutOutputFacts(CommitHeight, CommitHeight, s_preimage));

        // Assert
        Assert.Equal(ResolutionActionKind.RaiseFulfilled, Assert.Single(plan.Actions).Kind);
    }

    [Theory]
    [InlineData(5, ResolutionActionKind.Wait)]
    [InlineData(6, ResolutionActionKind.RaiseFailed)]
    public void Given_OurHtlcWithoutOutput_When_CommitmentReasonablyDeep_Then_Failed(uint depth,
                                                                                     ResolutionActionKind expected)
    {
        // Act
        var plan = OutputResolutionPlanner.PlanHtlcWithoutOutput(
            SpecHtlc(HtlcDirection.Outgoing), new HtlcWithoutOutputFacts(CommitHeight + depth - 1, CommitHeight));

        // Assert
        var action = Assert.Single(plan.Actions);
        Assert.Equal(expected, action.Kind);
        if (expected == ResolutionActionKind.Wait)
            Assert.Equal(CommitHeight + 5, action.WaitUntilHeight);
    }

    [Fact]
    public void Given_OurHtlcWithoutOutputInAnyValidCommitment_When_Planning_Then_FailedAtOnce()
    {
        // Act: trimmed everywhere (B5-LCL-LO-04 "MAY fail sooner")
        var plan = OutputResolutionPlanner.PlanHtlcWithoutOutput(
            SpecHtlc(HtlcDirection.Outgoing),
            new HtlcWithoutOutputFacts(CommitHeight, CommitHeight, OutputInAnyValidCommitment: false));

        // Assert
        Assert.Equal(ResolutionActionKind.RaiseFailed, Assert.Single(plan.Actions).Kind);
    }

    [Fact]
    public void Given_TheirHtlcWithoutOutputOrUpstreamDone_When_Planning_Then_NothingToDo()
    {
        // Act
        var incoming = OutputResolutionPlanner.PlanHtlcWithoutOutput(
            SpecHtlc(HtlcDirection.Incoming), new HtlcWithoutOutputFacts(CommitHeight + 50, CommitHeight));
        var done = OutputResolutionPlanner.PlanHtlcWithoutOutput(
            SpecHtlc(HtlcDirection.Outgoing),
            new HtlcWithoutOutputFacts(CommitHeight + 99, CommitHeight, UpstreamResolved: true));

        // Assert
        Assert.Empty(incoming.Actions);
        Assert.Empty(done.Actions);
        Assert.Equal(OutputResolutionState.IrrevocablyResolved, done.State);
    }

    #endregion

    #region B5-LCL-RO-*: an HTLC the peer offered, on our commitment

    [Fact]
    public void Given_TheirHtlcWithAllowedPreimage_When_Planning_Then_HtlcSuccessTxBroadcast()
    {
        // Act (B5-LCL-RO-01)
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalReceivedHtlc, HtlcDirection.Incoming),
                                                Facts(CommitHeight + 1) with { AllowedPreimage = s_preimage });

        // Assert
        var action = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.BroadcastHtlcSuccessTx, Cltv), (action.Kind, action.DeadlineHeight!.Value));
        Assert.Equal(s_preimage, action.Preimage);
    }

    [Fact]
    public void Given_TheirHtlcWithoutAllowedPreimage_When_Planning_Then_NotClaimed()
    {
        // Act (B5-LCL-RO-02: an invoice preimage the switch did not accept for this HTLC is never passed in)
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalReceivedHtlc, HtlcDirection.Incoming),
                                                Facts(CommitHeight + 1));

        // Assert
        Assert.Equal(ResolutionActionKind.Wait, Assert.Single(plan.Actions).Kind);
        Assert.False(plan.Has(ResolutionActionKind.BroadcastHtlcSuccessTx));
    }

    [Fact]
    public void Given_TheirHtlcNotIrrevocablyCommitted_When_PreimageKnown_Then_NotSpent()
    {
        // Act (B5-LCL-RO-03)
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalReceivedHtlc, HtlcDirection.Incoming),
                                                Facts(CommitHeight + 1) with
                                                {
                                                    AllowedPreimage = s_preimage,
                                                    RemoteIrrevocablyCommitted = false
                                                });

        // Assert
        var wait = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.Wait, "B5-LCL-RO-03"), (wait.Kind, wait.RequirementId));
    }

    [Fact]
    public void Given_TheirHtlcExpiredWithoutPreimage_When_Planning_Then_IrrevocablyResolved()
    {
        // Act (B5-LCL-RO-04)
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalReceivedHtlc, HtlcDirection.Incoming),
                                                Facts(Cltv));

        // Assert
        Assert.Equal(OutputResolutionState.IrrevocablyResolved, plan.State);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Given_PeerTimedOutTheirHtlc_When_Planning_Then_ResolvedWithoutAction()
    {
        // Act
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalReceivedHtlc, HtlcDirection.Incoming),
                                                Facts(Cltv + 3, Spend(Cltv + 1, false, HtlcSpendPath.TimeoutClaim)));

        // Assert
        Assert.Equal(OutputResolutionState.Resolved, plan.State);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void Given_OurHtlcSuccessConfirmed_When_Planning_Then_SecondLevelSweptAfterCsvWithoutUpstreamEvent()
    {
        // Arrange
        const uint successHeight = CommitHeight + 2;

        // Act
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.LocalReceivedHtlc, HtlcDirection.Incoming),
                                                SecondLevelFacts(successHeight + ToSelfDelay - 1,
                                                                 Spend(successHeight, true,
                                                                       HtlcSpendPath.HtlcSuccessTransaction,
                                                                       s_preimage)));

        // Assert
        var sweep = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.Sweep, true), (sweep.Kind, sweep.OnSecondLevel));
    }

    #endregion

    #region B5-RMT-*: the peer's commitment

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    public void Given_OurToRemote_When_Planning_Then_SweptAtOnce(ushort csv)
    {
        // Act (D5, B5-RMT-02; the anchors form has 1 OP_CSV: it can enter the block after the commitment's)
        var plan = OutputResolutionPlanner.Plan(Output(OutputDescriptorKind.PaymentToRemote, csv: csv),
                                                Facts(CommitHeight));

        // Assert
        var sweep = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.Sweep, SweepSpendKind.PaymentToRemote),
                     (sweep.Kind, sweep.SpendKind!.Value));
    }

    [Fact]
    public void Given_OurToRemoteSpentByOthers_When_Planning_Then_LostFundsAlert()
    {
        // Act
        var plan = OutputResolutionPlanner.Plan(Output(OutputDescriptorKind.PaymentToRemote),
                                                Facts(CommitHeight + 2, Spend(CommitHeight + 1, false)));

        // Assert
        Assert.True(plan.Has(ResolutionActionKind.AlertLostFunds));
    }

    [Fact]
    public void Given_PeerHtlcSuccessRevealedPreimage_When_Planning_Then_UpstreamFulfilled()
    {
        // Act (B5-RMT-LO-01)
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RemoteReceivedHtlc, HtlcDirection.Outgoing),
                                                Facts(CommitHeight + 2,
                                                      Spend(CommitHeight + 1, false,
                                                            HtlcSpendPath.HtlcSuccessTransaction, s_preimage)));

        // Assert
        var action = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.RaiseFulfilled, "B5-RMT-LO-01"), (action.Kind, action.RequirementId));
    }

    [Theory]
    [InlineData(Cltv - 1, ResolutionActionKind.Wait)]
    [InlineData(Cltv, ResolutionActionKind.Sweep)]
    public void Given_OurOfferedHtlcOnTheirCommitment_When_TimedOut_Then_TimeoutClaim(uint tip,
                                                                                      ResolutionActionKind expected)
    {
        // Act (B5-RMT-LO-02)
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RemoteReceivedHtlc, HtlcDirection.Outgoing),
                                                Facts(tip));

        // Assert
        var action = Assert.Single(plan.Actions);
        Assert.Equal(expected, action.Kind);
        if (expected == ResolutionActionKind.Sweep)
            Assert.Equal(SweepSpendKind.HtlcTimeoutClaim, action.SpendKind);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void Given_OurTimeoutClaimConfirmed_When_Planning_Then_UpstreamFailedAtReasonableDepth(uint depth,
                                                                                                   bool failed)
    {
        // Act
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RemoteReceivedHtlc, HtlcDirection.Outgoing),
                                                Facts(Cltv + depth, Spend(Cltv + 1, true, HtlcSpendPath.TimeoutClaim)));

        // Assert
        Assert.Equal(failed, plan.Has(ResolutionActionKind.RaiseFailed));
        Assert.Equal(!failed, plan.Has(ResolutionActionKind.Wait));
        Assert.Equal(OutputResolutionState.Resolved, plan.State);
    }

    [Fact]
    public void Given_TheirOfferedHtlcWithAllowedPreimage_When_Planning_Then_PreimageClaim()
    {
        // Act (B5-RMT-RO-01)
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RemoteOfferedHtlc, HtlcDirection.Incoming),
                                                Facts(CommitHeight + 1) with { AllowedPreimage = s_preimage });

        // Assert
        var claim = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.Sweep, SweepSpendKind.HtlcPreimageClaim, Cltv),
                     (claim.Kind, claim.SpendKind!.Value, claim.DeadlineHeight!.Value));
        Assert.Equal(s_preimage, claim.Preimage);
    }

    [Fact]
    public void Given_TheirOfferedHtlcNotIrrevocablyCommitted_When_Planning_Then_NotSpentAndResolvedAtExpiry()
    {
        // Act (B5-RMT-RO-02)
        var before = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RemoteOfferedHtlc, HtlcDirection.Incoming),
                                                  Facts(Cltv - 1) with
                                                  {
                                                      AllowedPreimage = s_preimage,
                                                      RemoteIrrevocablyCommitted = false
                                                  });
        var after = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RemoteOfferedHtlc, HtlcDirection.Incoming),
                                                 Facts(Cltv) with { RemoteIrrevocablyCommitted = false });

        // Assert
        Assert.Equal(ResolutionActionKind.Wait, Assert.Single(before.Actions).Kind);
        Assert.Equal(OutputResolutionState.IrrevocablyResolved, after.State);
    }

    [Theory]
    [InlineData(true, HtlcSpendPath.PreimageClaim, false)]
    [InlineData(false, HtlcSpendPath.HtlcTimeoutTransaction, false)]
    [InlineData(false, HtlcSpendPath.Unknown, true)]
    public void Given_TheirOfferedHtlcSpent_When_Planning_Then_Resolved(bool byUs, HtlcSpendPath path, bool alert)
    {
        // Act: our claim, or its HTLC-timeout; anything else is unexpected
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RemoteOfferedHtlc, HtlcDirection.Incoming),
                                                Facts(Cltv + 5, Spend(Cltv + 1, byUs, path)));

        // Assert
        Assert.Equal(OutputResolutionState.Resolved, plan.State);
        Assert.Equal(alert, plan.Has(ResolutionActionKind.AlertLostFunds));
    }

    #endregion

    #region B5-REV-*: a revoked commitment

    [Fact]
    public void Given_RevokedToLocal_When_Planning_Then_PenaltyBeforeTheirCsv()
    {
        // Act (B5-REV-03)
        var plan = OutputResolutionPlanner.Plan(Output(OutputDescriptorKind.RevokedToLocal, csv: ToSelfDelay),
                                                Facts(CommitHeight));

        // Assert
        var penalty = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.Sweep, SweepSpendKind.RevokedDelayedOutput, CommitHeight + ToSelfDelay),
                     (penalty.Kind, penalty.SpendKind!.Value, penalty.DeadlineHeight!.Value));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Given_RevokedToLocalSpent_When_Planning_Then_ResolvedAndAlertIfCheaterWon(bool byUs, bool alert)
    {
        // Act
        var plan = OutputResolutionPlanner.Plan(Output(OutputDescriptorKind.RevokedToLocal, csv: ToSelfDelay),
                                                Facts(CommitHeight + 200, Spend(CommitHeight + 150, byUs)));

        // Assert
        Assert.Equal(alert, plan.Has(ResolutionActionKind.AlertLostFunds));
        Assert.Equal(OutputResolutionState.Resolved, plan.State);
    }

    [Theory]
    [InlineData(HtlcDirection.Incoming, "B5-REV-04", Cltv)]
    [InlineData(HtlcDirection.Outgoing, "B5-REV-05", CommitHeight + 1)]
    public void Given_RevokedHtlc_When_Planning_Then_PenaltyWithItsDeadline(HtlcDirection direction,
                                                                            string requirement, uint deadline)
    {
        // Act: their offered HTLC can be timed out from cltv_expiry, ours taken with the preimage at once
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RevokedHtlc, direction),
                                                Facts(CommitHeight));

        // Assert
        var penalty = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.Sweep, SweepSpendKind.RevokedHtlc, requirement, deadline),
                     (penalty.Kind, penalty.SpendKind!.Value, penalty.RequirementId, penalty.DeadlineHeight!.Value));
    }

    [Fact]
    public void Given_CheaterHtlcTimeoutConfirmedFirst_When_Planning_Then_SecondLevelPenalized()
    {
        // Act (B5-REV-06, B5-REV-09: re-plan on the second-level output)
        const uint timeoutHeight = Cltv + 1;
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RevokedHtlc, HtlcDirection.Incoming),
                                                SecondLevelFacts(timeoutHeight,
                                                                 Spend(timeoutHeight, false,
                                                                       HtlcSpendPath.HtlcTimeoutTransaction)));

        // Assert
        var penalty = Assert.Single(plan.Actions);
        Assert.Equal((ResolutionActionKind.Sweep, SweepSpendKind.RevokedDelayedOutput, true,
                      timeoutHeight + ToSelfDelay),
                     (penalty.Kind, penalty.SpendKind!.Value, penalty.OnSecondLevel, penalty.DeadlineHeight!.Value));
        Assert.Equal(OutputResolutionState.Unresolved, plan.State);
    }

    [Fact]
    public void Given_CheaterHtlcSuccessRevealsPreimage_When_Planning_Then_UpstreamFulfilledAtOnceAndPenalized()
    {
        // Act (B5-REV-07, B5-REV-RES-01)
        const uint successHeight = CommitHeight + 1;
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RevokedHtlc, HtlcDirection.Outgoing),
                                                SecondLevelFacts(successHeight,
                                                                 Spend(successHeight, false,
                                                                       HtlcSpendPath.HtlcSuccessTransaction,
                                                                       s_preimage)));

        // Assert
        Assert.Equal([ResolutionActionKind.RaiseFulfilled, ResolutionActionKind.Sweep],
                     plan.Actions.Select(a => a.Kind));
        Assert.Equal("B5-REV-RES-01", plan.Actions[0].RequirementId);
        Assert.True(plan.Actions[1].OnSecondLevel);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Given_SecondLevelOutputSpent_When_Planning_Then_ResolvedAndAlertIfCheaterWon(bool byUs, bool alert)
    {
        // Arrange
        const uint timeoutHeight = Cltv + 1;

        // Act
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RevokedHtlc, HtlcDirection.Incoming),
                                                SecondLevelFacts(timeoutHeight + 200,
                                                                 Spend(timeoutHeight, false,
                                                                       HtlcSpendPath.HtlcTimeoutTransaction))
                                                   with
                                                { SecondLevelSpend = Spend(timeoutHeight + 150, byUs) });

        // Assert
        Assert.Equal(alert, plan.Has(ResolutionActionKind.AlertLostFunds));
        Assert.Equal(OutputResolutionState.Resolved, plan.State);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void Given_OurOfferedHtlcPenalized_When_Planning_Then_UpstreamFailedAtReasonableDepth(uint depth,
                                                                                                  bool failed)
    {
        // Act (B5-REV-RES-02)
        const uint penaltyHeight = CommitHeight + 1;
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RevokedHtlc, HtlcDirection.Outgoing),
                                                Facts(penaltyHeight + depth - 1,
                                                      Spend(penaltyHeight, true, HtlcSpendPath.Revocation)));

        // Assert
        Assert.Equal(failed, plan.Has(ResolutionActionKind.RaiseFailed));
        if (failed)
            Assert.Equal("B5-REV-RES-02", plan.Get(ResolutionActionKind.RaiseFailed)!.RequirementId);
    }

    [Fact]
    public void Given_TheirOfferedHtlcPenalized_When_Planning_Then_NoUpstreamEvent()
    {
        // Act: nothing of ours depends on an HTLC the peer offered
        var plan = OutputResolutionPlanner.Plan(Htlc(OutputDescriptorKind.RevokedHtlc, HtlcDirection.Incoming),
                                                Facts(CommitHeight + 20,
                                                      Spend(CommitHeight + 1, true, HtlcSpendPath.Revocation)));

        // Assert
        Assert.Empty(plan.Actions);
        Assert.Equal(OutputResolutionState.Resolved, plan.State);
    }

    #endregion

    #region Descriptor validation

    [Fact]
    public void Given_HtlcKindWithoutHtlc_When_Planning_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => OutputResolutionPlanner.Plan(
                                             Output(OutputDescriptorKind.LocalOfferedHtlc), Facts(CommitHeight)));
    }

    [Theory]
    [InlineData(OutputDescriptorKind.LocalOfferedHtlc, HtlcDirection.Incoming)]
    [InlineData(OutputDescriptorKind.LocalReceivedHtlc, HtlcDirection.Outgoing)]
    [InlineData(OutputDescriptorKind.RemoteReceivedHtlc, HtlcDirection.Incoming)]
    [InlineData(OutputDescriptorKind.RemoteOfferedHtlc, HtlcDirection.Outgoing)]
    public void Given_HtlcDirectionNotFittingKind_When_Planning_Then_Throws(OutputDescriptorKind kind,
                                                                            HtlcDirection direction)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => OutputResolutionPlanner.Plan(Htlc(kind, direction),
                                                                           Facts(CommitHeight)));
    }

    #endregion

    private static OutputResolutionFacts Facts(uint tip, OutputSpend? spend = null) => new(tip, CommitHeight, spend);

    private static OutputResolutionFacts SecondLevelFacts(uint tip, OutputSpend spend) =>
        new(tip, CommitHeight, spend, SecondLevelCsvDelay: ToSelfDelay);

    private static OutputSpend Spend(uint height, bool byUs, HtlcSpendPath path = HtlcSpendPath.Unknown,
                                     byte[]? preimage = null) =>
        new(OnchainTestData.TxIdOf(9), height, byUs, path, preimage);

    private static SpecHtlc SpecHtlc(HtlcDirection direction) =>
        new(direction, 3, 50_000_000, new byte[32], Cltv);

    private static CommitmentOutputDescriptor Output(OutputDescriptorKind kind, ushort csv = 0) =>
        new(0, 50_000, kind, [0x00, 0x20], [0x51], null, csv, false);

    private static CommitmentOutputDescriptor Htlc(OutputDescriptorKind kind, HtlcDirection direction) =>
        new(1, 50_000, kind, [0x00, 0x20], [0x51], SpecHtlc(direction), 0, false);
}
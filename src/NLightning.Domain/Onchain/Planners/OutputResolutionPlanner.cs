namespace NLightning.Domain.Onchain.Planners;

using Channels.Commitments;
using Channels.Enums;
using Enums;
using Models;

/// <summary>
/// The pure BOLT 5 resolution rules per output (plan §3.1, §3.3, §3.4; tasks O3-T2, O4-T2, O5-T2): from an output's
/// descriptor and the chain facts, its state and the actions to take now. Stateless: the watcher re-runs it on every
/// block, so every action is repeated until its effect is on chain, and consumers dedupe (persisted broadcast rows,
/// the idempotent switch).
/// </summary>
/// <remarks>
/// Heights: a CSV-locked spend of an output confirmed at <c>h</c> with delay <c>d</c> can enter block <c>h + d</c>, so
/// it is broadcast once <c>tip + 1 &gt;= h + d</c>; a <c>nLockTime = cltv_expiry</c> spend can enter block
/// <c>cltv_expiry + 1</c>, so it is broadcast once <c>tip &gt;= cltv_expiry</c> ("timed out", BOLT 5). Upstream events
/// (<see cref="ResolutionActionKind.RaiseFulfilled"/>, <see cref="ResolutionActionKind.RaiseFailed"/>) are only asked
/// for HTLCs we offered: a fulfill as soon as a preimage is known (on chain or off chain), a fail only once the
/// transaction that settles the HTLC without a preimage is <see cref="OutputResolutionFacts.ReasonableDepth"/> deep.
/// </remarks>
public static class OutputResolutionPlanner
{
    /// <summary>Plans one output of a commitment on chain.</summary>
    /// <exception cref="ArgumentException">The descriptor is inconsistent (an HTLC kind without its HTLC, or a
    /// direction that does not fit the kind), or a second-level spend confirmed without
    /// <see cref="OutputResolutionFacts.SecondLevelCsvDelay"/>.</exception>
    public static OutputResolutionPlan Plan(CommitmentOutputDescriptor output, OutputResolutionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(facts);

        return output.Kind switch
        {
            OutputDescriptorKind.DelayedToLocal => PlanDelayedToLocal(output, facts),
            OutputDescriptorKind.PaymentToRemote => PlanPaymentToRemote(output, facts),
            OutputDescriptorKind.LocalOfferedHtlc => PlanLocalOffered(RequireHtlc(output, HtlcDirection.Outgoing),
                                                                      facts),
            OutputDescriptorKind.LocalReceivedHtlc => PlanLocalReceived(RequireHtlc(output, HtlcDirection.Incoming),
                                                                        facts),
            OutputDescriptorKind.RemoteReceivedHtlc => PlanRemoteReceived(
                RequireHtlc(output, HtlcDirection.Outgoing), facts),
            OutputDescriptorKind.RemoteOfferedHtlc => PlanRemoteOffered(RequireHtlc(output, HtlcDirection.Incoming),
                                                                        facts),
            OutputDescriptorKind.RevokedToLocal => PlanRevokedToLocal(output, facts),
            OutputDescriptorKind.RevokedHtlc => PlanRevokedHtlc(RequireHtlc(output, null), facts),

            // Not ours (B5-LCL-02, B5-RMT-02; anchors wait for O7): resolved by the commitment itself
            _ => ResolvedAt(facts, facts.CommitmentHeight, [])
        };
    }

    /// <summary>
    /// Plans a committed HTLC without an output in the commitment on chain (B5-LCL-LO-04, B5-RMT-LO-03,
    /// B5-REV-RES-03): for an HTLC we offered, fulfill upstream at once with a known preimage, else fail it once the
    /// commitment is reasonably deep, or at once when no valid commitment has an output for it. An HTLC the peer
    /// offered needs nothing.
    /// </summary>
    public static OutputResolutionPlan PlanHtlcWithoutOutput(SpecHtlc htlc, HtlcWithoutOutputFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var commitmentDepth = Depth(facts.TipHeight, facts.CommitmentHeight);
        var state = commitmentDepth >= facts.IrrevocableDepth
                        ? OutputResolutionState.IrrevocablyResolved
                        : OutputResolutionState.Resolved;
        if (htlc.Direction == HtlcDirection.Incoming || facts.UpstreamResolved)
            return new OutputResolutionPlan(state, []);

        const string requirement = "B5-LCL-LO-04";
        if (facts.AllowedPreimage is not null)
            return new OutputResolutionPlan(state, [Fulfilled(requirement, facts.AllowedPreimage)]);

        if (!facts.OutputInAnyValidCommitment || commitmentDepth >= facts.ReasonableDepth)
            return new OutputResolutionPlan(state, [Failed(requirement)]);

        return new OutputResolutionPlan(state, [
            WaitFor(requirement, facts.CommitmentHeight + facts.ReasonableDepth - 1)
        ]);
    }

    // B5-LCL-01: our to_local, after to_self_delay
    private static OutputResolutionPlan PlanDelayedToLocal(CommitmentOutputDescriptor output,
                                                           OutputResolutionFacts facts)
    {
        const string requirement = "B5-LCL-01";
        if (facts.Spend is { } spend)
            return spend.ByUs
                       ? ResolvedAt(facts, spend.Height, [])
                       : ResolvedAt(facts, spend.Height, [Alert(requirement)]);

        return new OutputResolutionPlan(OutputResolutionState.Unresolved,
                                        [CsvSweep(requirement, facts, facts.CommitmentHeight, output.CsvDelay,
                                                  SweepSpendKind.DelayedOutput, false)]);
    }

    // D5 / B5-RMT-02 / B5-REV-02 / B5-RMT-03: our to_remote on a peer commitment is swept into the wallet
    private static OutputResolutionPlan PlanPaymentToRemote(CommitmentOutputDescriptor output,
                                                            OutputResolutionFacts facts)
    {
        const string requirement = "B5-RMT-02";
        if (facts.Spend is { } spend)
            return spend.ByUs
                       ? ResolvedAt(facts, spend.Height, [])
                       : ResolvedAt(facts, spend.Height, [Alert(requirement)]);

        return new OutputResolutionPlan(OutputResolutionState.Unresolved,
                                        [CsvSweep(requirement, facts, facts.CommitmentHeight, output.CsvDelay,
                                                  SweepSpendKind.PaymentToRemote, false)]);
    }

    // B5-LCL-LO-*: an HTLC we offered, on our commitment
    private static OutputResolutionPlan PlanLocalOffered(SpecHtlc htlc, OutputResolutionFacts facts)
    {
        if (facts.Spend is not { } spend)
        {
            var actions = new List<ResolutionAction>();
            AddKnownPreimageFulfill(actions, facts, "B5-LCL-LO-01");
            actions.Add(facts.TipHeight >= htlc.CltvExpiry
                            ? new ResolutionAction(ResolutionActionKind.BroadcastHtlcTimeoutTx, "B5-LCL-LO-02")
                            : WaitFor("B5-LCL-LO-02", htlc.CltvExpiry));
            return new OutputResolutionPlan(OutputResolutionState.Unresolved, actions);
        }

        // The peer claimed it with the preimage: fulfill upstream at once (B5-LCL-LO-01)
        if (!spend.ByUs && spend.Preimage is not null)
            return ResolvedAt(facts, spend.Height, UpstreamFulfilled(facts, "B5-LCL-LO-01", spend.Preimage));

        // Our HTLC-timeout: fail upstream at reasonable depth, then sweep its output after the CSV (B5-LCL-LO-03)
        if (spend.ByUs)
            return PlanOwnSecondLevel(facts, spend, htlc, "B5-LCL-LO-03");

        // Any other spend (a revocation of our own commitment) loses the HTLC: warn, and fail upstream at depth
        return PlanLostHtlc(facts, spend, htlc, "B5-LCL-LO-03");
    }

    // B5-LCL-RO-*: an HTLC the peer offered, on our commitment
    private static OutputResolutionPlan PlanLocalReceived(SpecHtlc htlc, OutputResolutionFacts facts)
    {
        if (facts.Spend is not { } spend)
        {
            if (facts.RemoteIrrevocablyCommitted && facts.AllowedPreimage is not null)
                return new OutputResolutionPlan(OutputResolutionState.Unresolved, [
                    new ResolutionAction(ResolutionActionKind.BroadcastHtlcSuccessTx, "B5-LCL-RO-01",
                                         DeadlineHeight: htlc.CltvExpiry, Preimage: facts.AllowedPreimage)
                ]);

            return ExpiresUnclaimed(htlc, facts,
                                    facts.RemoteIrrevocablyCommitted ? "B5-LCL-RO-04" : "B5-LCL-RO-03");
        }

        // Our HTLC-success confirmed: sweep its output after the CSV (B5-LCL-RO-01)
        if (spend.ByUs)
            return PlanOwnSecondLevel(facts, spend, htlc, "B5-LCL-RO-01");

        // The peer timed it out (its direct timeout path): nothing for us (B5-LCL-RO-04); a revocation spend is a loss
        return spend.Path == HtlcSpendPath.TimeoutClaim
                   ? ResolvedAt(facts, spend.Height, [])
                   : ResolvedAt(facts, spend.Height, [Alert("B5-LCL-RO-04")]);
    }

    // B5-RMT-LO-*: an HTLC we offered, on the peer's commitment ("received" output there)
    private static OutputResolutionPlan PlanRemoteReceived(SpecHtlc htlc, OutputResolutionFacts facts)
    {
        if (facts.Spend is not { } spend)
        {
            var actions = new List<ResolutionAction>();
            AddKnownPreimageFulfill(actions, facts, "B5-RMT-LO-01");
            actions.Add(facts.TipHeight >= htlc.CltvExpiry
                            ? new ResolutionAction(ResolutionActionKind.Sweep, "B5-RMT-LO-02",
                                                   SweepSpendKind.HtlcTimeoutClaim)
                            : WaitFor("B5-RMT-LO-02", htlc.CltvExpiry));
            return new OutputResolutionPlan(OutputResolutionState.Unresolved, actions);
        }

        // The peer's HTLC-success revealed the preimage (B5-RMT-LO-01)
        if (!spend.ByUs && spend.Preimage is not null)
            return ResolvedAt(facts, spend.Height, UpstreamFulfilled(facts, "B5-RMT-LO-01", spend.Preimage));

        // Our timeout claim: fail upstream at reasonable depth (B5-RMT-LO-02, as B5-LCL-LO-03)
        if (spend.ByUs)
        {
            var actions = new List<ResolutionAction>();
            AddUpstreamFailAtDepth(actions, facts, spend.Height, "B5-RMT-LO-02");
            return ResolvedAt(facts, spend.Height, actions);
        }

        return PlanLostHtlc(facts, spend, htlc, "B5-RMT-LO-02");
    }

    // B5-RMT-RO-*: an HTLC the peer offered, on its commitment ("offered" output there)
    private static OutputResolutionPlan PlanRemoteOffered(SpecHtlc htlc, OutputResolutionFacts facts)
    {
        if (facts.Spend is not { } spend)
        {
            if (facts.RemoteIrrevocablyCommitted && facts.AllowedPreimage is not null)
                return new OutputResolutionPlan(OutputResolutionState.Unresolved, [
                    new ResolutionAction(ResolutionActionKind.Sweep, "B5-RMT-RO-01", SweepSpendKind.HtlcPreimageClaim,
                                         DeadlineHeight: htlc.CltvExpiry, Preimage: facts.AllowedPreimage)
                ]);

            return ExpiresUnclaimed(htlc, facts, "B5-RMT-RO-02");
        }

        // Our preimage claim confirmed, or the peer's HTLC-timeout took it back: either way final
        return spend.ByUs || spend.Path == HtlcSpendPath.HtlcTimeoutTransaction
                   ? ResolvedAt(facts, spend.Height, [])
                   : ResolvedAt(facts, spend.Height, [Alert("B5-RMT-RO-02")]);
    }

    // B5-REV-03: the cheater's to_local
    private static OutputResolutionPlan PlanRevokedToLocal(CommitmentOutputDescriptor output,
                                                           OutputResolutionFacts facts)
    {
        const string requirement = "B5-REV-03";
        if (facts.Spend is { } spend)
            return spend.ByUs
                       ? ResolvedAt(facts, spend.Height, [])
                       : ResolvedAt(facts, spend.Height, [Alert(requirement)]);

        return new OutputResolutionPlan(OutputResolutionState.Unresolved, [
            new ResolutionAction(ResolutionActionKind.Sweep, requirement, SweepSpendKind.RevokedDelayedOutput,
                                 DeadlineHeight: facts.CommitmentHeight + output.CsvDelay)
        ]);
    }

    // B5-REV-04..07, B5-REV-RES-01/02: an HTLC output of a revoked commitment
    private static OutputResolutionPlan PlanRevokedHtlc(SpecHtlc htlc, OutputResolutionFacts facts)
    {
        var ours = htlc.Direction == HtlcDirection.Outgoing;
        var requirement = ours ? "B5-REV-05" : "B5-REV-04";
        var actions = new List<ResolutionAction>();

        if (facts.Spend is not { } spend)
        {
            if (ours)
                AddKnownPreimageFulfill(actions, facts, "B5-REV-RES-01");

            // The cheater can take our offered HTLC with its HTLC-success at any time, its own after cltv_expiry
            actions.Add(new ResolutionAction(ResolutionActionKind.Sweep, requirement, SweepSpendKind.RevokedHtlc,
                                             DeadlineHeight: ours ? facts.TipHeight + 1 : htlc.CltvExpiry));
            return new OutputResolutionPlan(OutputResolutionState.Unresolved, actions);
        }

        // Our penalty took the commitment output
        if (spend.ByUs)
        {
            if (ours)
                AddUpstreamFailAtDepth(actions, facts, spend.Height, "B5-REV-RES-02");
            return ResolvedAt(facts, spend.Height, actions);
        }

        // A preimage on chain fulfills our offered HTLC at once, whoever gets the funds (B5-REV-07, B5-REV-RES-01)
        if (ours && spend.Preimage is not null)
            actions.AddRange(UpstreamFulfilled(facts, "B5-REV-RES-01", spend.Preimage));

        // The cheater's HTLC-timeout/success spent it: penalize its output before the CSV (B5-REV-06, B5-REV-09)
        if (spend.Path is HtlcSpendPath.HtlcSuccessTransaction or HtlcSpendPath.HtlcTimeoutTransaction)
        {
            var csv = RequireSecondLevelCsv(facts);
            if (facts.SecondLevelSpend is not { } secondLevel)
            {
                actions.Add(new ResolutionAction(ResolutionActionKind.Sweep, "B5-REV-06",
                                                 SweepSpendKind.RevokedDelayedOutput, true,
                                                 DeadlineHeight: spend.Height + csv));
                return new OutputResolutionPlan(OutputResolutionState.Unresolved, actions);
            }

            // The cheater swept its second-level output after the CSV: we lost it
            if (!secondLevel.ByUs)
                actions.Add(Alert("B5-REV-06"));

            if (ours && spend.Preimage is null)
                AddUpstreamFailAtDepth(actions, facts, secondLevel.Height, "B5-REV-RES-02");
            return ResolvedAt(facts, secondLevel.Height, actions);
        }

        // A direct preimage or timeout spend is ours only, so anything else is a loss
        actions.Add(Alert(requirement));
        if (ours && spend.Preimage is null)
            AddUpstreamFailAtDepth(actions, facts, spend.Height, "B5-REV-RES-02");
        return ResolvedAt(facts, spend.Height, actions);
    }

    /// <summary>
    /// Our HTLC-timeout/success confirmed at <c>spend.Height</c>: for an HTLC we offered, fail upstream at reasonable
    /// depth (fulfill if a preimage is known after all); then sweep the second-level output after the CSV
    /// (B5-LCL-LO-03, B5-LCL-RO-01).
    /// </summary>
    private static OutputResolutionPlan PlanOwnSecondLevel(OutputResolutionFacts facts, OutputSpend spend,
                                                           SpecHtlc htlc, string requirement)
    {
        var csv = RequireSecondLevelCsv(facts);
        var actions = new List<ResolutionAction>();
        if (htlc.Direction == HtlcDirection.Outgoing)
            AddUpstreamFailAtDepth(actions, facts, spend.Height, requirement);

        if (facts.SecondLevelSpend is not { } secondLevel)
        {
            actions.Add(CsvSweep(requirement, facts, spend.Height, csv, SweepSpendKind.DelayedOutput, true));
            return new OutputResolutionPlan(OutputResolutionState.Unresolved, actions);
        }

        if (!secondLevel.ByUs)
            actions.Add(Alert(requirement));
        return ResolvedAt(facts, secondLevel.Height, actions);
    }

    /// <summary>An HTLC output taken by a path that should not exist (a revocation of our commitment).</summary>
    private static OutputResolutionPlan PlanLostHtlc(OutputResolutionFacts facts, OutputSpend spend, SpecHtlc htlc,
                                                     string requirement)
    {
        var actions = new List<ResolutionAction> { Alert(requirement) };
        if (htlc.Direction == HtlcDirection.Outgoing)
            AddUpstreamFailAtDepth(actions, facts, spend.Height, requirement);
        return ResolvedAt(facts, spend.Height, actions);
    }

    /// <summary>
    /// An HTLC the peer offered that we do not (or may not) claim: irrevocably resolved once it expired, else we wait
    /// (a preimage may still arrive, and the peer may still become irrevocably committed).
    /// </summary>
    private static OutputResolutionPlan ExpiresUnclaimed(SpecHtlc htlc, OutputResolutionFacts facts,
                                                         string requirement) =>
        facts.TipHeight >= htlc.CltvExpiry
            ? new OutputResolutionPlan(OutputResolutionState.IrrevocablyResolved, [])
            : new OutputResolutionPlan(OutputResolutionState.Unresolved, [WaitFor(requirement, htlc.CltvExpiry)]);

    private static ResolutionAction CsvSweep(string requirement, OutputResolutionFacts facts, uint confirmedHeight,
                                             ushort csv, SweepSpendKind kind, bool onSecondLevel)
    {
        // A spend with nSequence = csv can enter block confirmedHeight + csv (at once without a delay)
        var firstBlock = confirmedHeight + csv;
        return facts.TipHeight + 1 >= firstBlock
                   ? new ResolutionAction(ResolutionActionKind.Sweep, requirement, kind, onSecondLevel)
                   : WaitFor(requirement, firstBlock - 1);
    }

    private static void AddKnownPreimageFulfill(List<ResolutionAction> actions, OutputResolutionFacts facts,
                                                string requirement)
    {
        if (facts.AllowedPreimage is not null && !facts.UpstreamResolved)
            actions.Add(Fulfilled(requirement, facts.AllowedPreimage));
    }

    private static IReadOnlyList<ResolutionAction> UpstreamFulfilled(OutputResolutionFacts facts, string requirement,
                                                                     byte[] preimage) =>
        facts.UpstreamResolved ? [] : [Fulfilled(requirement, preimage)];

    /// <summary>
    /// Fail our offered HTLC upstream once the settling transaction confirmed at <paramref name="height"/> is
    /// reasonably deep; fulfill instead when a preimage is known (the peer fulfilled it off chain).
    /// </summary>
    private static void AddUpstreamFailAtDepth(List<ResolutionAction> actions, OutputResolutionFacts facts,
                                               uint height, string requirement)
    {
        if (facts.UpstreamResolved)
            return;

        if (facts.AllowedPreimage is not null)
        {
            actions.Add(Fulfilled(requirement, facts.AllowedPreimage));
            return;
        }

        actions.Add(facts.DepthOf(height) >= facts.ReasonableDepth
                        ? Failed(requirement)
                        : WaitFor(requirement, height + facts.ReasonableDepth - 1));
    }

    private static OutputResolutionPlan ResolvedAt(OutputResolutionFacts facts, uint height,
                                                   IReadOnlyList<ResolutionAction> actions) =>
        new(facts.DepthOf(height) >= facts.IrrevocableDepth
                ? OutputResolutionState.IrrevocablyResolved
                : OutputResolutionState.Resolved, actions);

    private static ushort RequireSecondLevelCsv(OutputResolutionFacts facts) =>
        facts.SecondLevelCsvDelay > 0
            ? facts.SecondLevelCsvDelay
            : throw new ArgumentException("A confirmed HTLC transaction needs the CSV delay of its output",
                                          nameof(facts));

    private static SpecHtlc RequireHtlc(CommitmentOutputDescriptor output, HtlcDirection? direction)
    {
        if (output.Htlc is not { } htlc)
            throw new ArgumentException($"A {output.Kind} descriptor has its HTLC", nameof(output));

        if (direction is { } expected && htlc.Direction != expected)
            throw new ArgumentException($"A {output.Kind} descriptor holds a {expected} HTLC, not {htlc.Direction}",
                                        nameof(output));

        return htlc;
    }

    private static uint Depth(uint tip, uint height) => tip >= height ? tip - height + 1 : 0;

    private static ResolutionAction WaitFor(string requirement, uint height) =>
        new(ResolutionActionKind.Wait, requirement, WaitUntilHeight: height);

    private static ResolutionAction Fulfilled(string requirement, byte[] preimage) =>
        new(ResolutionActionKind.RaiseFulfilled, requirement, Preimage: preimage);

    private static ResolutionAction Failed(string requirement) => new(ResolutionActionKind.RaiseFailed, requirement);

    private static ResolutionAction Alert(string requirement) =>
        new(ResolutionActionKind.AlertLostFunds, requirement);
}
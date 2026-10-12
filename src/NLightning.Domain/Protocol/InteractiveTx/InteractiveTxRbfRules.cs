namespace NLightning.Domain.Protocol.InteractiveTx;

using Bitcoin.ValueObjects;
using Enums;
using Models;

/// <summary>
/// The base RBF rules of BOLT 2 "Interactive Transaction Construction" (<c>tx_init_rbf</c>/<c>tx_ack_rbf</c>, IT-RBF-01),
/// pure and table-testable. The dependent protocols (dual funding, splicing SPR-T1) add their own rows on top.
/// </summary>
/// <remarks>
/// <para>The feerate rule is the current BOLT 2 text (the additive floor of lightning/bolts PR #1327): the
/// <c>tx_init_rbf</c> sender "MUST set <c>feerate</c> greater than or equal to the maximum of: 25/24 times the
/// <c>feerate</c> of the previously constructed transaction, rounded down; 25 sat per kw greater than the
/// <c>feerate</c> of the previously constructed transaction", and the recipient "MUST respond with <c>tx_abort</c> if"
/// it is not. Example from the spec: after 520 the next feerate is at least max(541, 545) = 545.</para>
/// <para>The double-spend rule: a side that "contributed to previous transactions: MUST ensure that the new
/// transaction double-spends all other attempts, by sending <c>tx_add_input</c> with at least one input from each
/// previous transaction construction attempt". <see cref="CheckPartyDoubleSpends"/> checks one side against that;
/// <see cref="CheckDoubleSpendsAllAttempts"/> checks the whole transaction (a splice's shared input double-spends every
/// attempt by itself: BOLT 2 splicing, "RBF attempts automatically double-spend each other").</para>
/// </remarks>
public static class InteractiveTxRbfRules
{
    /// <summary>The additive part of the RBF feerate rule, in sat/kw (0.1 sat/vB, Bitcoin Core's incrementalRelayFee).</summary>
    public const uint MinimumFeerateIncrementPerKw = 25;

    /// <summary>
    /// The least feerate a <c>tx_init_rbf</c> after an attempt at <paramref name="previousFeeratePerKw"/> may carry:
    /// max(floor(25/24 x previous), previous + 25), saturating at <see cref="uint.MaxValue"/>.
    /// </summary>
    public static uint GetMinimumNextFeerate(uint previousFeeratePerKw)
    {
        var multiplicative = (ulong)previousFeeratePerKw * 25 / 24;
        var additive = (ulong)previousFeeratePerKw + MinimumFeerateIncrementPerKw;
        return (uint)Math.Min(Math.Max(multiplicative, additive), uint.MaxValue);
    }

    /// <summary>
    /// A received <c>tx_init_rbf</c>'s feerate (IT-RBF-01): the recipient "MUST respond with <c>tx_abort</c> if: the
    /// <c>feerate</c> is not greater than or equal to the maximum of: 25/24 times the <c>feerate</c> of the last
    /// successfully constructed transaction, rounded down; 25 sat per kw greater than [it]".
    /// </summary>
    /// <param name="proposedFeeratePerKw">The <c>tx_init_rbf</c>'s <c>feerate</c>.</param>
    /// <param name="previousFeeratePerKw">The feerate of the last successfully constructed transaction.</param>
    public static InteractiveTxRuleViolation? CheckFeerate(uint proposedFeeratePerKw, uint previousFeeratePerKw)
    {
        var minimum = GetMinimumNextFeerate(previousFeeratePerKw);
        return proposedFeeratePerKw < minimum
                   ? new InteractiveTxRuleViolation("IT-RBF-01",
                                                    $"feerate {proposedFeeratePerKw} sat/kw is below {minimum} sat/kw (previous {previousFeeratePerKw})")
                   : null;
    }

    /// <summary>
    /// Whether <paramref name="party"/> contributed to <paramref name="attempt"/>: it added at least one input there
    /// other than the shared input.
    /// </summary>
    public static bool HasContributedInputs(ConstructedInteractiveTx attempt, InteractiveTxParty party)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return attempt.Inputs.Any(i => !i.IsShared && i.AddedBy == party);
    }

    /// <summary>
    /// One side's double-spend duty (IT-RBF-01): for every previous attempt it contributed inputs to, the inputs it adds
    /// now (by outpoint) must include at least one input of that attempt.
    /// </summary>
    /// <param name="partyOutpoints">The outpoints of the inputs <paramref name="party"/> adds to the new attempt (the
    /// shared input excluded).</param>
    /// <param name="party">The side checked; previous attempts record the sides from our point of view too.</param>
    /// <param name="previousAttempts">Every earlier attempt of the negotiation
    /// (<see cref="InteractiveTxSessionParameters.PreviousAttempts"/>).</param>
    public static InteractiveTxRuleViolation? CheckPartyDoubleSpends(
        IReadOnlyCollection<(TxId TxId, uint Vout)> partyOutpoints, InteractiveTxParty party,
        IReadOnlyList<ConstructedInteractiveTx> previousAttempts)
    {
        ArgumentNullException.ThrowIfNull(partyOutpoints);
        ArgumentNullException.ThrowIfNull(previousAttempts);

        foreach (var attempt in previousAttempts)
        {
            if (!HasContributedInputs(attempt, party))
                continue;

            if (!attempt.Inputs.Any(i => partyOutpoints.Contains((i.PrevTxId, i.PrevTxVout))))
                return new InteractiveTxRuleViolation("IT-RBF-01",
                                                      $"the {Describe(party)} does not double-spend the previous attempt {attempt.TxId}");
        }

        return null;
    }

    /// <summary>
    /// The new transaction double-spends every previous attempt: each shares at least one spent outpoint with it.
    /// </summary>
    /// <param name="inputs">Every input of the new attempt.</param>
    /// <param name="previousAttempts">Every earlier attempt.</param>
    public static InteractiveTxRuleViolation? CheckDoubleSpendsAllAttempts(IReadOnlyList<InteractiveTxInput> inputs,
                                                                          IReadOnlyList<ConstructedInteractiveTx>
                                                                              previousAttempts)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(previousAttempts);

        var outpoints = inputs.Select(i => (i.PrevTxId, i.PrevTxVout)).ToHashSet();
        foreach (var attempt in previousAttempts)
        {
            if (!attempt.Inputs.Any(i => outpoints.Contains((i.PrevTxId, i.PrevTxVout))))
                return new InteractiveTxRuleViolation("IT-RBF-01",
                                                      $"the transaction does not double-spend the previous attempt {attempt.TxId}");
        }

        return null;
    }

    /// <summary>
    /// The check at <c>tx_complete</c> of an RBF attempt: the transaction double-spends every previous attempt
    /// (<see cref="CheckDoubleSpendsAllAttempts"/>). Null without previous attempts.
    /// </summary>
    /// <remarks>
    /// <para>BOLT 2 states the per-side duty ("If it contributed to previous transactions: MUST ensure that the new
    /// transaction double-spends all other attempts") only for the sender, and advises a peer facing a large feerate
    /// change to "stop contributing to the funding output, and decline to participate further in the transaction". So
    /// the receiver checks only what protects it: that no two attempts can both confirm. A peer that drops its inputs
    /// from an RBF is accepted as long as the whole transaction still conflicts with every attempt (Eclair does the
    /// same). <see cref="CheckPartyDoubleSpends"/> stays the sender-side check of our own contribution.</para>
    /// <para>With a shared input (a splice) every attempt spends the same funding output, so the attempts double-spend
    /// each other whatever each side adds (BOLT 2 splicing rationale).</para>
    /// </remarks>
    public static InteractiveTxRuleViolation? CheckTxComplete(IReadOnlyList<InteractiveTxInput> inputs,
                                                              IReadOnlyList<ConstructedInteractiveTx> previousAttempts)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(previousAttempts);

        return previousAttempts.Count == 0 ? null : CheckDoubleSpendsAllAttempts(inputs, previousAttempts);
    }

    private static string Describe(InteractiveTxParty party) => party == InteractiveTxParty.Local ? "local side" : "peer";
}
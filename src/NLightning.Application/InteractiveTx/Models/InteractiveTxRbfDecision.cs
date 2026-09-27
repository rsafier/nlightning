namespace NLightning.Application.InteractiveTx.Models;

using Domain.Money;

/// <summary>
/// The host's answer to a peer's <c>tx_init_rbf</c> (BOLT 2: the recipient MUST respond with <c>tx_abort</c> or
/// <c>tx_ack_rbf</c>). The driver has already enforced the feerate rule (IT-RBF-01) before asking.
/// </summary>
public sealed record InteractiveTxRbfDecision
{
    /// <summary>The new attempt's terms (<see cref="InteractiveTxTerms.IsInitiator"/> false: the sender of
    /// <c>tx_init_rbf</c> starts the new negotiation), or null when rejected.</summary>
    public InteractiveTxTerms? Terms { get; }

    /// <summary>What we contribute to the funding output (<c>funding_output_contribution</c> of <c>tx_ack_rbf</c>);
    /// zero for none.</summary>
    public LightningMoney FundingOutputContribution { get; }

    /// <summary>Why the attempt is rejected with <c>tx_abort</c>, or null when accepted.</summary>
    public string? RejectReason { get; }

    private InteractiveTxRbfDecision(InteractiveTxTerms? terms, LightningMoney fundingOutputContribution,
                                     string? rejectReason)
    {
        Terms = terms;
        FundingOutputContribution = fundingOutputContribution;
        RejectReason = rejectReason;
    }

    /// <summary>Accepts the attempt with <paramref name="terms"/>.</summary>
    public static InteractiveTxRbfDecision Accept(InteractiveTxTerms terms, LightningMoney fundingOutputContribution)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.IsInitiator)
            throw new ArgumentException("The recipient of tx_init_rbf is not the initiator of the new attempt",
                                        nameof(terms));

        return new InteractiveTxRbfDecision(terms, fundingOutputContribution, null);
    }

    /// <summary>Rejects the attempt with <c>tx_abort</c>.</summary>
    public static InteractiveTxRbfDecision Reject(string reason) =>
        new(null, LightningMoney.Zero, string.IsNullOrEmpty(reason) ? "rbf rejected" : reason);
}
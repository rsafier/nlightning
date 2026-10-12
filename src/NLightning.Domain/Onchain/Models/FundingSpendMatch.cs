namespace NLightning.Domain.Onchain.Models;

/// <summary>
/// The result of <see cref="Classifiers.FundingSpendClassifier.ClassifyAny"/>: which of the channel's fundings the
/// transaction spends and what it is (splicing plan §3.6, SP2-0; lane SP2-C, SP2-C-T1).
/// </summary>
/// <param name="Context">The context of the funding the transaction spends (its <see cref="FundingSpendContext.FundingTxId"/>
/// names the funding whose commitments, revocation log and capacity the resolution uses).</param>
/// <param name="Classification">The classification against that funding.</param>
public sealed record FundingSpendMatch(FundingSpendContext Context, FundingSpendClassification Classification);
namespace NLightning.Domain.Onchain.Models;

using Enums;

/// <summary>
/// The result of <see cref="Classifiers.FundingSpendClassifier.Classify"/>.
/// </summary>
/// <param name="Kind">What spent the funding output.</param>
/// <param name="CommitmentNumber">The commitment number decoded from the transaction (commitment kinds, and an
/// <see cref="FundingSpendKind.Unknown"/> spend that still has the commitment form); null otherwise.</param>
/// <param name="TxIdMatched">True when the spender's txid equals a candidate we rebuilt. False for a commitment
/// recognized by its number only (a revoked or future one, or a current one whose rebuild differs, risk §8.1): its
/// outputs must then be mapped by script.</param>
/// <param name="Reason">Why, for logs and the user alert.</param>
public sealed record FundingSpendClassification(
    FundingSpendKind Kind,
    ulong? CommitmentNumber,
    bool TxIdMatched,
    string Reason)
{
    /// <summary>True for every kind that is a commitment of either side.</summary>
    public bool IsCommitment => Kind is FundingSpendKind.LocalCommit or FundingSpendKind.RemoteCommit
                                    or FundingSpendKind.RemoteNextCommit or FundingSpendKind.Revoked
                                    or FundingSpendKind.FutureRemote;
}
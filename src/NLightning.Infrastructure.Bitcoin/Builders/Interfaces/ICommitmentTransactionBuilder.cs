namespace NLightning.Infrastructure.Bitcoin.Builders.Interfaces;

using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;

public interface ICommitmentTransactionBuilder
{
    /// <summary>
    /// Builds the unsigned commitment transaction. Same as <see cref="BuildWithOutputMap"/> without the HTLC map.
    /// </summary>
    SignedTransaction Build(CommitmentTransactionModel transaction);

    /// <summary>
    /// Builds the unsigned commitment transaction (outputs in BOLT 3 order) and reports the output index of every
    /// HTLC output, in transaction order. HTLC transactions and <c>htlc_signatures</c> follow that order.
    /// </summary>
    CommitmentTransactionBuildResult BuildWithOutputMap(CommitmentTransactionModel transaction);
}
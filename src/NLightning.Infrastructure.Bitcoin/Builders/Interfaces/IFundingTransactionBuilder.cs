namespace NLightning.Infrastructure.Bitcoin.Builders.Interfaces;

using Domain.Bitcoin.Transactions.Models;

public interface IFundingTransactionBuilder
{
    /// <summary>
    /// Builds an unsigned funding transaction from UTXOs. The model is not modified.
    /// </summary>
    /// <param name="transaction">The funding transaction model</param>
    /// <returns>The unsigned transaction and the index of its funding output</returns>
    FundingTransactionBuildResult Build(FundingTransactionModel transaction);
}
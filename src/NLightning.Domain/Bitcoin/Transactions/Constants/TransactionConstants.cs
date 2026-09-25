using System.Diagnostics.CodeAnalysis;
using NLightning.Domain.Money;

namespace NLightning.Domain.Bitcoin.Transactions.Constants;

[ExcludeFromCodeCoverage]
public static class TransactionConstants
{
    public const uint CommitmentTransactionVersion = 2;
    public const uint HtlcTransactionVersion = 2;
    public const uint FundingTransactionVersion = 2;

    public const int CommitmentTransactionInputWeight = WeightConstants.WitnessHeader
                                                      + WeightConstants.MultisigWitnessWeight
                                                      + 4 * WeightConstants.P2WshInputWeight;

    public static readonly LightningMoney AnchorOutputAmount = LightningMoney.Satoshis(330);

    public const int TxIdLength = 32;

    /// <summary>
    /// BOLT 3 expected weight of a commitment transaction without HTLC outputs when option_anchors does not apply.
    /// </summary>
    public const int InitialCommitmentTransactionWeightNoAnchor = 724;

    /// <summary>
    /// BOLT 3 expected weight of a commitment transaction without HTLC outputs when option_anchors applies.
    /// </summary>
    public const int InitialCommitmentTransactionWeightWithAnchor = 1124;
}
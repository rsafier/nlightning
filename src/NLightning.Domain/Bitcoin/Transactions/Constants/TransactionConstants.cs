using System.Diagnostics.CodeAnalysis;
using NLightning.Domain.Money;

namespace NLightning.Domain.Bitcoin.Transactions.Constants;

[ExcludeFromCodeCoverage]
public static class TransactionConstants
{
    public const uint CommitmentTransactionVersion = 2;
    public const uint HtlcTransactionVersion = 2;
    public const uint FundingTransactionVersion = 2;

    public static readonly LightningMoney AnchorOutputAmount = LightningMoney.Satoshis(330);

    /// <summary>
    /// Bitcoin Core's standardness dust limit for a P2WPKH output (at the default 3 sat/vB dust relay fee).
    /// </summary>
    public static readonly LightningMoney P2WpkhDustLimit = LightningMoney.Satoshis(294);

    /// <summary>
    /// Bitcoin Core's standardness dust limit for a P2TR (or P2WSH) output (at the default 3 sat/vB dust relay fee).
    /// </summary>
    public static readonly LightningMoney P2TrDustLimit = LightningMoney.Satoshis(330);

    /// <summary>
    /// The <c>dust_limit_satoshis</c> the simple taproot channel vectors use, which the spec calls "taproot dust": Bitcoin
    /// Core's dust threshold of a witness output with the largest (40-byte) program at the default 3 sat/vB dust relay
    /// fee, LND's default channel dust limit. It is a channel parameter, not a rule of the commitment format.
    /// </summary>
    public static readonly LightningMoney SimpleTaprootDustLimit = LightningMoney.Satoshis(354);

    public const int TxIdLength = 32;

    /// <summary>
    /// BOLT 3 expected weight of a commitment transaction without HTLC outputs when option_anchors does not apply.
    /// </summary>
    public const int InitialCommitmentTransactionWeightNoAnchor = WeightConstants.CommitmentWeightNoAnchors;

    /// <summary>
    /// BOLT 3 expected weight of a commitment transaction without HTLC outputs when option_anchors applies.
    /// </summary>
    public const int InitialCommitmentTransactionWeightWithAnchor = WeightConstants.CommitmentWeightAnchors;
}
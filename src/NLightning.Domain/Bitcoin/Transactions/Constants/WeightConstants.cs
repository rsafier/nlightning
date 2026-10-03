using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Bitcoin.Transactions.Constants;

[ExcludeFromCodeCoverage]
public static class WeightConstants
{
    //                                                     | Amount | Script Length | Script    |             
    public const int P2PkhOutputWeight = 34 * 4; // | 8  | 1             | 25        |
    public const int P2ShOutputWeight = 33 * 4; // | 8  | 1             | 23        |
    public const int P2WpkhOutputWeight = 31 * 4; // | 8  | 1             | 22        |
    public const int P2WshOutputWeight = 43 * 4; // | 8  | 1             | 34        |
    public const int P2TrOutputWeight = 43 * 4; // | 8  | 1             | 34        |
    public const int P2UnknownSOutputWeight = 51 * 4; // | 8  | 1             | 42        |

    public const int P2PkhInputWeight = 148; // At Least
    public const int P2ShInputWeight = 148; // At Least
    public const int P2WpkhInputWeight = 41; // At Least
    public const int P2TrInputWeight = P2WpkhInputWeight;
    public const int P2WshInputWeight = P2WpkhInputWeight;
    public const int P2UnknownInputWeight = P2WpkhInputWeight;

    public const int WitnessHeader = 2; // flag, marker
    public const int MultisigWitnessWeight = 222; // 1 byte for each signature
    public const int SingleSigWitnessWeight = 107;
    public const int TaprootSigWitnessWeight = 66;

    public const int HtlcOutputWeight = P2WshOutputWeight;
    public const int AnchorOutputWeight = P2WshOutputWeight;

    /// <summary>BOLT 3 expected commitment weight without HTLC outputs, no option_anchors.</summary>
    public const int CommitmentWeightNoAnchors = 724;

    /// <summary>BOLT 3 expected commitment weight without HTLC outputs, with option_anchors.</summary>
    public const int CommitmentWeightAnchors = 1124;

    /// <summary>
    /// Expected commitment weight without HTLC outputs with option_simple_taproot (bolt-simple-taproot.md). The spec
    /// text gives no number; its three commitment vectors fix it: with 2 x 330 sat anchors the fees 14,520 sat at
    /// 15,000 sat/kw (no HTLC), 1,177 sat at 644 sat/kw (5 HTLCs) and 844 sat at 644 sat/kw (2 HTLCs) are exactly
    /// <c>feerate * (968 + 172 * n) / 1000</c> rounded down, the 172 per HTLC output of BOLT 3. It is what LND computes:
    /// the anchors base 1124 less the P2WSH 2-of-2 funding witness (222) plus a 64-byte key-path signature (66).
    /// </summary>
    public const int CommitmentWeightSimpleTaproot = 968;

    /// <summary>
    /// BOLT 3 expected HTLC-timeout weight with option_anchors. Informational only: with anchors the HTLC-timeout fee
    /// is 0, so this weight never feeds a fee or a trimming decision (NL-195). Use <c>CommitmentFeeCalculator</c>.
    /// </summary>
    public const int HtlcTimeoutWeightAnchors = 666;

    /// <summary>BOLT 3 expected HTLC-timeout weight without option_anchors (fee = feerate_per_kw * 663 / 1000).</summary>
    public const int HtlcTimeoutWeightNoAnchors = 663;

    /// <summary>
    /// BOLT 3 expected HTLC-success weight with option_anchors. Informational only: with anchors the HTLC-success fee
    /// is 0, so this weight never feeds a fee or a trimming decision (NL-195). Use <c>CommitmentFeeCalculator</c>.
    /// </summary>
    public const int HtlcSuccessWeightAnchors = 706;

    /// <summary>BOLT 3 expected HTLC-success weight without option_anchors (fee = feerate_per_kw * 703 / 1000).</summary>
    public const int HtlcSuccessWeightNoAnchors = 703;

    public const int TransactionBaseWeight = 10 * 4; // version, input count, output count, locktime
}
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Constants;

/// <summary>
/// The BOLT 2 "Interactive Transaction Construction" limits that are not receiver-side rules of
/// <see cref="NLightning.Domain.Protocol.InteractiveTx.InteractiveTxRules"/>: the <c>tx_add_input</c> sequence ceiling
/// the payload validator enforces (NL-474: the duplicated input, output, money and weight limits are owned by the rules
/// since wave IT and were removed).
/// </summary>
[ExcludeFromCodeCoverage]
public static class InteractiveTransactionConstants
{
    /// <summary>BOLT 2 (tx_add_input sender): "MUST set <c>sequence</c> to be less than or equal to 4294967293".</summary>
    public const uint MaxSequence = 0xFFFFFFFD;
}
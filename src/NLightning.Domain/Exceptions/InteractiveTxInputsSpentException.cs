using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

/// <summary>
/// Our wallet inputs of an interactive transaction cannot be signed because the wallet saw them spent on chain: their
/// reservation is still held but the outputs left the wallet's UTXO set, typically when an RBF sibling of the same
/// funding confirmed in the middle of an RBF attempt (NL-867). Before our <c>tx_signatures</c> the negotiation can only
/// be abandoned (BOLT 2: "If the previous transaction confirms in the middle of an RBF attempt, the attempt MUST be
/// abandoned"), so the interactive-tx driver answers it with <c>tx_abort</c>, never with a failed connection.
/// </summary>
/// <remarks>
/// It derives from <see cref="InvalidOperationException"/>, what the contributor threw before, so callers that do not
/// tell it apart keep treating it as a signing failure.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class InteractiveTxInputsSpentException : InvalidOperationException
{
    public InteractiveTxInputsSpentException(string message) : base(message) { }
}
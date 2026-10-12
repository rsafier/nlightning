using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

using Money;

/// <summary>
/// Thrown when a channel open, accept or funding would leave the wallet below the reserve it keeps for
/// <c>option_anchors</c> channels (NL-379).
/// </summary>
[ExcludeFromCodeCoverage]
public class AnchorReserveException : InsufficientFundsException
{
    /// <summary>The reserve the wallet must keep after the operation.</summary>
    public LightningMoney RequiredReserve { get; }

    public AnchorReserveException(string message, LightningMoney required, LightningMoney available,
                                  LightningMoney requiredReserve)
        : base(message, required, available)
    {
        RequiredReserve = requiredReserve;
    }
}
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

using Money;

/// <summary>
/// Thrown when the available inputs cannot cover the requested amount plus the fee.
/// </summary>
[ExcludeFromCodeCoverage]
public class InsufficientFundsException : ErrorException
{
    /// <summary>
    /// The amount needed (outputs plus fee).
    /// </summary>
    public LightningMoney Required { get; }

    /// <summary>
    /// The amount available from the inputs.
    /// </summary>
    public LightningMoney Available { get; }

    public InsufficientFundsException(LightningMoney required, LightningMoney available)
        : base($"Insufficient funds: required {required.Satoshi} sat, available {available.Satoshi} sat")
    {
        Required = required;
        Available = available;
    }
}
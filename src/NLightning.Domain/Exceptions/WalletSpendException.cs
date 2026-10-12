using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

using Bitcoin.Enums;

/// <summary>
/// An on-chain wallet spend (<c>withdraw</c>) refused before anything was reserved or broadcast; the message says why
/// in words the operator can act on.
/// </summary>
[ExcludeFromCodeCoverage]
public class WalletSpendException : Exception
{
    /// <summary>What was wrong with the request.</summary>
    public WalletSpendError Error { get; }

    public WalletSpendException(WalletSpendError error, string message) : base(message)
    {
        Error = error;
    }
}
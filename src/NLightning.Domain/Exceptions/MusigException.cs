using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

/// <summary>
/// A BIP 327 (MuSig2) operation refused its arguments (the reference implementation's <c>ValueError</c>, with the same
/// message), for example a tweak at or above the curve order, a used secret nonce or a signer missing from the keys.
/// </summary>
[ExcludeFromCodeCoverage]
public class MusigException : Exception
{
    public MusigException(string message) : base(message) { }
    public MusigException(string message, Exception innerException) : base(message, innerException) { }
}
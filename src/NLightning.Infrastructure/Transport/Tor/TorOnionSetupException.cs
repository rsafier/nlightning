using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Transport.Tor;

/// <summary>
/// The onion service cannot be set up as configured (a client authorization or PoW option Tor cannot take); the
/// service is not started rather than added without what the operator asked for.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class TorOnionSetupException : Exception
{
    public TorOnionSetupException(string message) : base(message) { }
}
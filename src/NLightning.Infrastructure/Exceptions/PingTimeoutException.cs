using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Exceptions;

/// <summary>
/// The peer did not answer our <c>ping</c> in time (BOLT 1 lets us close the connection, never fail the channels): a
/// connection problem, logged as a warning (NL-532).
/// </summary>
[ExcludeFromCodeCoverage]
public class PingTimeoutException : ConnectionTimeoutException
{
    public PingTimeoutException(string message) : base(message) { }
}
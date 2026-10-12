using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Exceptions;

using Domain.Exceptions;

/// <summary>
/// The peer closed the connection (end of stream on a read): a routine disconnection, not a fault of ours (NL-532).
/// </summary>
[ExcludeFromCodeCoverage]
public class PeerClosedConnectionException : ConnectionException
{
    public PeerClosedConnectionException(string message) : base(message) { }
    public PeerClosedConnectionException(string message, Exception innerException) : base(message, innerException) { }
}
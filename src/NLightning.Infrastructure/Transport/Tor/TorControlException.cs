using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Transport.Tor;

/// <summary>
/// Tor's control port refused a command, broke the protocol or closed the connection.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class TorControlException : Exception
{
    /// <summary>The reply that failed, when there was one.</summary>
    public TorControlReply? Reply { get; }

    public TorControlException(string message) : base(message) { }
    public TorControlException(string message, Exception innerException) : base(message, innerException) { }

    public TorControlException(string message, TorControlReply reply) : base($"{message}: {reply}")
    {
        Reply = reply;
    }
}
namespace NLightning.Infrastructure.Bitcoin.Gossip;

/// <summary>
/// The Esplora index could not give a proven txid (down, rate limited, not indexed yet, or a proof that does not match
/// our node's header). Transient: the funding output lookup answers <c>ChainUnavailable</c>.
/// </summary>
public sealed class EsploraUnavailableException : Exception
{
    public EsploraUnavailableException(string message) : base(message)
    {
    }

    public EsploraUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
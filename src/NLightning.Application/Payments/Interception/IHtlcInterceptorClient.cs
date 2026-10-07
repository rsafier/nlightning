namespace NLightning.Application.Payments.Interception;

using Domain.Payments.Interception;

/// <summary>The connected forward interceptor (an LND <c>HtlcInterceptor</c> stream, NL-1183).</summary>
public interface IHtlcInterceptorClient
{
    /// <summary>
    /// Queues <paramref name="forward"/> for the client without waiting; false when the client cannot take it (the
    /// forward stays held and is offered again when a client connects, as LND does).
    /// </summary>
    bool TryOffer(InterceptedForward forward);
}

/// <summary>The limits of forward interception (LND's defaults, NL-1183).</summary>
public sealed class HtlcInterceptorSettings
{
    /// <summary>Blocks before the incoming expiry at which a held forward fails back (LND: 19).</summary>
    public uint CltvRejectDelta { get; set; } = 19;

    /// <summary>A forward whose incoming HTLC expires within this many blocks is not held (LND: 22).</summary>
    public uint CltvInterceptDelta { get; set; } = 22;

    /// <summary>The most forwards held at once.</summary>
    public int MaxHeld { get; set; } = 1000;

    /// <summary>
    /// Forwards wait for an interceptor (LND's <c>requireinterceptor</c>, NL-1182): without a client a new forward is
    /// failed with <c>temporary_channel_failure</c> and a replayed one held; a disconnect keeps every hold.
    /// </summary>
    public bool RequireInterceptor { get; set; }
}

/// <summary>How <see cref="HtlcInterceptorHub.ResolveAsync"/> ended.</summary>
public enum InterceptResolveResult
{
    /// <summary>The resolution was carried out.</summary>
    Resolved,

    /// <summary>The operation failed; the forward remains held and expiry protection remains active.</summary>
    Failed,

    /// <summary>Another resolution is executing for this circuit.</summary>
    InProgress,

    /// <summary>No forward with that key is held (LND's <c>ErrFwdNotExists</c>).</summary>
    NotFound,

    /// <summary>A settle whose preimage does not match the payment hash; the forward stays held.</summary>
    PreimageMismatch,

    /// <summary>
    /// A resume or fail of a forward held on chain (its incoming channel is closing on chain): only a settle is possible
    /// (LND's <c>ErrCannotResumeOnChain</c>/<c>ErrCannotFailOnChain</c>, NL-1182); the forward stays held.
    /// </summary>
    NotAllowedOnChain
}
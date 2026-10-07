namespace NLightning.Domain.Payments.Interception;

/// <summary>What <see cref="IHtlcForwardInterceptor.Intercept"/> did with a forward (NL-1183).</summary>
public enum ForwardInterceptOutcome
{
    /// <summary>No interceptor: forward as usual.</summary>
    NotIntercepted,

    /// <summary>Held: the resolution arrives through the callback, nothing else is done with the HTLC now.</summary>
    Held,

    /// <summary>The incoming HTLC expires too soon to be held: fail it with <c>expiry_too_soon</c> (LND).</summary>
    ExpiryTooSoon,

    /// <summary>Too many forwards are held: fail it with <c>temporary_channel_failure</c>.</summary>
    Full
}

/// <summary>
/// The forward interception hook of the HTLC switch (LND's <c>InterceptableSwitch</c>, NL-1183). The switch offers every
/// HTLC it is about to forward, after the onion is peeled and before the outgoing channel and the forwarding policy are
/// looked at (so an interceptor also sees forwards to channels that do not exist yet, the JIT channel case). Final-hop
/// HTLCs are never offered (LND intercepts forwards only).
/// </summary>
/// <remarks>
/// Held forwards are memory only: after a restart the switch's replay offers them again (or forwards them when no
/// interceptor is connected), as LND does with <c>requireinterceptor</c> off. When the interceptor disconnects, every
/// held forward is resumed.
/// </remarks>
public interface IHtlcForwardInterceptor
{
    /// <summary>Whether an interceptor is connected (none: <see cref="Intercept"/> returns
    /// <see cref="ForwardInterceptOutcome.NotIntercepted"/>).</summary>
    bool IsActive { get; }

    /// <summary>
    /// Offers <paramref name="forward"/> at <paramref name="currentHeight"/>. When held, <paramref name="resolve"/> is
    /// called later, never concurrently, inline or under a lock of the interceptor; failed callbacks may be retried. A forward already held
    /// (a replay) stays held with its first callback and is not offered again.
    /// </summary>
    ForwardInterceptOutcome Intercept(InterceptedForward forward, uint currentHeight,
                                      Func<ForwardInterceptResolution, Task> resolve);
}
namespace NLightning.Domain.Payments.Interception;

using Channels.ValueObjects;

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
    Full,

    /// <summary>
    /// An interceptor is required and none is connected, and the forward is new (not a replay): fail it with
    /// <c>temporary_channel_failure</c> (LND's <c>requireinterceptor</c>, NL-1182).
    /// </summary>
    InterceptorRequired,

    /// <summary>
    /// The incoming HTLC's auto-fail height would not fit LND's signed 32-bit <c>auto_fail_height</c>: fail it with
    /// <c>expiry_too_far</c> (LND, NL-1182).
    /// </summary>
    ExpiryTooFar
}

/// <summary>
/// The forward interception hook of the HTLC switch (LND's <c>InterceptableSwitch</c>, NL-1183). The switch offers every
/// HTLC it is about to forward, after the onion is peeled and before the outgoing channel and the forwarding policy are
/// looked at (so an interceptor also sees forwards to channels that do not exist yet, the JIT channel case). Final-hop
/// HTLCs are never offered (LND intercepts forwards only).
/// </summary>
/// <remarks>
/// Held forwards are memory only: after a restart the switch's replay offers them again. With no interceptor connected a
/// replay is forwarded, as LND does with <c>requireinterceptor</c> off; with it on (NL-1182) a new forward is failed and a
/// replay is held until an interceptor connects. When the interceptor disconnects, every forward held off chain is
/// resumed unless an interceptor is required. A forward whose incoming channel is closing on chain is held on chain
/// (<see cref="InterceptOnChain"/>): settle only, kept across disconnects, dropped at its incoming expiry.
/// </remarks>
public interface IHtlcForwardInterceptor
{
    /// <summary>Whether an interceptor is connected (none: <see cref="Intercept"/> returns
    /// <see cref="ForwardInterceptOutcome.NotIntercepted"/>, unless one is required).</summary>
    bool IsActive { get; }

    /// <summary>Whether forwards wait for an interceptor even when none is connected (LND's
    /// <c>requireinterceptor</c>).</summary>
    bool IsRequired => false;

    /// <summary>
    /// Offers <paramref name="forward"/> at <paramref name="currentHeight"/>. When held, <paramref name="resolve"/> is
    /// called later, never concurrently, inline or under a lock of the interceptor; failed callbacks may be retried. A forward already held
    /// (a replay) stays held with its first callback and is not offered again. <paramref name="isReplay"/> says the
    /// switch handled this HTLC before (a restart's or a reconnection's replay): with an interceptor required and none
    /// connected a replay is held, a new forward refused.
    /// </summary>
    ForwardInterceptOutcome Intercept(InterceptedForward forward, uint currentHeight, bool isReplay,
                                      Func<ForwardInterceptResolution, Task> resolve);

    /// <summary>
    /// Holds <paramref name="forward"/>, whose incoming channel is closing on chain, for a settle (LND's on-chain
    /// interception, NL-1182): offered to a connected interceptor (now, or when one connects), replacing a hold of the
    /// same HTLC made off chain; only a settle is accepted; dropped once the chain reaches its incoming expiry. A
    /// forward already held on chain is left as it is. True when it is held.
    /// </summary>
    bool InterceptOnChain(InterceptedForward forward, Func<ForwardInterceptResolution, Task> resolve) => false;

    /// <summary>
    /// Forgets the forward held under the incoming HTLC <paramref name="incomingHtlcId"/> of
    /// <paramref name="incomingChannelId"/> when its HTLC is resolved elsewhere (LND's <c>RemoveOnChainIntercept</c>).
    /// </summary>
    void Release(ChannelId incomingChannelId, ulong incomingHtlcId)
    {
    }
}
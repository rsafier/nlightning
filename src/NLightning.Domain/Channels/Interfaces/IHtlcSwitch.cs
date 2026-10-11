namespace NLightning.Domain.Channels.Interfaces;

using Commitments.Events;

/// <summary>
/// Decides what happens to HTLCs once the commitment engine reports them (plan N6-T2, ONION M4): it receives every
/// <see cref="IChannelDomainEvent"/> after the transition that raised it is persisted.
/// </summary>
/// <remarks>
/// <para>
/// Expected behaviour per event:
/// <list type="bullet">
/// <item><see cref="IncomingHtlcLockedIn"/>: peel the onion, then fulfill (final hop), forward (offer the outgoing HTLC)
/// or fail the incoming HTLC. Never offer an outgoing HTLC before this event (B2-FWD-01).</item>
/// <item><see cref="OutgoingHtlcFulfilled"/>: fulfill the upstream HTLC at once with the preimage (B2-FWD-05).</item>
/// <item><see cref="OutgoingHtlcFailed"/>: fail the upstream HTLC; this is the only point where that is allowed
/// (B2-FWD-02).</item>
/// <item><see cref="OutgoingHtlcSettled"/>: prune the circuit / payment record.</item>
/// </list>
/// </para>
/// <para>
/// Events are replayed on startup from the persisted HTLC states
/// (<see cref="ChannelDomainEvents.DerivePending(Commitments.ChannelCommitments, IEnumerable{Commitments.HtlcRecord})"/>),
/// so an implementation must be idempotent: the same event can arrive more than once (for example, a locked-in HTLC
/// already forwarded must not be forwarded again, an upstream HTLC already fulfilled must not be fulfilled again).
/// It is called outside the channel's lock; to change a channel it goes through the channel operations, which take the
/// lock themselves.
/// </para>
/// <para>
/// <c>DerivePending</c> throws <see cref="ArgumentException"/> on a record in a legacy (pre-engine) state, so the
/// startup replay (N6-T2) must derive per channel and catch it (log and skip that channel), or filter legacy records
/// first; one legacy row must not stop the replay of the other channels.
/// </para>
/// </remarks>
public interface IHtlcSwitch
{
    /// <summary>Handles one event (see the remarks for what each one asks for).</summary>
    /// <param name="channelEvent">The event.</param>
    /// <param name="cancellationToken">Cancels the handling.</param>
    Task HandleAsync(IChannelDomainEvent channelEvent, CancellationToken cancellationToken);
}
namespace NLightning.Domain.Channels.Commitments.Events;

using ValueObjects;

/// <summary>
/// Something the commitment engine learned that the rest of the node must act on (plan N4-T4, §3.4 I8): an incoming
/// HTLC is locked in or settled, or an outgoing HTLC was fulfilled, irrevocably failed or settled.
/// </summary>
/// <remarks>
/// The engine returns events in <see cref="CommitmentsResult.Events"/>. The caller raises them only <b>after</b> the
/// transition that produced them is persisted (I8), in list order. Every event can be re-derived from the persisted HTLC
/// records with <see cref="ChannelDomainEvents.DerivePending(ChannelId, IEnumerable{HtlcRecord})"/>, so after a crash
/// between the save and the delivery the node replays them on startup. Consumers
/// (<see cref="NLightning.Domain.Channels.Interfaces.IHtlcSwitch"/>) must therefore be idempotent: an event can be
/// delivered more than once, never zero times.
/// </remarks>
public interface IChannelDomainEvent
{
    /// <summary>The channel the event belongs to.</summary>
    ChannelId ChannelId { get; }

    /// <summary>The HTLC id (the peer's id for an incoming HTLC, ours for an outgoing one).</summary>
    ulong HtlcId { get; }
}
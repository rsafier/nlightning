namespace NLightning.Domain.Payments.Interfaces;

using Channels.ValueObjects;
using Models;

/// <summary>
/// Stores forward circuits (incoming HTLC to outgoing HTLC), keyed by the incoming (channel, HTLC id).
/// </summary>
/// <remarks>
/// <para>Writes are staged: they reach the database with <c>IUnitOfWork.SaveChangesAsync</c>. The circuit is saved as
/// <c>Pending</c> before the outgoing HTLC is offered. <c>IChannelOperations.OfferHtlcAsync</c> saves the add (with
/// <c>HtlcOrigin.Forwarded</c>) in its own save and only then returns the HTLC id, so <c>Offered</c> is a second,
/// later save: after a crash in between, or when the downstream resolution is handled first, the circuit is still
/// <c>Pending</c> while its outgoing HTLC is live. The resolution is routed by the HTLC's origin
/// (<see cref="GetByIncomingAsync"/>) and applied with the outgoing HTLC supplied
/// (<c>ForwardCircuitModel.MarkFulfilled(channel, id, at)</c> / <c>MarkFailed(channel, id, at)</c>, valid from
/// <c>Pending</c>), in the same unit of work as the upstream fulfill or fail.</para>
/// <para>On startup the switch replays <see cref="GetUnresolvedAsync"/> against the persisted channel states
/// (ONION M4-T7). A <c>Pending</c> circuit may be failed upstream only after confirming that no channel HTLC carries
/// <c>HtlcOrigin.Forwarded(incomingChannelId, incomingHtlcId)</c>; if one does, record it with
/// <c>AddOutgoingHtlc</c> and treat the circuit as <c>Offered</c>.</para>
/// </remarks>
public interface IForwardCircuitDbRepository
{
    /// <summary>
    /// Stages a new circuit. The incoming (channel, HTLC id) must be new.
    /// </summary>
    Task AddAsync(ForwardCircuitModel circuit);

    /// <summary>
    /// Stages the circuit's mutable fields (status, outgoing channel and HTLC id, resolution time).
    /// </summary>
    Task UpdateAsync(ForwardCircuitModel circuit);

    /// <summary>
    /// The circuit that forwards incoming HTLC <paramref name="incomingHtlcId"/> of
    /// <paramref name="incomingChannelId"/>, or null.
    /// </summary>
    Task<ForwardCircuitModel?> GetByIncomingAsync(ChannelId incomingChannelId, ulong incomingHtlcId);

    /// <summary>
    /// The circuit whose outgoing HTLC is <paramref name="outgoingHtlcId"/> on <paramref name="outgoingChannelId"/>,
    /// or null.
    /// </summary>
    Task<ForwardCircuitModel?> GetByOutgoingAsync(ChannelId outgoingChannelId, ulong outgoingHtlcId);

    /// <summary>
    /// Circuits that are <c>Pending</c> or <c>Offered</c>.
    /// </summary>
    Task<IReadOnlyList<ForwardCircuitModel>> GetUnresolvedAsync();
}
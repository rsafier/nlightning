namespace NLightning.Domain.Payments.Interfaces;

using Channels.ValueObjects;
using Models;

/// <summary>
/// Stores forward circuits (incoming HTLC to outgoing HTLC), keyed by the incoming (channel, HTLC id).
/// </summary>
/// <remarks>
/// Writes are staged: they reach the database with <c>IUnitOfWork.SaveChangesAsync</c>. The circuit is saved as
/// <c>Pending</c> before the outgoing HTLC is offered; <c>Offered</c> is saved with the outgoing add; the resolution
/// is saved in the same unit of work as the upstream fulfill or fail. On startup the switch replays
/// <see cref="GetUnresolvedAsync"/> against the persisted channel states (ONION M4-T7).
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
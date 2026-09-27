namespace NLightning.Domain.Channels.Splicing.Models;

using Bitcoin.ValueObjects;
using Enums;
using ValueObjects;

/// <summary>
/// The outcome of <see cref="Interfaces.ISpliceService.StartAsync"/>: how far the splice got when the call returned.
/// </summary>
/// <param name="ChannelId">The spliced channel.</param>
/// <param name="State">The negotiation's state (<see cref="SpliceNegotiationState.Signed"/> once both
/// <c>tx_signatures</c> were exchanged).</param>
/// <param name="SpliceTxId">The splice transaction id, once constructed.</param>
/// <param name="NewCapacitySatoshis">The capacity of the new funding, once known.</param>
/// <param name="FailureReason">Why the splice did not go through (a <c>tx_abort</c> reason, a refused rule), or
/// null.</param>
public sealed record SpliceResult(
    ChannelId ChannelId,
    SpliceNegotiationState State,
    TxId? SpliceTxId = null,
    ulong? NewCapacitySatoshis = null,
    string? FailureReason = null);
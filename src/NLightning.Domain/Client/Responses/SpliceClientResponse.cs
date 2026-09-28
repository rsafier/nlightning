namespace NLightning.Domain.Client.Responses;

using Bitcoin.ValueObjects;
using Channels.Splicing.Enums;
using Channels.ValueObjects;

/// <summary>
/// The outcome of <c>splicein</c>/<c>spliceout</c> (<c>ClientCommand.SpliceIn</c>/<c>SpliceOut</c>, splicing plan
/// §3.10): the splice txid, the new capacity and how far the splice got. DTO shell of the SP1 contracts (lane SP1-E).
/// </summary>
public sealed class SpliceClientResponse
{
    public SpliceClientResponse(ChannelId channelId, SpliceNegotiationState state)
    {
        ChannelId = channelId;
        State = state;
    }

    public ChannelId ChannelId { get; }

    public SpliceNegotiationState State { get; }

    /// <summary>The splice transaction id (internal byte order), once constructed.</summary>
    public TxId? SpliceTxId { get; init; }

    /// <summary>The capacity of the new funding, once known.</summary>
    public ulong? NewCapacitySat { get; init; }

    /// <summary>Why the splice did not go through, or null.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Information about a splice that is not a failure, or null: a splice stopped at
    /// <see cref="SpliceNegotiationState.CommitmentSigned"/> names itself here and says it completes on the reconnection.
    /// </summary>
    public string? Note { get; init; }
}
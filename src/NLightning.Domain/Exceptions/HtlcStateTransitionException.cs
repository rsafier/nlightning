using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

using Channels.Commitments;
using Channels.Enums;

/// <summary>
/// Thrown by <see cref="HtlcStateTable.Next"/> when an event is not legal in the current HTLC state.
/// </summary>
/// <remarks>
/// Peer-driven input is validated before any transition, so reaching this means the engine or its caller has a bug. It
/// is a <see cref="ChannelErrorException"/> because the channel state can no longer be trusted.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class HtlcStateTransitionException : ChannelErrorException
{
    public HtlcState State { get; }
    public HtlcEvent Event { get; }

    public HtlcStateTransitionException(HtlcState state, HtlcEvent htlcEvent)
        : base($"Illegal HTLC state transition: {htlcEvent} in state {state}", "Internal error")
    {
        State = state;
        Event = htlcEvent;
    }
}
namespace NLightning.Domain.Channels.Quiescence;

/// <summary>
/// The quiescence sub-state of one <c>Open</c> channel (BOLT 2 "Channel Quiescence"; splicing plan §3.2).
/// </summary>
/// <remarks>
/// <para>In memory only, owned by the <see cref="IQuiescenceService"/> implementation and changed only under the
/// channel's lock; never a <c>ChannelState</c> and never persisted (splicing plan D1): a disconnection resets it to
/// <see cref="None"/> (Q-R-04).</para>
/// <para>The phases: <see cref="None"/> → quiescing (<see cref="PendingRequest"/> queued and/or one <c>stfu</c> sent
/// or received) → quiescent (both <c>stfu</c> exchanged, <see cref="Initiator"/> set) → <see cref="None"/>. While
/// <see cref="BlocksNewLocalUpdates"/> we propose no new update (<c>update_*</c>); pending changes are still signed
/// and revoked, which drains the channel so our <c>stfu</c> can be sent (Q-S-02, Q-R-02).</para>
/// <para>This record is data only; the transitions are <c>QuiescenceRules</c> (lane Q-A) and the service (lane
/// Q-B).</para>
/// </remarks>
public sealed record QuiescenceState
{
    /// <summary>No quiescence in progress.</summary>
    public static QuiescenceState None { get; } = new();

    /// <summary>
    /// Our own request (<see cref="IQuiescenceService.RequestAsync"/>) queued until <c>stfu</c> may be sent (Q-S-01,
    /// Q-S-02), or null for none. It stays set after our <c>stfu</c> is sent, so the dependent protocol is known once
    /// quiescent.
    /// </summary>
    public QuiescencePurpose? PendingRequest { get; init; }

    /// <summary>
    /// The <c>initiator</c> flag of the <c>stfu</c> we sent, or null if we sent none. BOLT 2: 1 when we sent it first,
    /// 0 when it replies to the peer's (Q-S-03). We never send a second one.
    /// </summary>
    public bool? SentStfuInitiator { get; init; }

    /// <summary>
    /// The <c>initiator</c> flag of the <c>stfu</c> the peer sent, or null if it sent none. A second <c>stfu</c> from
    /// the peer is a protocol violation (Q-S-03).
    /// </summary>
    public bool? ReceivedStfuInitiator { get; init; }

    /// <summary>
    /// Who is the initiator; set when the channel becomes quiescent (Q-R-01, Q-R-05), null before.
    /// </summary>
    public QuiescenceInitiator? Initiator { get; init; }

    /// <summary>
    /// When the channel became quiescent (for the Q-R-03 timeout), null before.
    /// </summary>
    public DateTimeOffset? QuiescentSince { get; init; }

    /// <summary>Whether we sent our <c>stfu</c>.</summary>
    public bool StfuSent => SentStfuInitiator.HasValue;

    /// <summary>Whether the peer sent its <c>stfu</c>; from then on it must not send any update (Q-S-04).</summary>
    public bool StfuReceived => ReceivedStfuInitiator.HasValue;

    /// <summary>Both <c>stfu</c> were exchanged: the channel is quiescent (Q-R-01).</summary>
    public bool IsQuiescent => StfuSent && StfuReceived;

    /// <summary>A request is queued or one <c>stfu</c> was exchanged, but the channel is not quiescent yet.</summary>
    public bool IsQuiescing => !IsQuiescent && (StfuSent || StfuReceived || PendingRequest.HasValue);

    /// <summary>
    /// Whether we must not propose a new update: after our <c>stfu</c> (MUST, Q-S-04), after the peer's (SHOULD NOT,
    /// Q-R-02) and while our own request is queued (splicing plan §3.2, so the channel drains).
    /// </summary>
    public bool BlocksNewLocalUpdates => IsQuiescing || IsQuiescent;
}
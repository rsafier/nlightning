namespace NLightning.Domain.Channels.Quiescence;

/// <summary>
/// Why we may not send our <c>stfu</c> now (<see cref="QuiescenceRules.CheckSend"/>), or <see cref="None"/> when we
/// may.
/// </summary>
public enum StfuSendBlocker : byte
{
    /// <summary>A <c>stfu</c> is owed and BOLT 2 allows sending it now.</summary>
    None = 0,

    /// <summary>Nothing is owed: no request of ours is queued and the peer sent no <c>stfu</c>.</summary>
    NothingOwed = 1,

    /// <summary><c>option_quiesce</c> is not negotiated with the peer (Q-S-01).</summary>
    NotNegotiated = 2,

    /// <summary>We already sent our <c>stfu</c> on this connection (Q-S-03: never twice).</summary>
    AlreadySent = 3,

    /// <summary>
    /// The channel is not <c>Open</c> or not reestablished on this connection, so no <c>stfu</c> can go out yet.
    /// </summary>
    LinkNotReady = 4,

    /// <summary>
    /// One of our HTLC additions, HTLC removals or fee updates is still pending for either peer (Q-S-02); it goes
    /// away once our pending changes are committed and revoked both ways.
    /// </summary>
    LocalUpdatesPending = 5
}
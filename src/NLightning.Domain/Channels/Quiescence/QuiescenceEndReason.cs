namespace NLightning.Domain.Channels.Quiescence;

/// <summary>
/// Why a channel stopped being quiescing or quiescent (BOLT 2 "Channel Quiescence": Q-R-04 and Q-R-06).
/// </summary>
public enum QuiescenceEndReason : byte
{
    /// <summary>The connection closed: the channel is no longer quiescent (Q-R-04).</summary>
    Disconnected = 1,

    /// <summary>The dependent protocol finished: both <c>tx_signatures</c> were exchanged (SP-Q-01).</summary>
    TxSignaturesExchanged = 2,

    /// <summary>
    /// <c>tx_abort</c> was sent and received (SP-Q-01; for <see cref="QuiescencePurpose.Probe"/> it is the only exit
    /// short of a disconnect, splicing plan D2).
    /// </summary>
    TxAbort = 3,

    /// <summary>
    /// The quiescence timeout fired (Q-R-03, <c>Node:Quiescence:Timeout</c>/<c>IdleTimeout</c>); the connection is
    /// closed right after.
    /// </summary>
    Timeout = 4,

    /// <summary>
    /// Our queued request was withdrawn before we sent <c>stfu</c> (the caller cancelled). A <c>stfu</c> already sent
    /// can't be withdrawn: after it only the other reasons end quiescence.
    /// </summary>
    RequestCancelled = 5
}
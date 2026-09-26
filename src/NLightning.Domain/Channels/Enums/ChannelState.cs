namespace NLightning.Domain.Channels.Enums;

public enum ChannelState : byte
{
    None = 0,
    V1Opening = 1,
    V1FundingCreated = 2,
    V1FundingSigned = 3,
    V2Opening = 10,
    ReadyForThem = 20,
    ReadyForUs = 21,
    Open = 22,

    /// <summary>
    /// A <c>shutdown</c> was sent or received (BOLT 2 "Closing Initiation"): no new HTLC is added in either direction,
    /// the ones in flight are still fulfilled or failed, and the channel waits until none is left.
    /// </summary>
    ShuttingDown = 23,

    /// <summary>
    /// Both <c>shutdown</c>s were exchanged and no HTLC or update is left: the legacy <c>closing_signed</c> fee
    /// negotiation runs (restarted on every reconnection, BOLT 2). No update is sent or accepted any more.
    /// </summary>
    Negotiating = 25,

    /// <summary>
    /// The mutual close transaction was agreed, fully signed, persisted and broadcast; it waits for its confirmation,
    /// then the channel is <see cref="Closed"/>.
    /// </summary>
    Closing = 30,

    /// <summary>
    /// The channel was failed (BOLT 1/2 "fail the channel"): we sent, or must send, an <c>error</c> naming it.
    /// </summary>
    /// <remarks>
    /// Terminal for updates: no <c>update_*</c>, <c>commitment_signed</c> or <c>revoke_and_ack</c> is sent or accepted
    /// afterwards, and the <c>error</c> is re-sent when the peer reconnects (B2-RE-05). It sits below
    /// <see cref="Closed"/> because <c>ChannelModel.UpdateState</c> only moves forward: a failed channel still closes
    /// once its commitment transaction confirms (BOLT2 plan N6-T3, D10, D11). Data loss is a separate persisted flag,
    /// not a state.
    /// </remarks>
    Failed = 35,

    Closed = 40,
    Stale = 50
}
namespace NLightning.Domain.Channels.Enums;

/// <summary>
/// Per-HTLC state, persisted as one byte.
/// </summary>
/// <remarks>
/// <para>
/// Values 0-3 are the legacy states written before the commitment state machine existed. They stay decodable so old
/// rows can be read and mapped by the N5 migration; the state machine
/// (<see cref="Commitments.HtlcStateTable"/>) rejects them.
/// </para>
/// <para>
/// Values 10-19 (we offered) and 30-39 (they offered) are core-lightning's <c>htlc_state</c>, which maps 1:1 to the five
/// BOLT 2 update stages. "Sent"/"Rcvd" name the last message that moved the HTLC; transitions are driven only by
/// <see cref="Commitments.HtlcEvent"/> through <see cref="Commitments.HtlcStateTable.Next"/>.
/// </para>
/// </remarks>
public enum HtlcState : byte
{
    // Legacy (pre state machine) values.
    Offered = 0,
    Fulfilled = 1,
    Failed = 2,
    Expired = 3,

    // We offered the HTLC.
    SentAddHtlc = 10,
    SentAddCommit = 11,
    RcvdAddRevocation = 12,
    RcvdAddAckCommit = 13,
    SentAddAckRevocation = 14,
    RcvdRemoveHtlc = 15,
    RcvdRemoveCommit = 16,
    SentRemoveRevocation = 17,
    SentRemoveAckCommit = 18,
    RcvdRemoveAckRevocation = 19,

    // They offered the HTLC.
    RcvdAddHtlc = 30,
    RcvdAddCommit = 31,
    SentAddRevocation = 32,
    SentAddAckCommit = 33,
    RcvdAddAckRevocation = 34,
    SentRemoveHtlc = 35,
    SentRemoveCommit = 36,
    RcvdRemoveRevocation = 37,
    RcvdRemoveAckCommit = 38,
    SentRemoveAckRevocation = 39,
}